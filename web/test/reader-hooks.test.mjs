import { test } from 'node:test';
import assert from 'node:assert/strict';
import { XrplFileReader } from '../src/reader.mjs';
import { parseLink } from '../src/link.mjs';
import { vectorFile, vectorLedger, vectorFileBytes, FakeSource, LINK } from './vectors.mjs';
import { chunkIndexOf } from '../src/tree.mjs';

const link = parseLink(LINK);

// 1024001 bytes at 1000 per chunk: 1025 chunks, so block 0 is chunks 0..1023 and block 1 is chunk 1024.
const SIZE = 1024001;
const LAST_CHUNK = 1024;

async function fixture() {
  const vector = await vectorFile(SIZE);
  const source = new FakeSource(await vectorLedger(vector));
  return { vector, source };
}

// Records every hook call in one ordered list, so ordering between hooks can be asserted too.
function recorder() {
  const events = [];
  const hooks = {
    onManifest: (manifest) => events.push({ kind: 'manifest', manifest }),
    onChunk: ({ index, bytes, ledger }) => events.push({ kind: 'chunk', index, bytes, ledger }),
    onBlockVerified: (info) => events.push({ kind: 'block', ...info }),
  };
  return { events, hooks };
}

test('onChunk receives every chunk index once with its bytes', async () => {
  const { vector, source } = await fixture();
  const { events, hooks } = recorder();
  const result = await new XrplFileReader(source).read(link, () => {}, hooks);

  const chunks = events.filter((e) => e.kind === 'chunk');
  const indices = chunks.map((e) => e.index);
  assert.equal(indices.length, LAST_CHUNK + 1);
  assert.deepEqual([...indices].sort((a, b) => a - b), Array.from({ length: LAST_CHUNK + 1 }, (_, i) => i));

  const ordered = [...chunks].sort((a, b) => a.index - b.index);
  const joined = new Uint8Array(ordered.reduce((sum, c) => sum + c.bytes.length, 0));
  let at = 0;
  for (const c of ordered) { joined.set(c.bytes, at); at += c.bytes.length; }
  assert.deepEqual(joined, await vectorFileBytes(vector.seed, vector.size));
  assert.deepEqual(joined, result.bytes);
});

test('onChunk reports the ledger the chunk transaction was validated in', async () => {
  const { vector, source } = await fixture();
  const { events, hooks } = recorder();
  await new XrplFileReader(source).read(link, () => {}, hooks);

  const chunks = events.filter((e) => e.kind === 'chunk');
  assert.equal(chunks.length, LAST_CHUNK + 1);
  for (const c of chunks) {
    assert.equal(c.ledger, vector.ledger.first + Math.floor(c.index / 100), `chunk ${c.index}`);
  }
});

test('onBlockVerified fires once per block after all its chunks', async () => {
  const { source } = await fixture();
  const { events, hooks } = recorder();
  await new XrplFileReader(source).read(link, () => {}, hooks);

  const blocks = events.filter((e) => e.kind === 'block').map(({ block, firstChunk, lastChunk }) => ({ block, firstChunk, lastChunk }));
  assert.deepEqual(blocks, [
    { block: 0, firstChunk: 0, lastChunk: 1023 },
    { block: 1, firstChunk: 1024, lastChunk: LAST_CHUNK },
  ]);

  for (const verified of events.filter((e) => e.kind === 'block')) {
    const at = events.indexOf(verified);
    const inBlock = events.filter((e) => e.kind === 'chunk' && e.index >= verified.firstChunk && e.index <= verified.lastChunk);
    assert.equal(inBlock.length, verified.lastChunk - verified.firstChunk + 1);
    for (const chunk of inBlock) assert.ok(events.indexOf(chunk) < at, `chunk ${chunk.index} comes before block ${verified.block} is verified`);
  }
});

test('onManifest fires once, before the first chunk, with the manifest the read resolves with', async () => {
  const { source } = await fixture();
  const { events, hooks } = recorder();
  const result = await new XrplFileReader(source).read(link, () => {}, hooks);

  const manifests = events.filter((e) => e.kind === 'manifest');
  assert.equal(manifests.length, 1);
  assert.equal(manifests[0].manifest, result.manifest);
  assert.equal(events.indexOf(manifests[0]), 0);
});

test('a corrupt block fires no onBlockVerified for it', async () => {
  const { source } = await fixture();
  // Chunk 1500 would be block 1 of a longer file; here block 0 is the one to tamper with.
  source.transactions = source.transactions.map((tx) => (chunkIndexOf(tx.memoTypeHex) === 500 ? { ...tx, memoDataHex: 'F'.repeat(2000) } : tx));
  const { events, hooks } = recorder();

  await assert.rejects(new XrplFileReader(source).read(link, () => {}, hooks), (e) => e.code === 'BlockMismatch' && e.block === 0);
  assert.deepEqual(events.filter((e) => e.kind === 'block'), []);
});

test('a block that verifies is reported even when a later block is corrupt', async () => {
  const { source } = await fixture();
  source.transactions = source.transactions.map((tx) => (chunkIndexOf(tx.memoTypeHex) === LAST_CHUNK ? { ...tx, memoDataHex: 'F'.repeat(2000) } : tx));
  const { events, hooks } = recorder();

  await assert.rejects(new XrplFileReader(source).read(link, () => {}, hooks), (e) => e.code === 'BlockMismatch' && e.block === 1);
  assert.deepEqual(events.filter((e) => e.kind === 'block').map((e) => e.block), [0]);
});

test('read without hooks behaves as before', async () => {
  const { vector, source } = await fixture();
  const progress = [];
  const result = await new XrplFileReader(source).read(link, (p) => progress.push(p));
  assert.deepEqual(result.bytes, await vectorFileBytes(vector.seed, vector.size));
  assert.equal(progress.length, LAST_CHUNK + 1);
  assert.deepEqual(progress.at(-1), { chunksDone: LAST_CHUNK + 1, chunksTotal: LAST_CHUNK + 1 });

  const bare = await new XrplFileReader(source).read(link);
  assert.deepEqual(bare.bytes, result.bytes);
});
