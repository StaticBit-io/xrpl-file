import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { vectorBytes } from '../src/vector-stream.mjs';
import { split, blockHash, chunkMarkerHex, listMarkerHex, MANIFEST_MARKER_HEX, JSON_FORMAT_HEX, CHUNK_BYTES } from '../src/tree.mjs';
import { encodeManifest } from '../src/manifest.mjs';
import { hex, utf8Hex } from '../src/hex.mjs';
import { XrplFileError } from '../src/link.mjs';

export const WRITER = 'rVectorWriter';

export async function loadVectors() {
  return JSON.parse(await readFile(new URL('../../tests/vectors/v1.json', import.meta.url), 'utf8'));
}

export async function vectorFile(size) {
  const set = await loadVectors();
  const file = set.files.find((f) => f.size === size);
  if (!file) throw new Error(`no vector of ${size} bytes`);
  return file;
}

export function labelHash(label) {
  return createHash('sha256').update(label, 'utf8').digest('hex').toUpperCase();
}

export const MANIFEST_HASH = labelHash('manifest');

export const LINK = `xrpl-file://testnet/${MANIFEST_HASH}`;

// Generating a vector's bytes dominates these tests (seconds for the 2.5 MB vector), so each
// vector is generated once per process and every caller gets its own copy to mutate.
const bytesCache = new Map();
const ledgerCache = new Map();

/** The vector's bytes, generated once per process; the caller owns the returned copy. */
export async function vectorFileBytes(seed, size) {
  const key = `${seed}:${size}`;
  if (!bytesCache.has(key)) bytesCache.set(key, vectorBytes(seed, size));
  return (await bytesCache.get(key)).slice();
}

/** The vector's transactions laid out in the synthetic ledger the vector file describes. */
export async function vectorLedger(vector) {
  const key = `${vector.seed}:${vector.size}`;
  if (!ledgerCache.has(key)) ledgerCache.set(key, buildLedger(vector));
  return structuredClone(await ledgerCache.get(key));
}

async function buildLedger(vector) {
  const bytes = await vectorFileBytes(vector.seed, vector.size);
  const chunks = split(bytes, vector.chunk);
  const ledger = [];

  chunks.forEach((chunk, i) => ledger.push({
    hash: labelHash(`chunk:${i}`),
    ledgerIndex: vector.ledger.first + Math.floor(i / 100),
    account: WRITER,
    succeeded: true,
    validated: true,
    memoTypeHex: chunkMarkerHex(i),
    memoDataHex: hex(chunk),
    memoFormatHex: null,
  }));

  vector.lists.forEach((payload, j) => ledger.push({
    hash: labelHash(`list:${j}`),
    ledgerIndex: vector.ledger.listLedger,
    account: WRITER,
    succeeded: true,
    validated: true,
    memoTypeHex: listMarkerHex(j),
    memoDataHex: utf8Hex(payload),
    memoFormatHex: JSON_FORMAT_HEX,
  }));

  ledger.push({
    hash: MANIFEST_HASH,
    ledgerIndex: vector.ledger.manifestLedger,
    account: WRITER,
    succeeded: true,
    validated: true,
    memoTypeHex: MANIFEST_MARKER_HEX,
    memoDataHex: utf8Hex(vector.manifest),
    memoFormatHex: JSON_FORMAT_HEX,
  });

  return ledger;
}

/** An in-memory source: insertion order is canonical order within a ledger. */
export class FakeSource {
  constructor(transactions = [], earliest = 1) {
    this.transactions = [...transactions];
    this.earliest = earliest;
    this.calls = [];
    // Every record accountTransactions has handed out, over all walks.
    this.yielded = 0;
    // The next walk answers this many records and then fails the way a connection dropped between
    // two pages does, once. A failure only at the start of a walk cannot reach a reader that
    // carries a walk over from one block to the next.
    this.failAfter = null;
    this.transactionCalls = 0;
  }

  get accountTransactionsCalls() {
    return this.calls.length;
  }

  async transaction(hash) {
    this.transactionCalls++;
    return this.transactions.find((tx) => tx.hash.toUpperCase() === hash.toUpperCase()) ?? null;
  }

  async *accountTransactions(account, fromLedger, toLedger, forward) {
    this.calls.push({ account, fromLedger, toLedger, forward });
    const failAfter = this.failAfter;
    this.failAfter = null;
    const matching = this.transactions
      .map((tx, position) => ({ tx, position }))
      .filter(({ tx }) => tx.account === account && tx.ledgerIndex >= fromLedger && tx.ledgerIndex <= toLedger)
      .sort((a, b) => (a.tx.ledgerIndex - b.tx.ledgerIndex) || (a.position - b.position));
    if (!forward) matching.reverse();
    let handed = 0;
    for (const { tx } of matching) {
      if (handed++ === failAfter) throw new XrplFileError('NodeUnreachable', `fake: the node stopped answering after ${failAfter} records`);
      this.yielded++;
      yield tx;
    }
  }

  async earliestLedger() {
    return this.earliest;
  }
}

/**
 * A file of one block, laid out the way a writer publishes it: every chunk in `firstLedger`, the
 * manifest one ledger later. The manifest's hash is labelHash(label).
 */
export async function smallFileLedger({ label, bytes, type, writer = WRITER, firstLedger, cover = null }) {
  const chunks = split(bytes, CHUNK_BYTES);
  const root = hex(await blockHash(chunks));
  const transactions = chunks.map((chunk, i) => ({
    hash: labelHash(`${label}:chunk:${i}`),
    ledgerIndex: firstLedger,
    account: writer,
    succeeded: true,
    validated: true,
    memoTypeHex: chunkMarkerHex(i),
    memoDataHex: hex(chunk),
    memoFormatHex: null,
  }));
  const manifest = encodeManifest({
    type, size: bytes.length, chunk: CHUNK_BYTES, root, firstLedger, lastLedger: firstLedger,
    blocks: 1, lists: 0, cover, name: null,
  });
  transactions.push({
    hash: labelHash(label),
    ledgerIndex: firstLedger + 1,
    account: writer,
    succeeded: true,
    validated: true,
    memoTypeHex: MANIFEST_MARKER_HEX,
    memoDataHex: utf8Hex(manifest),
    memoFormatHex: JSON_FORMAT_HEX,
  });
  return { transactions, hash: labelHash(label) };
}
