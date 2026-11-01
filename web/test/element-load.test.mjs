import { test } from 'node:test';
import assert from 'node:assert/strict';
import { prepare, loadWhole, progressDetail } from '../src/element/load.mjs';
import { NodePool } from '../src/element/page.mjs';
import { parseLink } from '../src/link.mjs';
import { decodeManifest, encodeManifest } from '../src/manifest.mjs';
import { utf8Hex } from '../src/hex.mjs';
import {
  vectorFile, vectorLedger, vectorFileBytes, FakeSource, LINK, MANIFEST_HASH, smallFileLedger,
} from './vectors.mjs';

const link = parseLink(LINK);

function fakeClient(source) {
  return {
    transaction: (hash) => source.transaction(hash),
    accountTransactions: (...args) => source.accountTransactions(...args),
    earliestLedger: () => source.earliestLedger(),
    close() {},
  };
}

/** A handle that records which blocks were asked for and how. */
function spyOn(handle) {
  // A wrapper, not a subclass: FileHandle keeps its state in private fields.
  const spy = {
    blocksAsked: [],
    optionsAsked: [],
    record: () => handle.record(),
    cover: () => handle.cover(),
    entries: () => handle.entries(),
    blockBytes: () => handle.blockBytes(),
    block(index, options) {
      spy.blocksAsked.push(index);
      spy.optionsAsked.push(options);
      return handle.block(index, options);
    },
  };
  return spy;
}

function handleOver(source, link_ = link) {
  const lease = new NodePool(() => fakeClient(source)).acquire(['wss://a']);
  return lease.files.open(link_);
}

async function vectorHandle({ tamper = false } = {}) {
  const vector = await vectorFile(2500500);
  const ledger = await vectorLedger(vector);
  if (tamper) {
    // Chunk 2050 lies in block 2 (chunks 2048 and up); one changed nibble is enough.
    const chunk = ledger[2050];
    chunk.memoDataHex = (chunk.memoDataHex[0] === 'A' ? 'B' : 'A') + chunk.memoDataHex.slice(1);
  }
  const handle = handleOver(new FakeSource(ledger));
  return { vector, handle: spyOn(handle), record: await handle.record() };
}

test('loadWhole reports each verified block and returns the whole file', async () => {
  const { vector, handle, record } = await vectorHandle();
  const details = [];
  const bytes = await loadWhole(handle, record, { onProgress: (d) => details.push(d) });
  assert.deepEqual(bytes, await vectorFileBytes(vector.seed, vector.size));
  assert.deepEqual(details.at(-1), { bytesDone: 2500500, bytesTotal: 2500500, blocksVerified: 3, blocksTotal: 3 });
  assert.ok(details.every((d, i) => i === 0 || d.bytesDone >= details[i - 1].bytesDone));
  assert.ok(details.some((d) => d.blocksVerified === 1 && d.bytesDone === 1_024_000));
});

test('a limit reads only the blocks that cover it', async () => {
  const { handle, record } = await vectorHandle();
  const bytes = await loadWhole(handle, record, { limitBytes: 1_048_576 });
  assert.equal(bytes.length, 2_048_000);
  assert.deepEqual(handle.blocksAsked, [0, 1]);
});

test('every block is read in order through the sequential reader at the given priority', async () => {
  const { handle, record } = await vectorHandle();
  await loadWhole(handle, record, { priority: 0 });
  assert.deepEqual(handle.blocksAsked, [0, 1, 2]);
  assert.ok(handle.optionsAsked.every((o) => o.sequential === true && o.priority === 0));
});

test('a priority function is asked again before each block', async () => {
  const { handle, record } = await vectorHandle();
  let asked = 0;
  await loadWhole(handle, record, { priority: () => asked++ });
  assert.deepEqual(handle.optionsAsked.map((o) => o.priority), [0, 1, 2]);
});

test('a tampered block rejects with its index and returns nothing', async () => {
  const { handle, record } = await vectorHandle({ tamper: true });
  await assert.rejects(loadWhole(handle, record), (e) => e.code === 'BlockMismatch' && e.block === 2);
});

test('an aborted signal stops the load before any block is asked for', async () => {
  const { handle, record } = await vectorHandle();
  const controller = new AbortController();
  controller.abort();
  await assert.rejects(loadWhole(handle, record, { signal: controller.signal }), (e) => e.name === 'AbortError');
  assert.deepEqual(handle.blocksAsked, []);
});

test('a file of one block is loaded and reported complete', async () => {
  const bytes = new Uint8Array(1500).fill(7);
  const file = await smallFileLedger({ label: 'one', bytes, type: 'text/plain', firstLedger: 500 });
  const handle = handleOver(new FakeSource(file.transactions), { network: 'testnet', txHash: file.hash });
  const record = await handle.record();
  const details = [];
  const loaded = await loadWhole(handle, record, { onProgress: (d) => details.push(d) });
  assert.deepEqual(loaded, bytes);
  assert.deepEqual(details.at(-1), { bytesDone: 1500, bytesTotal: 1500, blocksVerified: 1, blocksTotal: 1 });
});

test('prepare reads the manifest and the cover and no block of the file', async () => {
  const webp = new Uint8Array(1500).fill(0x5a);
  webp.set([0x52, 0x49, 0x46, 0x46, 0x10, 0, 0, 0, 0x57, 0x45, 0x42, 0x50]);
  const cover = await smallFileLedger({ label: 'cover', bytes: webp, type: 'image/webp', firstLedger: 990 });
  const vector = await vectorFile(1001);
  const ledger = await vectorLedger(vector);
  const manifestTx = ledger.find((tx) => tx.hash === MANIFEST_HASH);
  manifestTx.memoDataHex = utf8Hex(encodeManifest({ ...decodeManifest(vector.manifest), cover: cover.hash }));
  const handle = spyOn(handleOver(new FakeSource([...ledger, ...cover.transactions])));

  const { record, cover: found } = await prepare(handle);
  assert.deepEqual(handle.blocksAsked, []);
  assert.equal(found.type, 'image/webp');
  assert.equal(record.manifest.size, 1001);
});

test('prepare without a cover in the manifest gives null for it', async () => {
  const { handle } = await vectorHandle();
  const { cover, record } = await prepare(handle);
  assert.equal(cover, null);
  assert.equal(record.manifest.blocks, 3);
});

test('progressDetail returns a fresh object with the four numbers only', () => {
  const input = { bytesDone: 1, bytesTotal: 2, blocksVerified: 3, blocksTotal: 4, extra: 'x' };
  const a = progressDetail(input);
  assert.deepEqual(a, { bytesDone: 1, bytesTotal: 2, blocksVerified: 3, blocksTotal: 4 });
  assert.notEqual(a, progressDetail(input));
  assert.notEqual(a, input);
});
