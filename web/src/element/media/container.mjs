// Which container family a file's first bytes belong to, and whether a declared MIME type agrees.
// New in this repository; the ISO test reuses the box header reader of boxes.mjs.

import { readBox } from './boxes.mjs';

const EBML_MAGIC = [0x1a, 0x45, 0xdf, 0xa3];
const DOC_TYPE_ID = 0x4282;

// What can open an ISO file: a normal writer starts with ftyp, QuickTime-era writers with wide or
// mdat, and a faststart writer may have nothing in front of moov. free and skip are padding.
const ISO_FIRST_BOXES = new Set(['ftyp', 'moov', 'mdat', 'free', 'skip', 'wide']);

// brand (4) plus minor version (4) are the least an ftyp holds.
const MIN_FTYP_BYTES = 16;

/**
 * Reads one EBML variable-length integer.
 *
 * @param {Uint8Array} bytes
 * @param {number} at
 * @param {boolean} keepMarker an element ID keeps its length marker bits, a size drops them
 * @returns {{ value: number, length: number } | null}
 */
function readVint(bytes, at, keepMarker) {
  if (at >= bytes.length) return null;

  const first = bytes[at];
  if (first === 0) return null;

  const length = Math.clz32(first) - 23;
  if (length > 8 || at + length > bytes.length) return null;

  let value = keepMarker ? first : first & (0xff >> length);
  for (let i = 1; i < length; i++) value = value * 256 + bytes[at + i];

  return { value, length };
}

/** The DocType string of the EBML header, or null when the head does not reach it. */
function readDocType(head) {
  const size = readVint(head, EBML_MAGIC.length, false);
  if (size === null) return null;

  let at = EBML_MAGIC.length + size.length;
  const end = Math.min(at + size.value, head.length);

  while (at < end) {
    const id = readVint(head, at, true);
    if (id === null) return null;

    const length = readVint(head, at + id.length, false);
    if (length === null) return null;

    const body = at + id.length + length.length;
    if (id.value === DOC_TYPE_ID) {
      if (body + length.value > head.length) return null;
      return String.fromCharCode(...head.subarray(body, body + length.value));
    }

    at = body + length.value;
  }

  return null;
}

/**
 * Names the container family from the first bytes of a file.
 *
 * A plain MP4 and a fragmented one are both `iso`: telling them apart takes the whole `moov`
 * (presence of `mvex`), not the first bytes, so it is parseMovie's job.
 *
 * @param {Uint8Array} head the start of the file; a few dozen bytes are enough
 * @returns {'iso' | 'webm' | 'unknown'}
 */
export function detectContainer(head) {
  if (head.length >= EBML_MAGIC.length && EBML_MAGIC.every((byte, i) => head[i] === byte)) {
    return readDocType(head) === 'webm' ? 'webm' : 'unknown';
  }

  let box;
  try {
    box = readBox(head, 0);
  } catch {
    return 'unknown';
  }

  if (box === null || !ISO_FIRST_BOXES.has(box.type) || !Number.isSafeInteger(box.size)) return 'unknown';
  if (box.type === 'ftyp' && box.size < MIN_FTYP_BYTES) return 'unknown';

  return 'iso';
}

/**
 * Whether a declared MIME type is one the detected container can legitimately carry.
 *
 * @param {string} type a MIME type, parameters allowed
 * @param {'iso' | 'webm' | 'unknown'} kind the result of {@link detectContainer}
 * @returns {boolean}
 */
export function containerAgrees(type, kind) {
  const essence = String(type).split(';')[0].trim().toLowerCase();

  if (essence === 'video/mp4' || essence === 'video/quicktime') return kind === 'iso';
  if (essence === 'video/webm') return kind === 'webm';
  return false;
}
