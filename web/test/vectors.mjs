import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { vectorBytes } from '../src/vector-stream.mjs';
import { split, chunkMarkerHex, listMarkerHex, MANIFEST_MARKER_HEX, JSON_FORMAT_HEX } from '../src/tree.mjs';
import { hex, utf8Hex } from '../src/hex.mjs';

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

/** The vector's transactions laid out in the synthetic ledger the vector file describes. */
export async function vectorLedger(vector) {
  const bytes = await vectorBytes(vector.seed, vector.size);
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
    this.accountTransactionsCalls = 0;
  }

  async transaction(hash) {
    return this.transactions.find((tx) => tx.hash.toUpperCase() === hash.toUpperCase()) ?? null;
  }

  async *accountTransactions(account, fromLedger, toLedger, forward) {
    this.accountTransactionsCalls++;
    const matching = this.transactions
      .map((tx, position) => ({ tx, position }))
      .filter(({ tx }) => tx.account === account && tx.ledgerIndex >= fromLedger && tx.ledgerIndex <= toLedger)
      .sort((a, b) => (a.tx.ledgerIndex - b.tx.ledgerIndex) || (a.position - b.position));
    if (!forward) matching.reverse();
    for (const { tx } of matching) yield tx;
  }

  async earliestLedger() {
    return this.earliest;
  }
}
