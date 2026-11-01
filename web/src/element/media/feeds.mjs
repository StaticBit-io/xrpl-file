// Feeds: what to append to a `SourceBuffer`, and from which block to read, for each container.
//
// A feed sits between the verified blocks of a file and Media Source Extensions. It answers three
// questions: what the initialization segments are, which byte to start reading from to play from a
// moment (the keyframe at or before it), and what media segments the bytes read from there make.
//
//  - Fragmented MP4 and WebM already are segments: the bytes are appended as they are, and seeking
//    is a lookup in the `sidx` box (MP4) or the Cues element (WebM).
//  - A plain MP4 keeps its samples in one `mdat` and its index in `moov`, which MSE cannot take.
//    mp4box.js (0.5.4) cuts it into fragments in the visitor's browser. mp4box's `seek` works in
//    composition time and does not reset a segmentation that is already running, so every seek
//    starts a fresh `ISOFile` and reads from the offset that `ISOFile` returns (Spec question 15).
//
// New in this repository; it uses the readers of boxes.mjs, movie.mjs, sidx.mjs and webm.mjs.

import { locateMoov, readBox, topLevelBoxes } from './boxes.mjs';
import { mimeFor, parseMovie } from './movie.mjs';
import { groupAt, parseSidx } from './sidx.mjs';
import { cueAt, locateCues, parseCues, parseWebmHead, webmMime } from './webm.mjs';

/**
 * @typedef {object} Segment
 * @property {number} track  an id from `Feed.tracks`
 * @property {Uint8Array} bytes  for fragmented MP4 and WebM this can be a view of the block that was passed to `media`
 */

/**
 * @typedef {object} SeekResult
 * @property {number} block  the block to start reading from
 * @property {number} offset  the file offset the next `media` call starts at; inside `block`
 * @property {number} time  seconds: where playback can start, at or before the time asked for (the earliest start over the tracks)
 */

/**
 * @typedef {object} Feed
 * @property {'fmp4' | 'mp4' | 'webm'} kind
 * @property {{ id: number, mime: string }[]} tracks  one `SourceBuffer` each; `id` is what `Segment.track` carries. `mime` can be refused by `isTypeSupported`
 * @property {number} duration  seconds; 0 when the file does not say
 * @property {boolean} seekable  whether `seekTo` can answer for a time other than 0
 * @property {() => Promise<Segment[]>} init  the initialization segments, one per track
 * @property {(seconds: number) => Promise<SeekResult | null>} seekTo  where to read from to play from `seconds`, and the position the next `media` calls continue from. `seekTo(0)` starts playback. null when the file cannot seek there
 * @property {(block: number, bytes: Uint8Array, from?: number) => Promise<Segment[]>} media  the segments that block `block` completes, in order; `from` (a file offset inside the block, default its start) is where the bytes the caller wants to use begin, as `seekTo` answered
 * @property {() => void} close
 */

// Boxes between `moov` and a `sidx` that are walked over (free, skip, uuid); a file with more has no
// index worth waiting for.
const MAX_BOXES_BEFORE_SIDX = 4;
// A `sidx` holds at most 65,535 references of 12 bytes. Anything larger is not an index.
const MAX_SIDX_BYTES = 1 << 20;
// The most that may sit between `moov` and the end of its `sidx`, padding included.
const MAX_SIDX_SPAN_BYTES = 2 * MAX_SIDX_BYTES;
// A fragmented file starts with `ftyp` (and perhaps padding) before `moov`. Reading far past that to
// build an initialization segment would download a file's worth of data before the first frame.
const MAX_PREFIX_BYTES = 1 << 20;
// mp4box emits one segment per this many samples of a track (and the rest on `flush`).
const SEGMENT_SAMPLES = 100;

const EMPTY = new Uint8Array(0);

function concat(parts) {
  const out = new Uint8Array(parts.reduce((sum, part) => sum + part.length, 0));
  let at = 0;
  for (const part of parts) {
    out.set(part, at);
    at += part.length;
  }
  return out;
}

/**
 * Block reads for a feed: block 0 is the caller's `head` when that is the whole block, the two most
 * recent other blocks are kept so that neighbouring lookups (the `moov`, the `sidx` behind it) do
 * not read a block twice, and an index outside the file gives an empty block instead of a read.
 *
 * The last case is not hypothetical: `locateCues` opens with a 12-byte read, and a Cues element
 * that ends exactly where a block does makes that read ask for the block after the last one.
 */
function blockSource({ head, readBlock, blockBytes, size }) {
  const total = Math.ceil(size / blockBytes);
  const headIsBlock0 = head instanceof Uint8Array && head.length === Math.min(blockBytes, size);
  const recent = new Map();
  let failure = null;

  async function block(index) {
    if (!Number.isInteger(index) || index < 0 || index >= total) return EMPTY;
    if (index === 0 && headIsBlock0) return head;

    const cached = recent.get(index);
    if (cached) return cached;

    let bytes;
    try {
      bytes = await readBlock(index);
    } catch (error) {
      failure = error;
      throw error;
    }

    const expected = Math.min(blockBytes, size - index * blockBytes);
    if (bytes.length !== expected) throw new Error(`block ${index} has ${bytes.length} bytes, expected ${expected}`);

    recent.set(index, bytes);
    if (recent.size > 2) recent.delete(recent.keys().next().value);
    return bytes;
  }

  /** `[from, to)` of the file, cut at its end. */
  async function range(from, to) {
    const end = Math.min(to, size);
    if (end <= from) return EMPTY;

    const out = new Uint8Array(end - from);
    for (let index = Math.floor(from / blockBytes); index <= Math.floor((end - 1) / blockBytes); index++) {
      const bytes = await block(index);
      const blockStart = index * blockBytes;
      const copyFrom = Math.max(from, blockStart);
      const copyTo = Math.min(end, blockStart + bytes.length);
      out.set(bytes.subarray(copyFrom - blockStart, copyTo - blockStart), copyFrom - from);
    }

    return out;
  }

  return { block, range, total, lastFailure: () => failure };
}

/**
 * Runs an optional lookup (the `sidx`, the Cues): malformed bytes mean the file has none, which is
 * not an error, but a block that could not be read or verified is, and passes through unchanged.
 */
async function optional(source, lookup) {
  try {
    return await lookup();
  } catch (error) {
    if (error === source.lastFailure()) throw error;
    return null;
  }
}

/** Builds seek answers that stay inside the file, whatever offset an index claims. */
function seekAnswer(source, blockBytes, size) {
  return (offset, time) => {
    const at = Math.min(Math.max(offset, 0), size);
    return { block: Math.min(Math.floor(at / blockBytes), source.total - 1), offset: at, time };
  };
}

/** Segments that are the block's own bytes, for containers MSE takes as they are. */
function passThrough(blockBytes) {
  return async (block, bytes, from = block * blockBytes) => {
    const skip = Math.min(Math.max(from - block * blockBytes, 0), bytes.length);
    if (skip >= bytes.length) return [];
    return [{ track: 0, bytes: skip === 0 ? bytes : bytes.subarray(skip) }];
  };
}

function seconds(value) {
  return Number.isNaN(value) || value < 0 ? 0 : value;
}

/**
 * The `sidx` after `moov`: where it ends, the bytes from the end of `moov` to there (padding boxes
 * included, so they can join the initialization segment), and its groups with file offsets.
 */
async function readSidx(source, from, size) {
  let at = from;

  for (let step = 0; step < MAX_BOXES_BEFORE_SIDX && at + 8 <= size; step++) {
    const box = readBox(await source.range(at, at + 16), 0, size - at);
    if (box === null || box.end - from > MAX_SIDX_SPAN_BYTES) return null;

    if (box.type === 'sidx') {
      if (box.size > MAX_SIDX_BYTES || at + box.size > size) return null;

      const bytes = await source.range(from, at + box.size);
      const index = parseSidx(bytes, at - from);
      return { end: at + box.size, bytes, groups: index.groups.map((group) => ({ ...group, offset: group.offset + from })) };
    }

    if (box.type === 'moof' || box.type === 'mdat') return null;
    at = box.end;
  }

  return null;
}

async function openFragmented({ source, located, movie, blockBytes, size }) {
  if (located.offset > MAX_PREFIX_BYTES) {
    throw new Error(`a fragmented file's moov starts ${located.offset} bytes in, past the ${MAX_PREFIX_BYTES} that are read to build its initialization segment`);
  }

  const moovEnd = located.offset + located.bytes.length;
  const sidx = await optional(source, () => readSidx(source, moovEnd, size));
  const groups = sidx?.groups.filter((group) => Number.isFinite(group.startTime)) ?? [];

  const initBytes = concat([await source.range(0, located.offset), located.bytes, sidx?.bytes ?? EMPTY]);
  const last = groups[groups.length - 1];
  const duration = movie.duration || (last ? last.startTime + last.duration : 0);
  const answer = seekAnswer(source, blockBytes, size);
  const initEnd = moovEnd + (sidx ? sidx.bytes.length : 0);

  return {
    kind: 'fmp4',
    tracks: [{ id: 0, mime: mimeFor(movie) }],
    duration,
    seekable: groups.length > 0,

    async init() {
      return [{ track: 0, bytes: initBytes }];
    },

    async seekTo(target) {
      const t = seconds(target);
      if (groups.length > 0) {
        const group = groups[groupAt(groups, t)];
        return answer(group.offset, group.startTime);
      }

      return t === 0 ? answer(initEnd, 0) : null;
    },

    media: passThrough(blockBytes),
    close() {},
  };
}

async function openWebm({ source, blockBytes, size }) {
  const first = await source.block(0);
  const head = parseWebmHead(first);
  const cues = await optional(source, async () => {
    const element = await locateCues(source.block, blockBytes, head);
    return element === null ? null : parseCues(element, head.segmentStart, head.timecodeScale);
  }) ?? [];

  const initBytes = first.subarray(0, head.initEnd);
  const answer = seekAnswer(source, blockBytes, size);

  return {
    kind: 'webm',
    tracks: [{ id: 0, mime: webmMime(head) }],
    duration: Number.isFinite(head.duration) ? head.duration : 0,
    seekable: cues.length > 0,

    async init() {
      return [{ track: 0, bytes: initBytes }];
    },

    async seekTo(target) {
      const t = seconds(target);
      if (t === 0) return answer(head.initEnd, 0);
      if (cues.length === 0) return null;

      const cue = cues[cueAt(cues, t)];
      // Before the first cue is the start of the file, as it is for a request for 0.
      return cue.time > t ? answer(head.initEnd, 0) : answer(cue.offset, cue.time);
    },

    media: passThrough(blockBytes),
    close() {},
  };
}

/** A copy of the bytes with the file position mp4box wants as a property of the buffer. */
function mp4boxBuffer(bytes, fileStart) {
  const copy = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
  copy.fileStart = fileStart;
  return copy;
}

function mp4boxFailure(what, error) {
  // mp4box throws bare strings from some paths.
  return new Error(`mp4box could not ${what}: ${error?.message ?? error}`);
}

/** The codec of an mp4box track as a safe MIME parameter value, or null for tracks MSE does not take. */
function trackMime(track) {
  if (track.type !== 'video' && track.type !== 'audio') return null;

  const codec = String(track.codec ?? '').replace(/[^A-Za-z0-9._-]/g, '');
  return codec === '' ? null : `${track.type}/mp4; codecs="${codec}"`;
}

/**
 * Where to tell mp4box the `moov` sits. In the file it can follow other boxes (`free`, a second
 * `mdat`) that are never read; mp4box only goes on parsing from the end of the box that ran past
 * what it was given, so it is told the `moov` begins right there. Sample offsets are positions in the
 * whole file and do not depend on where the `moov` itself is, so the answer to a seek is unchanged.
 */
function moovPosition(block0, moov) {
  if (moov.offset < block0.length) return moov.offset;

  const boxes = topLevelBoxes(block0);
  const last = boxes[boxes.length - 1];
  return last && !last.partial && last.end > block0.length && last.end <= moov.offset ? last.end : moov.offset;
}

/**
 * A new `ISOFile` that has read what a plain MP4 needs before it can cut samples: block 0 (`ftyp`,
 * and the `mdat` header, without which mp4box cannot skip over the media data to a `moov` at the end)
 * and the `moov`.
 */
function startSession(MP4Box, block0, moov) {
  const file = MP4Box.createFile();
  const session = { file, info: null, tracks: [], init: [], pending: [], error: null, flushed: false };

  file.onError = (error) => {
    session.error ??= mp4boxFailure('read the file', error);
  };

  file.onReady = (info) => {
    session.info = info;

    for (const track of info.tracks) {
      const mime = trackMime(track);
      if (mime === null) continue;

      session.tracks.push({ id: track.id, mime });
      file.setSegmentOptions(track.id, null, { nbSamples: SEGMENT_SAMPLES });
    }

    for (const segment of file.initializeSegmentation()) {
      session.init.push({ track: segment.id, bytes: new Uint8Array(segment.buffer) });
    }
  };

  file.onSegment = (id, user, buffer, sampleNumber) => {
    session.pending.push({ track: id, bytes: new Uint8Array(buffer) });
    file.releaseUsedSamples(id, sampleNumber);
  };

  try {
    file.appendBuffer(mp4boxBuffer(block0, 0));

    // A moov that begins inside block 0 has had its start appended with it; only the rest follows.
    const at = moovPosition(block0, moov);
    const skip = Math.max(block0.length - at, 0);
    if (skip < moov.bytes.length) file.appendBuffer(mp4boxBuffer(moov.bytes.subarray(skip), at + skip));
  } catch (error) {
    throw mp4boxFailure('read the movie header', error);
  }

  if (session.error) throw session.error;
  if (session.info === null) throw new Error('mp4box could not read the movie header');
  if (session.tracks.length === 0) throw new Error('the file has no video or audio track the browser can play');

  return session;
}

async function openPlain({ source, located, movie, blockBytes, size, loadMp4Box }) {
  if (typeof loadMp4Box !== 'function') throw new TypeError('opening a plain MP4 needs loadMp4Box');

  const MP4Box = await loadMp4Box();
  const block0 = await source.block(0);

  // Parsed once here for the track list and the initialization segments; the first seek starts from
  // this same instance, later ones from a new one.
  let spare = startSession(MP4Box, block0, located);
  const { tracks, init } = spare;
  let current = null;
  let closed = false;

  // Time past the end of every track makes mp4box answer with no position at all.
  const reach = Math.max(0, ...movie.tracks.filter((t) => t.table !== null && t.timescale > 0).map((t) => t.table.endTicks / t.timescale));
  const answer = seekAnswer(source, blockBytes, size);
  const lastBlock = source.total - 1;

  return {
    kind: 'mp4',
    tracks,
    duration: movie.duration || reach,
    seekable: reach > 0,

    async init() {
      return init;
    },

    async seekTo(target) {
      if (closed) return null;

      const session = spare ?? startSession(MP4Box, block0, located);
      spare = null;
      current?.file.stop?.();
      current = null;

      let position;
      try {
        position = session.file.seek(Math.min(seconds(target), reach), true);
        session.file.start();
      } catch (error) {
        throw mp4boxFailure('seek', error);
      }

      if (session.error) throw session.error;
      if (!Number.isFinite(position.offset) || position.offset < 0) throw new Error('mp4box found no sample to start from');

      current = session;
      return answer(position.offset, position.time);
    },

    async media(block, bytes, from = block * blockBytes) {
      const session = current;
      if (closed || session === null) return [];

      const blockStart = block * blockBytes;
      const skip = Math.min(Math.max(from - blockStart, 0), bytes.length);

      try {
        if (skip < bytes.length) session.file.appendBuffer(mp4boxBuffer(bytes.subarray(skip), blockStart + skip));

        if (block >= lastBlock && !session.flushed) {
          session.flushed = true;
          session.file.flush();
        }
      } catch (error) {
        throw mp4boxFailure('cut the media data', error);
      }

      if (session.error) throw session.error;
      return session.pending.splice(0);
    },

    close() {
      closed = true;
      current?.file.stop?.();
      current = spare = null;
    },
  };
}

/**
 * Opens a file for playback.
 *
 * Reads what the container needs before the first frame: for an ISO file the blocks that hold
 * `moov` (and `sidx`, for a fragmented one); for WebM the block that holds the Cues. Nothing else
 * is read, and block 0 is not read again when `head` is all of it.
 *
 * @param {object} options
 * @param {'iso' | 'webm'} options.kind  from `detectContainer`
 * @param {Uint8Array} options.head  block 0 of the file; WebM needs all of the initialization segment in it
 * @param {(index: number) => Promise<Uint8Array>} options.readBlock  a verified block, `blockBytes` long (the last may be shorter)
 * @param {number} options.blockBytes
 * @param {number} options.size  file size in bytes
 * @param {() => Promise<any>} [options.loadMp4Box]  resolves the `MP4Box` namespace; called once, and only for a plain MP4
 * @returns {Promise<Feed>}
 * @throws {Error} when the container is damaged or has nothing playable; any error a block read throws, unchanged
 */
export async function openFeed({ kind, head, readBlock, blockBytes, size, loadMp4Box }) {
  if (kind !== 'iso' && kind !== 'webm') throw new TypeError(`no feed for a container of kind "${kind}"`);
  if (!Number.isInteger(blockBytes) || blockBytes <= 0) throw new TypeError('blockBytes must be a positive integer');
  if (!Number.isSafeInteger(size) || size <= 0) throw new TypeError('size must be a positive integer');

  const source = blockSource({ head, readBlock, blockBytes, size });

  if (kind === 'webm') return openWebm({ source, blockBytes, size });

  const located = await locateMoov(source.block, blockBytes, size);
  const movie = parseMovie(located.bytes, { fileSize: size });
  const context = { source, located, movie, blockBytes, size, loadMp4Box };

  return movie.fragmented ? openFragmented(context) : openPlain(context);
}
