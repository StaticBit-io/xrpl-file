// ISO base media file format box walking: headers with 64-bit sizes, children, and finding the moov
// box wherever the writer put it while reading as few blocks as possible.
//
// Adapted from E:\GIT\XRPL\xrpl-video-platform src/Platform.Web/wwwroot/js/mp4.mjs (`topLevelBoxes`,
// the size-1 and size-0 header rules) and fragments.mjs (`readBoxes`, the opt-in handling of a box
// cut by the end of the bytes). Changed: the header is returned whole, a header that is not all in
// the bytes is reported instead of thrown, a box may reach past the bytes (a file is read a block
// at a time), and `locateMoov` is new.

const SMALL_HEADER = 8;
const LARGE_HEADER = 16;

/**
 * @typedef {object} Box
 * @property {string} type  four-character code
 * @property {number} start  offset of the first header byte in the bytes the box was read from
 * @property {number} headerSize  8, or 16 when the size is a 64-bit `largesize`
 * @property {number} size  header included
 * @property {number} end  `start + size`; can lie past the bytes when only part of the file is held
 */

function fourcc(bytes, at) {
  return String.fromCharCode(bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]);
}

/**
 * The box whose header starts at `at`.
 *
 * @param {Uint8Array} bytes
 * @param {number} at
 * @param {number} [limit] where a box of size 0 ends: the end of its parent, the end of the bytes by default
 * @returns {Box | null} null when the header is not all inside `bytes`
 * @throws {RangeError} when the declared size is smaller than the header itself
 */
export function readBox(bytes, at, limit = bytes.length) {
  if (at + SMALL_HEADER > bytes.length) return null;

  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const type = fourcc(bytes, at + 4);
  let size = view.getUint32(at);
  let headerSize = SMALL_HEADER;

  if (size === 1) {
    if (at + LARGE_HEADER > bytes.length) return null;
    size = view.getUint32(at + 8) * 2 ** 32 + view.getUint32(at + 12);
    headerSize = LARGE_HEADER;
  } else if (size === 0) {
    // "To the end": of the file, which is as far as this caller can see.
    size = limit - at;
  }

  if (size < headerSize) {
    throw new RangeError(`malformed box "${type}" at byte ${at}: it claims ${size} bytes`);
  }

  return { type, start: at, headerSize, size, end: at + size };
}

/**
 * The boxes laid end to end from `from`.
 *
 * Stops at the first header that is not complete and reports it as the last element with
 * `partial: true`, so a caller holding only the first block of a file can tell "the next header is in
 * the next block" from "the list ended". A box whose body runs past the bytes is a normal element;
 * the walk ends there because the next header is out of reach.
 *
 * @param {Uint8Array} bytes
 * @param {number} [from]
 * @param {number} [limit]
 * @returns {(Box | { type: '', start: number, headerSize: 0, size: number, end: number, partial: true })[]}
 */
export function topLevelBoxes(bytes, from = 0, limit = bytes.length) {
  const visible = limit === bytes.length ? bytes : bytes.subarray(0, limit);
  const boxes = [];
  let at = from;

  while (at < limit) {
    const box = readBox(visible, at, limit);

    if (box === null) {
      boxes.push({ type: '', start: at, headerSize: 0, size: limit - at, end: limit, partial: true });
      break;
    }

    boxes.push(box);
    at = box.end;
  }

  return boxes;
}

/**
 * The boxes directly inside a container box.
 *
 * Plain containers only (`moov`, `trak`, `mdia`, `minf`, `stbl`, `mvex`, ...): a box with fields of
 * its own before its children, such as `stsd`, is read by its caller.
 *
 * @param {Uint8Array} bytes
 * @param {Box} box
 * @returns {Box[]}
 */
export function children(bytes, box) {
  const limit = Math.min(box.end, bytes.length);
  return topLevelBoxes(bytes, box.start + box.headerSize, limit).filter((child) => !child.partial);
}

/**
 * Follows a path of box types down from `box`; the first match at each level.
 *
 * @param {Uint8Array} bytes
 * @param {Box} box
 * @param {...string} path
 * @returns {Box | null}
 */
export function find(bytes, box, ...path) {
  let current = box;

  for (const type of path) {
    current = children(bytes, current).find((child) => child.type === type) ?? null;
    if (current === null) return null;
  }

  return current;
}

/**
 * Finds the `moov` box of a file by reading only the blocks that hold box headers and the box itself.
 *
 * Walks the top-level headers from block 0. A box that is not `moov` is jumped over by its size,
 * straight to the block holding the next header, so a `moov` behind a long `mdat` costs the first
 * block, the block at the end of the `mdat` and the blocks `moov` spans, not the whole file.
 *
 * @param {(index: number) => Promise<Uint8Array>} readBlock the block `index`, `blockBytes` long (the last one may be shorter)
 * @param {number} blockBytes
 * @param {number} size file size in bytes
 * @returns {Promise<{ offset: number, bytes: Uint8Array, blocks: number[] }>} where `moov` starts, its bytes (header included) and every block read, in the order read
 * @throws {Error} when no complete `moov` is found
 */
export async function locateMoov(readBlock, blockBytes, size) {
  const blocks = [];
  // The walk only moves forward, so the two most recent blocks cover every header that straddles an edge.
  const recent = new Map();

  async function block(index) {
    const cached = recent.get(index);
    if (cached) return cached;

    const bytes = await readBlock(index);
    const expected = Math.min(blockBytes, size - index * blockBytes);
    if (bytes.length !== expected) {
      throw new Error(`block ${index} has ${bytes.length} bytes, expected ${expected}`);
    }

    blocks.push(index);
    recent.set(index, bytes);
    if (recent.size > 2) recent.delete(recent.keys().next().value);
    return bytes;
  }

  async function range(from, to) {
    const out = new Uint8Array(to - from);
    const last = Math.floor((to - 1) / blockBytes);

    for (let index = Math.floor(from / blockBytes); index <= last; index++) {
      const bytes = await block(index);
      const blockStart = index * blockBytes;
      const copyFrom = Math.max(from, blockStart);
      const copyTo = Math.min(to, blockStart + bytes.length);
      out.set(bytes.subarray(copyFrom - blockStart, copyTo - blockStart), copyFrom - from);
    }

    return out;
  }

  let at = 0;

  while (at + SMALL_HEADER <= size) {
    const header = await range(at, Math.min(at + LARGE_HEADER, size));
    const box = readBox(header, 0, size - at);
    if (box === null) break;

    if (box.type === 'moov') {
      if (at + box.size > size) throw new Error('the moov box claims more bytes than the file holds');
      return { offset: at, bytes: await range(at, at + box.size), blocks };
    }

    at += box.size;
  }

  throw new Error('the file has no moov box');
}
