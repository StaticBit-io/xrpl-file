// Copied from E:\GIT\XRPL\xrpl-video-platform src/Platform.Web/wwwroot/js/sidx.mjs: `parseSidx` and
// `groupAt` are that file's, byte for byte. Left out: `buildSidx` and its version constant, because the
// element only reads an index. Added at the end: `findSidx`.

/**
 * The segment index: where in the file each stretch of time begins.
 *
 * A viewer who drags the playhead to the middle asks a question the bytes alone cannot answer.
 * Time lives in the fragments, scattered through the file; finding the one that covers a moment
 * means reading everything before it, which is exactly what streaming out of a ledger cannot
 * afford. So the file carries a table: for each group of fragments, how many bytes it takes and
 * how long it lasts. Running totals over that table turn a moment into a byte offset, and a byte
 * offset into a chunk, a block and the ledger that block starts in.
 *
 * The table is the ISO base media format's own `sidx` box, written after `moov`. Inside the file
 * rather than beside it, because then it is covered by the video's Merkle root — a lying index
 * would change the root and never be published — and because any other reader of the format
 * understands it without being told about this platform.
 *
 * One deliberate narrowing: `first_offset` is always zero and there are no second-level indexes.
 * A writer that needs them can add them; a reader that meets them here would be reading a file
 * this platform did not write.
 */

import { readBox } from './boxes.mjs';

const HEADER_BYTES = 8;
const VERSION_AND_FLAGS = 4;

const FIXED_BYTES = HEADER_BYTES + VERSION_AND_FLAGS + 4 + 4 + 8 + 8 + 2 + 2;
const ENTRY_BYTES = 12;

/**
 * The index read back, with byte offsets and start times a caller can use directly.
 *
 * @param {Uint8Array} bytes the file, or any buffer holding the box
 * @param {number} start where the box begins
 * @returns {{timescale: number, referenceId: number, end: number,
 *            groups: {offset: number, size: number, startTime: number, duration: number, sync: boolean}[]}}
 */
export function parseSidx(bytes, start) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);

  if (start + FIXED_BYTES > bytes.length) throw new Error('the file ends before its sidx does');

  const type = String.fromCharCode(bytes[start + 4], bytes[start + 5], bytes[start + 6], bytes[start + 7]);
  if (type !== 'sidx') throw new Error(`the box at byte ${start} is a ${type}, not a sidx`);

  const size = view.getUint32(start);
  const version = bytes[start + 8];
  const referenceId = view.getUint32(start + 12);
  const timescale = view.getUint32(start + 16);

  // Version 0 counts the two long fields in 32 bits. This platform writes version 1; a file from
  // elsewhere may not, and reading both is four lines.
  const long = version === 1 ? 8 : 4;
  let at = start + 20 + long * 2;

  const firstOffset = version === 1
    ? Number(view.getBigUint64(start + 28))
    : view.getUint32(start + 24);

  at += 2;                                          // reserved
  const count = view.getUint16(at);
  at += 2;

  if (start + size > bytes.length) throw new Error('the sidx claims more bytes than the file holds');

  const groups = [];
  let offset = start + size + firstOffset;
  let time = 0;

  for (let i = 0; i < count; i++) {
    const entry = at + i * ENTRY_BYTES;
    if (entry + ENTRY_BYTES > start + size) throw new Error('the sidx ends in the middle of a group');

    const referenceSize = view.getUint32(entry) & 0x7fffffff;
    const duration = view.getUint32(entry + 4);
    const sync = (view.getUint32(entry + 8) & (1 << 31)) !== 0;

    groups.push({ offset, size: referenceSize, startTime: time / timescale, duration: duration / timescale, sync });

    offset += referenceSize;
    time += duration;
  }

  return { timescale, referenceId, end: start + size, groups };
}

/**
 * Which group covers a moment: the last one that starts at or before it.
 *
 * Not the nearest. A decoder handed a group that starts after the moment asked for would have
 * nothing to show until it arrived there, and a moment before the first group is a request to
 * start over.
 *
 * @param {{startTime: number}[]} groups
 * @param {number} seconds
 * @returns {number} the group's index
 */
export function groupAt(groups, seconds) {
  let found = 0;

  for (let i = 0; i < groups.length; i++) {
    if (groups[i].startTime <= seconds) found = i;
    else break;
  }

  return found;
}

/**
 * Where the top-level `sidx` after `moov` starts, or null when the file has none.
 *
 * Walks the top-level headers only. The index sits between `moov` and the first fragment, so the
 * walk stops at the first `moof` and a head of the file is enough to find it.
 *
 * @param {Uint8Array} bytes the file, or its first bytes
 * @returns {number | null}
 */
export function findSidx(bytes) {
  let seenMoov = false;

  for (let at = 0; at < bytes.length; ) {
    const box = readBox(bytes, at);
    if (box === null) return null;

    if (box.type === 'moov') seenMoov = true;
    else if (box.type === 'sidx' && seenMoov) return box.start;
    else if (box.type === 'moof') return null;

    at = box.end;
  }

  return null;
}
