import { leaf, root } from './merkle.mjs';
import { hex, unhex, utf8Hex, hexUtf8 } from './hex.mjs';

export const MAX_MEMO_BYTES = 1019;
export const MEMO_TYPE_FRAMING = 2;
export const MAX_INDEX_DIGITS = 12;
export const MAX_MARKER_BYTES = 5 + MAX_INDEX_DIGITS;
export const CHUNK_BYTES = MAX_MEMO_BYTES - MEMO_TYPE_FRAMING - MAX_MARKER_BYTES;
export const LIST_BYTES = CHUNK_BYTES - MEMO_TYPE_FRAMING - 16;
export const MANIFEST_BYTES = MAX_MEMO_BYTES - MEMO_TYPE_FRAMING - 4 - MEMO_TYPE_FRAMING - 16;
export const BLOCK_CHUNKS = 1024;

export const MANIFEST_MARKER = 'xf/m';
export const MANIFEST_MARKER_HEX = utf8Hex(MANIFEST_MARKER);
export const JSON_FORMAT_HEX = utf8Hex('application/json');

const CHUNK_PREFIX = 'xf/c/';
const LIST_PREFIX = 'xf/b/';

function indexedMarkerHex(prefix, index) {
  if (!Number.isInteger(index) || index < 0) throw new RangeError('a memo index is never negative');
  const marker = `${prefix}${index}`;
  if (marker.length > MAX_MARKER_BYTES) throw new RangeError(`an index past ${MAX_INDEX_DIGITS} digits does not fit its memo`);
  return utf8Hex(marker);
}

// Strict on purpose: a leading zero, a sign or a stray character is somebody else's memo.
function indexOf(memoTypeHex, prefix) {
  if (!memoTypeHex) return null;
  let marker;
  try { marker = hexUtf8(memoTypeHex); } catch { return null; }
  if (!marker.startsWith(prefix)) return null;
  const digits = marker.slice(prefix.length);
  if (!/^(0|[1-9][0-9]*)$/.test(digits) || digits.length > MAX_INDEX_DIGITS) return null;
  return Number(digits);
}

export const chunkMarkerHex = (i) => indexedMarkerHex(CHUNK_PREFIX, i);
export const listMarkerHex = (j) => indexedMarkerHex(LIST_PREFIX, j);
export const chunkIndexOf = (memoTypeHex) => indexOf(memoTypeHex, CHUNK_PREFIX);
export const listIndexOf = (memoTypeHex) => indexOf(memoTypeHex, LIST_PREFIX);

export function isManifest(memoTypeHex) {
  if (!memoTypeHex) return false;
  try { return hexUtf8(memoTypeHex) === MANIFEST_MARKER; } catch { return false; }
}

export const blockOf = (chunkIndex) => Math.floor(chunkIndex / BLOCK_CHUNKS);
export const blockCount = (chunkCount) => (chunkCount <= 0 ? 0 : Math.ceil(chunkCount / BLOCK_CHUNKS));
export const chunkCount = (size, chunkBytes) => (size <= 0 ? 0 : Math.ceil(size / chunkBytes));

export function split(bytes, chunkBytes) {
  const chunks = [];
  for (let at = 0; at < bytes.length; at += chunkBytes) chunks.push(bytes.subarray(at, Math.min(at + chunkBytes, bytes.length)));
  return chunks;
}

export async function blockHash(chunks) {
  const leaves = [];
  for (const chunk of chunks) leaves.push(await leaf(chunk));
  return root(leaves);
}

export async function rootHex(entries) {
  const ordered = [...entries].sort((a, b) => a.i - b.i);
  return hex(await root(ordered.map((e) => unhex(e.h))));
}

const serialize = (wires) => JSON.stringify(wires.map(({ i, h, l }) => ({ i, h, l })));
const byteLength = (text) => new TextEncoder().encode(text).length;

export function encodeEntries(entries) {
  const payloads = [];
  let current = [];
  for (const entry of entries) {
    current.push(entry);
    if (byteLength(serialize(current)) > LIST_BYTES) {
      current.pop();
      payloads.push(serialize(current));
      current = [entry];
    }
  }
  if (current.length > 0) payloads.push(serialize(current));
  return payloads;
}

export function decodeEntries(payloads) {
  const entries = [];
  for (const payload of payloads) {
    let wires;
    try { wires = JSON.parse(payload); } catch (e) { throw new Error('a block-list memo is not a JSON array of entries', { cause: e }); }
    if (!Array.isArray(wires)) throw new Error('a block-list memo is not a JSON array of entries');
    for (const wire of wires) {
      if (typeof wire.h !== 'string' || !/^[0-9a-fA-F]{64}$/.test(wire.h)) throw new Error(`block ${wire.i} carries a hash that is not 32 bytes of hex`);
      if (!Number.isInteger(wire.i) || !Number.isInteger(wire.l)) throw new Error(`block ${wire.i} has a non-integer index or ledger`);
      entries.push({ i: wire.i, h: wire.h.toUpperCase(), l: wire.l });
    }
  }
  if (entries.length === 0) throw new Error('the block list is empty');
  entries.sort((a, b) => a.i - b.i);
  entries.forEach((entry, expected) => {
    if (entry.i !== expected) throw new Error(`the block list is not 0..${entries.length - 1}: found ${entry.i} where ${expected} was expected`);
  });
  return entries;
}
