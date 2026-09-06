import { MANIFEST_BYTES, CHUNK_BYTES, blockCount, chunkCount } from './tree.mjs';

export const MANIFEST_VERSION = 1;

const byteLength = (text) => new TextEncoder().encode(text).length;

function serialize(m, name) {
  const fields = { v: MANIFEST_VERSION, type: m.type, size: m.size, chunk: m.chunk, root: m.root, ledgers: [m.firstLedger, m.lastLedger], blocks: m.blocks, lists: m.lists };
  if (name != null) fields.name = name;
  return JSON.stringify(fields);
}

/** One character off the end — a whole code point, so a surrogate pair is not cut in half. */
function shortenByOneCharacter(value) {
  const points = [...value];
  points.pop();
  return points.join('');
}

export function encodeManifest(m) {
  let name = m.name ?? null;
  for (;;) {
    const json = serialize(m, name);
    if (byteLength(json) <= MANIFEST_BYTES) return json;
    if (!name) throw new Error('the manifest does not fit its memo even without a name');
    name = shortenByOneCharacter(name);
    if (name.length === 0) name = null;
  }
}

// The .NET reader decodes size and the ledger pair as Int64 and chunk, blocks and lists as Int32;
// a number wider than that is "not one this manifest may carry" there and must be here too. The
// Int32 bound is what keeps a manifest from naming a billion block lists and having the reader
// size an index array for them before ever asking the node whether they exist.
const INT32_MIN = -2147483648;
const INT32_MAX = 2147483647;
const isInt = (v) => Number.isSafeInteger(v);
const isInt32 = (v) => Number.isSafeInteger(v) && v >= INT32_MIN && v <= INT32_MAX;

export function decodeManifest(json) {
  let m;
  try { m = JSON.parse(json); } catch (e) { throw new Error(`the manifest does not parse as JSON: ${e.message}`, { cause: e }); }
  if (m === null || typeof m !== 'object' || Array.isArray(m)) throw new Error('the manifest is not a JSON object');
  if (!isInt(m.v)) throw new Error('the manifest has no version');
  if (m.v !== MANIFEST_VERSION) throw new Error(`manifest version ${m.v} is not one this library reads`);
  if (typeof m.type !== 'string') throw new Error('the manifest has no type');
  if (!isInt(m.size)) throw new Error('the manifest has no size');
  if (!isInt32(m.chunk)) throw new Error('the manifest has no chunk size');
  if (typeof m.root !== 'string' || !/^[0-9a-fA-F]{64}$/.test(m.root)) throw new Error('the manifest root is not 64 hex characters');
  if (!isInt32(m.blocks)) throw new Error('the manifest has no block count');
  if (!isInt32(m.lists)) throw new Error('the manifest has no list count');
  if (!Array.isArray(m.ledgers) || m.ledgers.length !== 2) throw new Error('the manifest ledgers are not a pair');
  const [firstLedger, lastLedger] = m.ledgers;
  if (!isInt(firstLedger) || !isInt(lastLedger) || firstLedger <= 0 || lastLedger < firstLedger) throw new Error('the manifest ledger range is not ascending');
  return {
    type: m.type, size: m.size, chunk: m.chunk, root: m.root.toUpperCase(), firstLedger, lastLedger,
    blocks: m.blocks, lists: m.lists, name: typeof m.name === 'string' ? m.name : null,
  };
}

/** Why the counts do not agree with one another, or null when they do. */
export function inconsistency(m) {
  if (m.size < 0) return `the size ${m.size} is negative`;
  if (m.chunk <= 0 || m.chunk > CHUNK_BYTES) return `a chunk of ${m.chunk} bytes is outside 1..${CHUNK_BYTES}`;
  // Beyond this the file has more chunks than a chunk index can name. The .NET reader refuses the
  // same number for the same reason, where its chunk-count arithmetic would also overflow.
  const addressable = m.chunk * INT32_MAX;
  if (m.size > addressable) return `the size ${m.size} is past the ${addressable} bytes ${m.chunk}-byte chunks can address`;
  if (m.lists < 0) return `the list count ${m.lists} is negative`;
  const chunks = chunkCount(m.size, m.chunk);
  const expectedBlocks = blockCount(chunks);
  if (m.blocks !== expectedBlocks) return `the manifest says ${m.blocks} blocks, ${chunks} chunks make ${expectedBlocks}`;
  if ((m.lists === 0) !== (m.blocks === 1)) return `the manifest says ${m.blocks} blocks and ${m.lists} list memos, and a single block is the only file without a list`;
  return null;
}
