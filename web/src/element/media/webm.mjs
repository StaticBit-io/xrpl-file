// WebM (EBML) reading for the video player: the header that tells MediaSource what it is appending, the
// Cues that map a time to a Cluster, and the lookup in them. Pure functions over bytes; the only IO is
// the block reader `locateCues` is handed.
//
// New in this repository. The video platform never reads WebM, so there is nothing to adapt; the
// block-range reading mirrors `locateMoov` in boxes.mjs.
//
// EBML in one paragraph: an element is an ID, a size and the data. An ID is 1-4 bytes and keeps its
// length-marker bits (it is compared as written in the specification); a size is 1-8 bytes with the
// marker bits stripped, and a size whose value bits are all ones means "unknown", which the format
// allows only for a Segment or a Cluster written to a pipe. Bytes are untrusted: every size is checked
// against what the parent and the bytes can hold before it is used, so hostile input throws a
// RangeError and cannot loop or allocate more than the bytes it came in.

const ID_EBML = 0x1a45dfa3;
const ID_SEGMENT = 0x18538067;
const ID_SEEK_HEAD = 0x114d9b74;
const ID_INFO = 0x1549a966;
const ID_TRACKS = 0x1654ae6b;
const ID_CLUSTER = 0x1f43b675;
const ID_CUES = 0x1c53bb6b;

const ID_SEEK = 0x4dbb;
const ID_SEEK_ID = 0x53ab;
const ID_SEEK_POSITION = 0x53ac;
const ID_TIMECODE_SCALE = 0x2ad7b1;
const ID_DURATION = 0x4489;
const ID_TRACK_ENTRY = 0xae;
const ID_CODEC_ID = 0x86;
const ID_VIDEO = 0xe0;
const ID_PIXEL_WIDTH = 0xb0;
const ID_PIXEL_HEIGHT = 0xba;
const ID_CUE_POINT = 0xbb;
const ID_CUE_TIME = 0xb3;
const ID_CUE_TRACK_POSITIONS = 0xb7;
const ID_CUE_CLUSTER_POSITION = 0xf1;

const CUES_ID_BYTES = [0x1c, 0x53, 0xbb, 0x6b];

/** What the format says when a Segment or Cluster has no size yet. */
const UNKNOWN_SIZE = -1;

/** Nanoseconds in a second: the unit of `TimecodeScale`. */
const NANOSECONDS = 1e9;

/** The format's default `TimecodeScale`: one millisecond per tick. */
const DEFAULT_TIMECODE_SCALE = 1_000_000;

/** An ID (4) and a size (8) are the most an element header takes. */
const MAX_HEADER_BYTES = 12;

/**
 * The most `locateCues` reads for one Cues element. ffmpeg writes about 20 bytes a cue, so this is
 * hours of video at one cue per second; a larger claim is a corrupt or hostile size.
 */
const MAX_CUES_BYTES = 16 * 1024 * 1024;

const MAX_CODEC_LENGTH = 64;

const CODEC_STRINGS = new Map([
  ['V_VP9', 'vp9'],
  ['V_VP8', 'vp8'],
  ['A_OPUS', 'opus'],
  ['A_VORBIS', 'vorbis'],
]);

function truncated(at) {
  return new RangeError(`the WebM bytes end inside the element at byte ${at}`);
}

/** An element ID, marker bits kept. @returns {{ value: number, length: number }} */
function readId(bytes, at, limit) {
  if (at >= limit) throw truncated(at);

  const first = bytes[at];
  const length = Math.clz32(first) - 23;
  if (first === 0 || length > 4) throw new RangeError(`byte ${at} does not start an EBML element ID`);
  if (at + length > limit) throw truncated(at);

  let value = first;
  for (let i = 1; i < length; i++) value = value * 256 + bytes[at + i];
  return { value, length };
}

/** An element size, marker bits stripped. @returns {{ value: number, length: number }} value is UNKNOWN_SIZE when all ones */
function readSize(bytes, at, limit) {
  if (at >= limit) throw truncated(at);

  const first = bytes[at];
  const length = Math.clz32(first) - 23;
  if (first === 0) throw new RangeError(`byte ${at} does not start an EBML size`);
  if (at + length > limit) throw truncated(at);

  const mask = 0xff >> length;
  let value = first & mask;
  let allOnes = value === mask;
  for (let i = 1; i < length; i++) {
    value = value * 256 + bytes[at + i];
    if (bytes[at + i] !== 0xff) allOnes = false;
  }

  if (allOnes) return { value: UNKNOWN_SIZE, length };
  // Eight-byte sizes reach 2^56; past 2^53 the number is no longer exact, and no file is that big.
  if (!Number.isSafeInteger(value)) throw new RangeError(`the size at byte ${at} is beyond any file`);
  return { value, length };
}

/**
 * @typedef {object} Element
 * @property {number} id
 * @property {number} start  first header byte
 * @property {number} dataStart
 * @property {number} size  UNKNOWN_SIZE (-1) when the writer left it open
 * @property {number} end  `dataStart + size`; meaningless when the size is unknown
 */

/** The element header at `at`, read no further than `limit`. @returns {Element} */
function readHeader(bytes, at, limit) {
  const id = readId(bytes, at, limit);
  const size = readSize(bytes, at + id.length, limit);
  const dataStart = at + id.length + size.length;
  return { id: id.value, start: at, dataStart, size: size.value, end: dataStart + size.value };
}

/** The elements directly inside `[from, to)`, each of them known-size and inside it. @returns {Element[]} */
function childrenOf(bytes, from, to) {
  const out = [];
  let at = from;

  while (at < to) {
    const element = readHeader(bytes, at, to);
    if (element.size === UNKNOWN_SIZE) {
      throw new RangeError(`the element at byte ${at} has no size, which only a Segment or Cluster may`);
    }
    if (element.end > to) throw new RangeError(`the element at byte ${at} reaches past the one holding it`);

    out.push(element);
    at = element.end;
  }

  return out;
}

function readUint(bytes, element) {
  if (element.size > 8) throw new RangeError(`the integer at byte ${element.start} is ${element.size} bytes`);

  let value = 0;
  for (let i = element.dataStart; i < element.end; i++) value = value * 256 + bytes[i];
  if (!Number.isSafeInteger(value)) throw new RangeError(`the integer at byte ${element.start} is beyond any file`);
  return value;
}

function readFloat(bytes, element) {
  const view = new DataView(bytes.buffer, bytes.byteOffset + element.dataStart, element.size);
  if (element.size === 4) return view.getFloat32(0);
  if (element.size === 8) return view.getFloat64(0);
  throw new RangeError(`the float at byte ${element.start} is ${element.size} bytes`);
}

/** A CodecID is ASCII, usually; whatever else arrives is made safe to put inside a MIME parameter. */
function readCodecId(bytes, element) {
  let text = '';
  for (let i = element.dataStart; i < element.end && text.length < MAX_CODEC_LENGTH; i++) {
    if (bytes[i] === 0) break;
    text += String.fromCharCode(bytes[i]);
  }
  return text.replace(/[^A-Za-z0-9_.-]/g, '_');
}

function readSeekHead(bytes, seekHead, segmentStart) {
  for (const seek of childrenOf(bytes, seekHead.dataStart, seekHead.end)) {
    if (seek.id !== ID_SEEK) continue;

    let isCues = false;
    let position = null;
    for (const field of childrenOf(bytes, seek.dataStart, seek.end)) {
      if (field.id === ID_SEEK_ID) {
        isCues =
          field.size === CUES_ID_BYTES.length &&
          CUES_ID_BYTES.every((byte, i) => bytes[field.dataStart + i] === byte);
      } else if (field.id === ID_SEEK_POSITION) {
        position = readUint(bytes, field);
      }
    }

    if (isCues && position !== null) return segmentStart + position;
  }

  return null;
}

function readInfo(bytes, info) {
  let timecodeScale = DEFAULT_TIMECODE_SCALE;
  let ticks = NaN;

  for (const field of childrenOf(bytes, info.dataStart, info.end)) {
    if (field.id === ID_TIMECODE_SCALE) timecodeScale = readUint(bytes, field);
    else if (field.id === ID_DURATION) ticks = readFloat(bytes, field);
  }

  if (timecodeScale === 0) throw new RangeError('the WebM TimecodeScale is zero');
  return { timecodeScale, ticks };
}

function readTracks(bytes, tracks) {
  const codecs = [];
  let width = 0;
  let height = 0;

  for (const entry of childrenOf(bytes, tracks.dataStart, tracks.end)) {
    if (entry.id !== ID_TRACK_ENTRY) continue;

    for (const field of childrenOf(bytes, entry.dataStart, entry.end)) {
      if (field.id === ID_CODEC_ID) {
        const codecId = readCodecId(bytes, field);
        if (codecId !== '') codecs.push(CODEC_STRINGS.get(codecId) ?? codecId);
      } else if (field.id === ID_VIDEO && width === 0) {
        for (const dimension of childrenOf(bytes, field.dataStart, field.end)) {
          if (dimension.id === ID_PIXEL_WIDTH) width = readUint(bytes, dimension);
          else if (dimension.id === ID_PIXEL_HEIGHT) height = readUint(bytes, dimension);
        }
      }
    }
  }

  return { codecs, width, height };
}

/**
 * @typedef {object} WebmHead
 * @property {number} segmentStart  first byte of the Segment's data; Cue positions count from here
 * @property {number} initEnd  where the first Cluster starts; everything before it is the initialization segment
 * @property {number} timecodeScale  nanoseconds per timestamp tick
 * @property {number} duration  seconds; NaN when the file does not say
 * @property {number | null} cuesOffset  absolute offset of the Cues element, from the SeekHead; null without one
 * @property {string[]} codecs  `vp9`, `vp8`, `opus`, `vorbis`; any other CodecID is passed through as written
 * @property {number} width  pixels of the first video track; 0 without one
 * @property {number} height
 */

/**
 * Reads what comes before the first Cluster of a WebM file.
 *
 * Needs the whole initialization segment, so pass at least the first block; a file whose Tracks or
 * Tags sit past the bytes given is reported as truncated, not guessed at. Unknown-size Segments are
 * accepted (a file written to a pipe has no sizes yet); nothing before the first Cluster may be.
 *
 * @param {Uint8Array} bytes the start of the file
 * @returns {WebmHead}
 * @throws {RangeError} when the bytes are not WebM, end before the first Cluster, or carry a size that cannot be true
 */
export function parseWebmHead(bytes) {
  const header = readHeader(bytes, 0, bytes.length);
  if (header.id !== ID_EBML) throw new RangeError('these bytes do not start with an EBML header');
  if (header.size === UNKNOWN_SIZE) throw new RangeError('the EBML header has no size');
  if (header.end > bytes.length) throw truncated(0);

  let segment = readHeader(bytes, header.end, bytes.length);
  while (segment.id !== ID_SEGMENT) {
    if (segment.size === UNKNOWN_SIZE) throw new RangeError('an element ahead of the Segment has no size');
    if (segment.end > bytes.length) throw truncated(segment.start);
    segment = readHeader(bytes, segment.end, bytes.length);
  }

  const segmentStart = segment.dataStart;
  const segmentEnd = segment.size === UNKNOWN_SIZE ? Infinity : segment.end;
  const limit = Math.min(segmentEnd, bytes.length);

  let cuesOffset = null;
  let timecodeScale = DEFAULT_TIMECODE_SCALE;
  let ticks = NaN;
  let tracks = { codecs: [], width: 0, height: 0 };
  let initEnd = -1;

  for (let at = segmentStart; at < limit; ) {
    const element = readHeader(bytes, at, limit);

    if (element.id === ID_CLUSTER) {
      initEnd = at;
      break;
    }

    if (element.size === UNKNOWN_SIZE) {
      throw new RangeError(`the element at byte ${at} has no size, which only a Segment or Cluster may`);
    }
    if (element.end > segmentEnd) throw new RangeError(`the element at byte ${at} reaches past the Segment`);
    if (element.end > bytes.length) throw truncated(at);

    if (element.id === ID_SEEK_HEAD) cuesOffset ??= readSeekHead(bytes, element, segmentStart);
    else if (element.id === ID_INFO) ({ timecodeScale, ticks } = readInfo(bytes, element));
    else if (element.id === ID_TRACKS) tracks = readTracks(bytes, element);

    at = element.end;
  }

  if (initEnd < 0) throw new RangeError('the WebM bytes end before the first Cluster');

  const duration = Number.isFinite(ticks) && ticks >= 0 ? (ticks * timecodeScale) / NANOSECONDS : NaN;
  return { segmentStart, initEnd, timecodeScale, duration, cuesOffset, ...tracks };
}

/**
 * Reads the Cues element of a file from the blocks that hold it.
 *
 * Only the blocks the Cues element spans are read: the one holding its header, and the following ones
 * when it runs on. The file's size is not known here, so a block that ends short before the element
 * does is the signal that the SeekHead (or the size) lied.
 *
 * @param {(index: number) => Promise<Uint8Array>} readBlock the block `index`, `blockBytes` long (the last one may be shorter)
 * @param {number} blockBytes
 * @param {WebmHead} head
 * @returns {Promise<Uint8Array | null>} the whole Cues element, header included; null when the file declares none
 * @throws {RangeError} when the offset does not hold a Cues element or the element runs past the end of the file
 */
export async function locateCues(readBlock, blockBytes, head) {
  if (head.cuesOffset === null) return null;

  const recent = new Map();

  async function block(index) {
    const cached = recent.get(index);
    if (cached) return cached;

    const bytes = await readBlock(index);
    recent.set(index, bytes);
    if (recent.size > 2) recent.delete(recent.keys().next().value);
    return bytes;
  }

  /** `[from, to)`; shorter when `lenient` and the file ends first, otherwise an error then. */
  async function range(from, to, lenient) {
    const out = new Uint8Array(to - from);
    let filled = 0;
    const last = Math.floor((to - 1) / blockBytes);

    for (let index = Math.floor(from / blockBytes); index <= last; index++) {
      const bytes = await block(index);
      const blockStart = index * blockBytes;
      const copyFrom = Math.max(from, blockStart);
      const copyTo = Math.min(to, blockStart + bytes.length);

      if (copyTo > copyFrom) {
        out.set(bytes.subarray(copyFrom - blockStart, copyTo - blockStart), copyFrom - from);
        filled = copyTo - from;
      }

      if (bytes.length < blockBytes && copyTo < to) {
        if (lenient) return out.subarray(0, filled);
        throw new RangeError('the file ends inside the Cues element');
      }
    }

    return out;
  }

  const start = head.cuesOffset;
  const opening = await range(start, start + MAX_HEADER_BYTES, true);
  const header = readHeader(opening, 0, opening.length);
  if (header.id !== ID_CUES) throw new RangeError(`the SeekHead points at byte ${start}, which is not a Cues element`);
  if (header.size === UNKNOWN_SIZE) throw new RangeError('the Cues element has no size');
  if (header.size > MAX_CUES_BYTES) throw new RangeError(`the Cues element claims ${header.size} bytes`);

  return range(start, start + header.dataStart + header.size, false);
}

/**
 * Reads a Cues element into time and position pairs.
 *
 * A CuePoint may list a position per track; the first with a cluster position is taken, which is the
 * Cluster to start from for any of them.
 *
 * @param {Uint8Array} cuesBytes the whole Cues element, header included, as `locateCues` returns it
 * @param {number} segmentStart `WebmHead.segmentStart`: positions in the file count from it
 * @param {number} timecodeScale `WebmHead.timecodeScale`
 * @returns {{ time: number, offset: number }[]} seconds and absolute Cluster offsets, ascending by time
 * @throws {RangeError} when the bytes are not a complete Cues element
 */
export function parseCues(cuesBytes, segmentStart, timecodeScale) {
  const cues = readHeader(cuesBytes, 0, cuesBytes.length);
  if (cues.id !== ID_CUES) throw new RangeError('these bytes are not a Cues element');
  if (cues.size === UNKNOWN_SIZE) throw new RangeError('the Cues element has no size');
  if (cues.end > cuesBytes.length) throw truncated(0);

  const points = [];

  for (const point of childrenOf(cuesBytes, cues.dataStart, cues.end)) {
    if (point.id !== ID_CUE_POINT) continue;

    let ticks = null;
    let position = null;

    for (const field of childrenOf(cuesBytes, point.dataStart, point.end)) {
      if (field.id === ID_CUE_TIME) {
        ticks = readUint(cuesBytes, field);
      } else if (field.id === ID_CUE_TRACK_POSITIONS && position === null) {
        for (const inner of childrenOf(cuesBytes, field.dataStart, field.end)) {
          if (inner.id === ID_CUE_CLUSTER_POSITION) position = readUint(cuesBytes, inner);
        }
      }
    }

    if (ticks !== null && position !== null) {
      points.push({ time: (ticks * timecodeScale) / NANOSECONDS, offset: segmentStart + position });
    }
  }

  // Writers emit cues in time order; a stable sort makes the order a guarantee instead of a hope.
  return points.sort((a, b) => a.time - b.time);
}

/**
 * The cue to seek to.
 *
 * @param {{ time: number }[]} cues ascending by time
 * @param {number} seconds
 * @returns {number} index of the last cue at or before `seconds`; 0 before the first cue
 */
export function cueAt(cues, seconds) {
  let low = 0;
  let high = cues.length - 1;
  let found = 0;

  while (low <= high) {
    const middle = (low + high) >> 1;
    if (cues[middle].time <= seconds) {
      found = middle;
      low = middle + 1;
    } else {
      high = middle - 1;
    }
  }

  return found;
}

/**
 * The MIME type to hand `MediaSource.isTypeSupported` and `addSourceBuffer`.
 *
 * @param {WebmHead} head
 * @returns {string} for example `video/webm; codecs="vp9,opus"`
 */
export function webmMime(head) {
  return head.codecs.length === 0 ? 'video/webm' : `video/webm; codecs="${head.codecs.join(',')}"`;
}
