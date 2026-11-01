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
import { cueAt, locateCues, orderClusterBlocks, parseCues, parseWebmHead, readTopLevel, webmMime } from './webm.mjs';

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
 * The `media` of a WebM feed: whole Clusters with their blocks in timecode order.
 *
 * Media Source takes a Cluster only when its block timecodes never go backwards, and a muxed VP9 and
 * Opus file's do (see `orderClusterBlocks`). A Cluster is re-ordered once the blocks that hold it have
 * verified, so a Cluster that runs past the block in hand is carried to the next `media` call, and
 * what is appended is a list of complete Clusters. The carried bytes are one Cluster at most
 * (`MAX_CLUSTER_BYTES`): `readTopLevel` refuses a larger claim, and an element that is not a Cluster
 * is discarded as it goes by instead of being kept.
 *
 * Elements between and after the Clusters (Cues, Tags, Void) are dropped, not appended: Media Source
 * ignores them, the Cues are already read for seeking, and dropping them keeps every segment a Cluster.
 *
 * A read that does not continue the previous one (the first of a generation, a seek, a retry) starts
 * afresh; so does `reset`, which `seekTo` calls. The player only reads in file order inside a
 * generation, and the offset check keeps a stale tail from ever being glued to bytes it did not precede.
 */
function webmMedia({ blockBytes, lastBlock }) {
  let buffer = EMPTY;
  let length = 0;
  let discard = 0;
  let next = -1;
  let hint = { need: 0, resume: 0 };
  let closed = false;

  function reset() {
    buffer = EMPTY;
    length = 0;
    discard = 0;
    next = -1;
    hint = { need: 0, resume: 0 };
  }

  function append(bytes) {
    if (length + bytes.length > buffer.length) {
      const grown = new Uint8Array(Math.max(length + bytes.length, buffer.length * 2));
      grown.set(buffer.subarray(0, length));
      buffer = grown;
    }

    buffer.set(bytes, length);
    length += bytes.length;
  }

  /** Keeps `data[at..]`, which is a view of the carried buffer or of the block just read. */
  function carry(data, at, owned) {
    const rest = data.length - at;
    if (rest === 0) {
      buffer = EMPTY;
      length = 0;
      return;
    }

    if (owned) {
      if (at > 0) buffer.copyWithin(0, at, data.length);
    } else {
      buffer = new Uint8Array(rest);
      buffer.set(data.subarray(at));
    }
    length = rest;
  }

  function cut(data, at) {
    let offset = at;
    if (discard > 0) {
      const dropped = Math.min(discard, data.length);
      discard -= dropped;
      offset += dropped;
    }
    return offset;
  }

  function cluster(data, from, top, owned) {
    // Bytes of the carried buffer are overwritten by the next read, so a Cluster leaves it as a copy.
    const bytes = owned ? data.slice(from, top.end) : data.subarray(from, top.end);
    return { track: 0, bytes: top.truncated ? bytes : orderClusterBlocks(bytes) };
  }

  async function media(block, bytes, from = block * blockBytes) {
    if (closed) return [];

    const blockStart = block * blockBytes;
    const start = Math.min(Math.max(from, blockStart), blockStart + bytes.length);
    const fresh = bytes.subarray(start - blockStart);
    if (start !== next) reset();
    next = start + fresh.length;

    const last = block >= lastBlock;
    const owned = length > 0;
    if (owned) {
      append(fresh);
      if (!last && length < hint.need) return [];
    }

    const data = owned ? buffer.subarray(0, length) : fresh;
    const segments = [];

    try {
      let at = cut(data, 0);
      let resume = owned ? hint.resume : 0;

      while (at < data.length) {
        const top = readTopLevel(data, at, last, resume);
        resume = 0;

        if (top.kind === 'more') {
          hint = { need: top.need - at, resume: top.resume };
          break;
        }

        if (top.kind === 'skip') {
          discard = Math.max(top.end - data.length, 0);
          at = Math.min(top.end, data.length);
        } else {
          segments.push(cluster(data, at, top, owned));
          at = top.end;
        }
      }

      carry(data, at, owned);
    } catch (error) {
      reset();
      throw error;
    }

    if (length === 0) hint = { need: 0, resume: 0 };
    if (last) reset();
    return segments;
  }

  return {
    media,
    reset,
    close() {
      closed = true;
      reset();
    },
  };
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
  const clusters = webmMedia({ blockBytes, lastBlock: source.total - 1 });

  return {
    kind: 'webm',
    tracks: [{ id: 0, mime: webmMime(head) }],
    duration: Number.isFinite(head.duration) ? head.duration : 0,
    seekable: cues.length > 0,

    async init() {
      return [{ track: 0, bytes: initBytes }];
    },

    async seekTo(target) {
      // A new generation begins at a Cluster, so what was carried from the last one is of no use.
      clusters.reset();

      const t = seconds(target);
      if (t === 0) return answer(head.initEnd, 0);
      if (cues.length === 0) return null;

      const cue = cues[cueAt(cues, t)];
      // Before the first cue is the start of the file, as it is for a request for 0.
      return cue.time > t ? answer(head.initEnd, 0) : answer(cue.offset, cue.time);
    },

    media: clusters.media,
    close: clusters.close,
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

/**
 * The codec of an mp4box track as a safe MIME parameter value, or null for tracks MSE does not take.
 *
 * mp4box gives a bare `mp4a` for an audio entry in QuickTime's layout, where the `esds` sits inside a
 * `wave` box it does not read; the codec movie.mjs read from the same entry (`mp4a.40.2`) is used then.
 */
function trackMime(track, parsedCodec) {
  if (track.type !== 'video' && track.type !== 'audio') return null;

  let raw = String(track.codec ?? '');
  if (!raw.includes('.') && parsedCodec?.startsWith(`${raw}.`)) raw = parsedCodec;

  const codec = raw.replace(/[^A-Za-z0-9._-]/g, '');
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
function startSession(MP4Box, block0, moov, codecs) {
  const file = MP4Box.createFile();
  const session = { file, info: null, tracks: [], init: [], pending: [], error: null, flushed: false };

  file.onError = (error) => {
    session.error ??= mp4boxFailure('read the file', error);
  };

  file.onReady = (info) => {
    session.info = info;

    for (const track of info.tracks) {
      const mime = trackMime(track, codecs.get(track.id));
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

/**
 * Where to read from to play from `t`: mp4box's `ISOFile.seek(t, true)`, restricted to the tracks that
 * are segmented. `seek` itself takes the least offset and time over every `trak`, so a `tmcd` timecode
 * or `gpmd` metadata track (one early sample) would answer every seek with the start of the file.
 * The steps are those of `seek`: a track the time is past the end of is left out, the rest answer
 * through `seekTrack`, and the offset moves to the end of data already buffered.
 */
function seekPosition(session, t) {
  const { file } = session;
  let offset = Infinity;
  let time = Infinity;

  for (const { id } of session.tracks) {
    const trak = file.getTrackById(id);
    if (t > file.getTrackDuration(trak)) continue;

    const found = file.seekTrack(t, true, trak);
    offset = Math.min(offset, found.offset);
    time = Math.min(time, found.time);
  }

  if (offset === Infinity) throw new Error('mp4box found no sample to start from');
  return { offset: file.stream.getEndFilePositionAfter(offset), time };
}

async function openPlain({ source, located, movie, blockBytes, size, loadMp4Box }) {
  if (typeof loadMp4Box !== 'function') throw new TypeError('opening a plain MP4 needs loadMp4Box');

  const MP4Box = await loadMp4Box();
  const block0 = await source.block(0);

  // Parsed once here for the track list and the initialization segments; the first seek starts from
  // this same instance, later ones from a new one.
  const codecs = new Map(movie.tracks.map((track) => [track.id, track.codec]));
  let spare = startSession(MP4Box, block0, located, codecs);
  const { tracks, init } = spare;
  let current = null;
  let closed = false;

  // Time past the end of every segmented track leaves nothing to start from.
  const reach = Math.max(0, ...movie.tracks.filter((t) => t.kind !== 'other' && t.table !== null && t.timescale > 0).map((t) => t.table.endTicks / t.timescale));
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

      const session = spare ?? startSession(MP4Box, block0, located, codecs);
      spare = null;
      current?.file.stop?.();
      current = null;

      let position;
      try {
        position = seekPosition(session, Math.min(seconds(target), reach));
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
