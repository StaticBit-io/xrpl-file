import { test } from 'node:test';
import assert from 'node:assert/strict';
import { XrplFileReader } from '../src/reader.mjs';
import { parseLink } from '../src/link.mjs';
import { vectorFile, vectorLedger, FakeSource, LINK, WRITER, labelHash } from './vectors.mjs';
import { vectorBytes } from '../src/vector-stream.mjs';
import { chunkIndexOf, listIndexOf, isManifest, listMarkerHex } from '../src/tree.mjs';
import { utf8Hex, hexUtf8 } from '../src/hex.mjs';
import { MAINNET_FIRST_LEDGER } from '../src/ledger.mjs';

const link = parseLink(LINK);
const sourceFor = async (size) => new FakeSource(await vectorLedger(await vectorFile(size)));
const rejectsWith = (promise, code, block) =>
  assert.rejects(promise, (e) => e.code === code && (block === undefined || e.block === block));

for (const size of [1, 1000, 1001, 1024001, 2500500]) {
  test(`a ${size}-byte vector reads back byte for byte`, async () => {
    const vector = await vectorFile(size);
    const result = await new XrplFileReader(await sourceFor(size)).read(link);
    assert.deepEqual(result.bytes, await vectorBytes(vector.seed, vector.size));
    assert.equal(result.manifest.root, vector.root);
    assert.equal(result.writer, WRITER);
    assert.equal(result.blocks.length, vector.blocks);
  });
}

test('a single block file needs no list read', async () => {
  const source = await sourceFor(1001);
  const reader = new XrplFileReader(source);
  const record = await reader.manifest(link);
  const blocks = await reader.blockList(record);
  assert.deepEqual(blocks, [{ i: 0, h: record.manifest.root, l: record.manifest.firstLedger }]);
  assert.equal(source.accountTransactionsCalls, 0);
});

test('a duplicate index keeps the earlier copy', async () => {
  // 1024001 bytes: block 0 spans ledgers 1000..1010 and needs all 1024 of its chunks before the
  // scan can stop, so a duplicate placed at ledger 1005 -- strictly inside that range -- is one
  // the reader must actually pass through and skip, not one it never reaches because the block
  // already completed on the file's first couple of chunks (as the 1001-byte vector would let it).
  const vector = await vectorFile(1024001);
  const ledger = await vectorLedger(vector);
  const real = ledger.find((tx) => chunkIndexOf(tx.memoTypeHex) === 0);
  const duplicate = { ...real, hash: labelHash('retry'), ledgerIndex: 1005, memoDataHex: 'F'.repeat(real.memoDataHex.length) };
  const source = new FakeSource([...ledger, duplicate]);
  assert.deepEqual((await new XrplFileReader(source).read(link)).bytes, await vectorBytes(vector.seed, vector.size));
});

test('a corrupt chunk is reported with its block', async () => {
  const source = await sourceFor(2500500);
  source.transactions = source.transactions.map((tx) => (chunkIndexOf(tx.memoTypeHex) === 1500 ? { ...tx, memoDataHex: 'F'.repeat(2000) } : tx));
  await rejectsWith(new XrplFileReader(source).read(link), 'BlockMismatch', 1);
});

test('a missing chunk is reported with its block', async () => {
  const source = await sourceFor(2500500);
  source.transactions = source.transactions.filter((tx) => chunkIndexOf(tx.memoTypeHex) !== 2400);
  await rejectsWith(new XrplFileReader(source).read(link), 'ChunkMissing', 2);
});

test('an oversized chunk is skipped so a later real copy still wins', async () => {
  // manifest.chunk is 1000 for this vector; a memo decoding to 2000 bytes is well over that and
  // must be treated like a non-hex memo -- skipped, not accepted into the slot.
  const vector = await vectorFile(1024001);
  const ledger = await vectorLedger(vector);
  const real = ledger.find((tx) => chunkIndexOf(tx.memoTypeHex) === 0);
  const withOversizedFirstCopy = ledger.map((tx) => (tx === real ? { ...tx, memoDataHex: 'A'.repeat(4000) } : tx));
  // The good copy, later in canonical order, still inside block 0's range (1000..1010).
  const source = new FakeSource([...withOversizedFirstCopy, { ...real, hash: labelHash('good-later'), ledgerIndex: 1005 }]);
  assert.deepEqual((await new XrplFileReader(source).read(link)).bytes, await vectorBytes(vector.seed, vector.size));
});

test('a failed, foreign or unmarked transaction is not a chunk', async () => {
  // Same 1024001-byte vector and ledger-1005 placement as above, and for the same reason: block 0
  // needs all its chunks before the scan can stop, so these injected transactions are met mid-block.
  const vector = await vectorFile(1024001);
  const ledger = await vectorLedger(vector);
  const real = ledger.find((tx) => chunkIndexOf(tx.memoTypeHex) === 500);
  const source = new FakeSource([
    ...ledger,
    { ...real, hash: labelHash('failed'), ledgerIndex: 1005, succeeded: false, memoDataHex: '00' },
    { ...real, hash: labelHash('stranger'), ledgerIndex: 1005, account: 'rSomebodyElse', memoDataHex: '00' },
    { ...real, hash: labelHash('unmarked'), ledgerIndex: 1005, memoTypeHex: null, memoDataHex: '00' },
  ]);
  assert.deepEqual((await new XrplFileReader(source).read(link)).bytes, await vectorBytes(vector.seed, vector.size));
});

test('a list that does not match the root is refused before any chunk', async () => {
  const source = await sourceFor(2500500);
  source.transactions = source.transactions.map((tx) => (listIndexOf(tx.memoTypeHex) === 0
    ? { ...tx, memoDataHex: utf8Hex(hexUtf8(tx.memoDataHex).replaceAll('A', 'B')) }
    : tx));
  await rejectsWith(new XrplFileReader(source).read(link), 'ListMismatch');
});

test('a stray out-of-range list memo is a list mismatch, not a crash', async () => {
  // The manifest promises a fixed number of list memos indexed 0..lists-1. Re-marking the single
  // list memo xf/b/7 leaves the account with a stray out-of-range index and none of the indices
  // the manifest actually promises -- without the range filter this reaches decodeEntries as
  // undefined and blows up instead of failing cleanly as ListMismatch.
  const source = await sourceFor(2500500);
  source.transactions = source.transactions.map((tx) => (listIndexOf(tx.memoTypeHex) === 0 ? { ...tx, memoTypeHex: listMarkerHex(7) } : tx));
  await rejectsWith(new XrplFileReader(source).read(link), 'ListMismatch');
});

test('an unknown manifest is not found on a full-history node and a history problem on a partial one', async () => {
  await rejectsWith(new XrplFileReader(new FakeSource([], MAINNET_FIRST_LEDGER)).manifest(link), 'ManifestNotFound');
  await rejectsWith(new XrplFileReader(new FakeSource([], 100_000_000)).manifest(link), 'NodeLacksHistory');
});

test('a node whose history starts after the first chunk cannot read the file', async () => {
  const source = await sourceFor(1001);
  source.earliest = 1001;
  await rejectsWith(new XrplFileReader(source).read(link), 'NodeLacksHistory');
});

test('a manifest whose counts disagree is corrupt, and a non-manifest is not found', async () => {
  const corrupt = await sourceFor(1001);
  corrupt.transactions = corrupt.transactions.map((tx) => (isManifest(tx.memoTypeHex) ? { ...tx, memoDataHex: utf8Hex('not json') } : tx));
  await rejectsWith(new XrplFileReader(corrupt).manifest(link), 'CorruptManifest');

  const other = await sourceFor(1001);
  other.transactions = other.transactions.map((tx) => (isManifest(tx.memoTypeHex) ? { ...tx, memoTypeHex: utf8Hex('xf/c/5') } : tx));
  await rejectsWith(new XrplFileReader(other).manifest(link), 'ManifestNotFound');
});

test('a file above the ceiling is refused before any chunk is read', async () => {
  const source = await sourceFor(2500500);
  await rejectsWith(new XrplFileReader(source, { maxBytes: 2_000_000 }).read(link), 'TooLarge');
  assert.equal(source.accountTransactionsCalls, 0);
});

test('progress counts chunks as they land', async () => {
  const seen = [];
  await new XrplFileReader(await sourceFor(2500500)).read(link, (p) => seen.push(p));
  assert.equal(seen.at(-1).chunksDone, 2501);
  assert.equal(seen.at(-1).chunksTotal, 2501);
});
