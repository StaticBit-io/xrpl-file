import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  NodePool, ReadQueue, AbortableSource, SharedSource, PRIORITY, PAGE_HISTORY_RECORDS, pool as pagePool,
} from '../src/element/page.mjs';
import { parseLink, XrplFileError } from '../src/link.mjs';
import { chunkIndexOf } from '../src/tree.mjs';
import { decodeManifest, encodeManifest } from '../src/manifest.mjs';
import { utf8Hex } from '../src/hex.mjs';
import {
  vectorFile, vectorLedger, vectorFileBytes, FakeSource, LINK, MANIFEST_HASH, WRITER, smallFileLedger,
} from './vectors.mjs';

const link = parseLink(LINK);
const microtask = () => Promise.resolve();
const settle = () => new Promise((resolve) => setImmediate(resolve));
const BLOCK = 1_024_000;

/** A client over `source` that counts close(). */
function fakeClient(source) {
  return {
    closed: 0,
    transaction: (hash) => source.transaction(hash),
    accountTransactions: (...args) => source.accountTransactions(...args),
    earliestLedger: () => source.earliestLedger(),
    close() { this.closed++; },
  };
}

function poolOver(source = new FakeSource()) {
  const created = [];
  const pool = new NodePool((endpoints) => {
    const client = fakeClient(source);
    client.endpoints = endpoints;
    created.push(client);
    return client;
  });
  return { pool, created };
}

/** The 2,500,500-byte vector (three blocks) behind a pool, with a lease and two handles of its link. */
async function vectorPage() {
  const vector = await vectorFile(2500500);
  const source = new FakeSource(await vectorLedger(vector));
  const bytes = await vectorFileBytes(vector.seed, vector.size);
  const { pool, created } = poolOver(source);
  const lease = pool.acquire(['wss://a']);
  const blocks = [bytes.slice(0, BLOCK), bytes.slice(BLOCK, 2 * BLOCK), bytes.slice(2 * BLOCK)];
  return { source, bytes, blocks, pool, created, lease, h1: lease.files.open(link), h2: lease.files.open(link) };
}

async function readAll(handle) {
  const entries = await handle.entries();
  const parts = [];
  for (let i = 0; i < entries.length; i++) parts.push(await handle.block(i, { sequential: true }));
  return Buffer.concat(parts);
}

function gate() {
  let open;
  const promise = new Promise((resolve) => { open = resolve; });
  return { promise, open };
}

/** Fills the queue with `count` tasks that run until their gate opens. */
function fill(queue, count, priority = PRIORITY.BACKGROUND) {
  const gates = [];
  for (let i = 0; i < count; i++) {
    const g = gate();
    gates.push(g);
    queue.run(() => g.promise, { priority });
  }
  return () => gates.forEach((g) => g.open());
}

// NodePool

test('the same endpoints share one client, closed after the last release', async () => {
  const { pool, created } = poolOver();
  const lease1 = pool.acquire(['wss://a']);
  const lease2 = pool.acquire(['wss://a']);
  assert.equal(created.length, 1);
  lease1.release(); lease1.release(); await microtask();
  assert.equal(created[0].closed, 0);
  lease2.release(); await microtask();
  assert.equal(created[0].closed, 1);
  assert.equal(pool.size, 0);
});

test('a release followed by an acquire in the same tick keeps the client', async () => {
  const { pool, created } = poolOver();
  const lease = pool.acquire(['wss://a']);
  lease.release();
  const again = pool.acquire(['wss://a']);
  await microtask();
  assert.equal(created.length, 1);
  assert.equal(created[0].closed, 0);
  again.release();
});

test('an acquire after the closing microtask gets a new client', async () => {
  const { pool, created } = poolOver();
  pool.acquire(['wss://a']).release();
  await microtask();
  pool.acquire(['wss://a']);
  assert.equal(created.length, 2);
  assert.equal(created[0].closed, 1);
});

test('different endpoint sets get different clients', () => {
  const { pool, created } = poolOver();
  pool.acquire(['wss://a']); pool.acquire(['wss://b']); pool.acquire(['wss://a', 'wss://b']);
  assert.equal(created.length, 3);
  assert.equal(pool.size, 3);
});

test('the module pool is a pool', () => {
  assert.ok(pagePool instanceof NodePool);
});

// FileRegistry

test('two handles of one link share the manifest request and each block read', async () => {
  const { source, bytes, h1, h2 } = await vectorPage();
  const [b1, b2] = await Promise.all([readAll(h1), readAll(h2)]);
  assert.equal(source.transactionCalls, 1);
  assert.equal(source.calls.filter((c) => c.forward && c.fromLedger === 1000).length, 1);
  assert.deepEqual(b1, Buffer.from(bytes));
  assert.deepEqual(b2, Buffer.from(bytes));
});

test('releasing one handle mid-read leaves the other\'s read intact', async () => {
  const { blocks, h1, h2, created } = await vectorPage();
  const p = h2.block(1, {});
  h1.release();
  assert.deepEqual(await p, blocks[1]);
  assert.equal(created[0].closed, 0);
});

test('a handle released and reopened in the same tick keeps its verified blocks', async () => {
  const { source, lease, h1, h2 } = await vectorPage();
  h2.release();
  await h1.block(0, {});
  h1.release();
  const again = lease.files.open(link);
  await microtask();
  const before = source.yielded;
  await again.block(0, {});
  assert.equal(source.yielded, before);
});

test('a link whose last handle went is read afresh', async () => {
  const { source, lease, h1, h2 } = await vectorPage();
  await h1.block(0, {});
  h1.release(); h2.release();
  await microtask();
  const before = source.yielded;
  await lease.files.open(link).block(0, {});
  assert.ok(source.yielded > before);
});

test('a shared read is aborted only when every consumer aborted', async () => {
  const { blocks, source, h1, h2 } = await vectorPage();
  const c1 = new AbortController();
  const c2 = new AbortController();
  // The first chunk is the point by which both consumers have joined the one read.
  let aborted = false;
  const p1 = h1.block(2, { signal: c1.signal });
  const p2 = h2.block(2, { signal: c2.signal, onChunk: () => { if (!aborted) { aborted = true; c1.abort(); } } });
  await assert.rejects(p1, { name: 'AbortError' });
  assert.deepEqual(await p2, blocks[2]);
  assert.equal(source.calls.filter((c) => c.forward).length, 1);
});

test('an abort that lands between the awaits of block() still rejects the consumer', async () => {
  const { h1 } = await vectorPage();
  await h1.entries();
  const c = new AbortController();
  const p = h1.block(1, { signal: c.signal });
  Promise.resolve().then(() => c.abort());
  await assert.rejects(p, { name: 'AbortError' });
});

test('an abort in that gap releases the hold of the consumer on the shared read', async () => {
  const { source, h1, h2 } = await vectorPage();
  await h1.entries();
  const c1 = new AbortController();
  const c2 = new AbortController();
  const p1 = h1.block(1, { signal: c1.signal });
  Promise.resolve().then(() => c1.abort());
  const p2 = h2.block(1, { signal: c2.signal, onChunk: () => c2.abort() });
  await assert.rejects(p1, { name: 'AbortError' });
  await assert.rejects(p2, { name: 'AbortError' });
  await settle();
  assert.ok(source.yielded < 100, `the walk went on for ${source.yielded} records`);
});

test('a consumer that joins after every other one aborted gets a fresh read', async () => {
  const { blocks, source, h1, h2 } = await vectorPage();
  const c = new AbortController();
  const p1 = h1.block(1, { signal: c.signal, onChunk: () => c.abort() });
  await assert.rejects(p1, { name: 'AbortError' });
  assert.deepEqual(await h2.block(1, {}), blocks[1]);
  assert.ok(source.calls.filter((call) => call.forward).length >= 2);
});

test('a read nobody waits for any more stops its walk and is not kept', async () => {
  const { blocks, source, h1, h2 } = await vectorPage();
  const c1 = new AbortController();
  const c2 = new AbortController();
  const onChunk = () => { c1.abort(); c2.abort(); };
  const p1 = h1.block(1, { signal: c1.signal, onChunk });
  const p2 = h2.block(1, { signal: c2.signal });
  await assert.rejects(p1, { name: 'AbortError' });
  await assert.rejects(p2, { name: 'AbortError' });
  await settle();
  assert.ok(source.yielded < 100, `the walk went on for ${source.yielded} records`);
  assert.deepEqual(await h1.block(1, {}), blocks[1]);
});

test('a consumer that joins a read late is told of the chunks it missed', async () => {
  const { h1, h2 } = await vectorPage();
  let first = 0;
  let second = 0;
  let late;
  const p1 = h1.block(0, {
    onChunk: () => { if (++first === 10) late = h2.block(0, { onChunk: () => { second++; } }); },
  });
  await p1;
  await late;
  assert.equal(first, 1024);
  assert.equal(second, 1024);
});

test('a block already verified reports its chunks too, and every consumer gets its own copy', async () => {
  const { blocks, h1 } = await vectorPage();
  const first = await h1.block(2, {});
  let chunks = 0;
  const again = await h1.block(2, { onChunk: () => chunks++ });
  assert.equal(chunks, Math.ceil(blocks[2].length / 1000));
  assert.deepEqual(again, first);
  again[0] ^= 0xff;
  assert.deepEqual(await h1.block(2, {}), blocks[2]);
});

test('a throwing progress callback does not fail the read the others share', async () => {
  const { blocks, h1, h2 } = await vectorPage();
  const p1 = h1.block(2, { onChunk: () => { throw new Error('a consumer'); } });
  const p2 = h2.block(2, {});
  assert.deepEqual(await p1, blocks[2]);
  assert.deepEqual(await p2, blocks[2]);
});

test('the last release aborts what is still being read', async () => {
  const { source, h1, h2 } = await vectorPage();
  h2.release();
  let released = false;
  const p = h1.block(1, { onChunk: () => { if (!released) { released = true; h1.release(); } } });
  await assert.rejects(p, { name: 'AbortError' });
  await settle();
  assert.ok(source.yielded < 100);
});

test('a handle released before its manifest arrived reads nothing', async () => {
  const { source, h1, h2 } = await vectorPage();
  const p = h1.block(0, {});
  h1.release(); h2.release();
  await assert.rejects(p, { name: 'AbortError' });
  await settle();
  assert.equal(source.calls.filter((c) => c.forward).length, 0);
});

test('a released handle refuses further work', async () => {
  const { h1 } = await vectorPage();
  h1.release(); h1.release();
  await assert.rejects(h1.block(0, {}), { name: 'AbortError' });
  await assert.rejects(h1.record(), { name: 'AbortError' });
});

test('a block asks the node for the history before the list or any block', async () => {
  const vector = await vectorFile(2500500);
  const lacking = new FakeSource(await vectorLedger(vector), 1001);
  const lease = poolOver(lacking).pool.acquire(['wss://a']);
  await assert.rejects(lease.files.open(link).block(0, {}), (e) => e.code === 'NodeLacksHistory');
  assert.equal(lacking.calls.length, 0);
});

test('a node without the history is also reported by entries(), and asked again next time', async () => {
  const vector = await vectorFile(2500500);
  const source = new FakeSource(await vectorLedger(vector), 1001);
  const handle = poolOver(source).pool.acquire(['wss://a']).files.open(link);
  await assert.rejects(handle.entries(), (e) => e.code === 'NodeLacksHistory');
  source.earliest = 1;
  assert.equal((await handle.entries()).length, 3);
});

test('blockBytes is one block of the manifest\'s chunk size', async () => {
  const { h1 } = await vectorPage();
  assert.equal(await h1.blockBytes(), 1024 * 1000);
});

test('a block past the end of the list is a range error', async () => {
  const { h1 } = await vectorPage();
  await assert.rejects(h1.block(3, {}), RangeError);
});

test('a tampered block rejects with BlockMismatch and is read again next time', async () => {
  const { source, blocks, h1 } = await vectorPage();
  const tx = source.transactions.find((t) => chunkIndexOf(t.memoTypeHex) === 1500);
  const original = tx.memoDataHex;
  tx.memoDataHex = (original[0] === 'A' ? 'B' : 'A') + original.slice(1);
  await assert.rejects(h1.block(1, {}), (e) => e.code === 'BlockMismatch' && e.block === 1);
  tx.memoDataHex = original;
  assert.deepEqual(await h1.block(1, {}), blocks[1]);
});

test('blocks read in order go through the page\'s reader, one walk for the whole file', async () => {
  const { source, h1 } = await vectorPage();
  await readAll(h1);
  assert.equal(source.calls.filter((c) => c.forward).length, 1);
});

test('blocks that may be cancelled each walk from their own entry', async () => {
  const { source, blocks, h1 } = await vectorPage();
  assert.deepEqual(await h1.block(2, {}), blocks[2]);
  assert.deepEqual(await h1.block(1, {}), blocks[1]);
  assert.deepEqual(source.calls.filter((c) => c.forward).map((c) => c.fromLedger), [1020, 1010]);
});

test('the page\'s history cache is one block\'s worth', () => {
  assert.equal(PAGE_HISTORY_RECORDS, 1024);
});

test('a read asked for again at a higher priority runs at that priority', async () => {
  const { blocks, lease, h1, h2 } = await vectorPage();
  const release = fill(lease.queue, 3);
  await h1.entries();
  const slow = h1.block(0, { priority: PRIORITY.BACKGROUND });
  await settle();
  assert.equal(lease.queue.waiting, 1);
  const urgent = h2.block(0, { priority: PRIORITY.NOW });
  await settle();
  assert.equal(lease.queue.waiting, 0);
  assert.equal(lease.queue.active, 4);
  release();
  assert.deepEqual(await slow, blocks[0]);
  assert.deepEqual(await urgent, blocks[0]);
});

test('a link\'s manifest failing once is asked for again', async () => {
  const vector = await vectorFile(1001);
  const source = new FakeSource(await vectorLedger(vector));
  const real = source.transaction.bind(source);
  let fail = true;
  source.transaction = async (hash) => {
    if (fail) { fail = false; throw new XrplFileError('NodeUnreachable', 'fake'); }
    return real(hash);
  };
  const handle = poolOver(source).pool.acquire(['wss://a']).files.open(link);
  await assert.rejects(handle.record(), (e) => e.code === 'NodeUnreachable');
  assert.equal((await handle.record()).manifest.size, 1001);
});

// cover

const ascii = (text) => [...text].map((c) => c.charCodeAt(0));
const WEBP = new Uint8Array(1500).fill(0x5a);
WEBP.set([...ascii('RIFF'), 0x10, 0, 0, 0, ...ascii('WEBP'), ...ascii('VP8 ')]);

async function decoratedPage() {
  const vector = await vectorFile(1001);
  const ledger = await vectorLedger(vector);
  const cover = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990 });
  const manifestTx = ledger.find((tx) => tx.hash === MANIFEST_HASH);
  manifestTx.memoDataHex = utf8Hex(encodeManifest({ ...decodeManifest(vector.manifest), cover: cover.hash }));
  const source = new FakeSource([...ledger, ...cover.transactions]);
  const lease = poolOver(source).pool.acquire(['wss://a']);
  return { source, cover, lease, h1: lease.files.open(link), h2: lease.files.open(link) };
}

test('cover() reads the cover once for every handle of the link', async () => {
  const { source, cover, h1, h2 } = await decoratedPage();
  const [c1, c2] = await Promise.all([h1.cover(), h2.cover()]);
  assert.equal(c1.type, 'image/webp');
  assert.equal(c1.txHash, cover.hash);
  assert.deepEqual(c1.bytes, WEBP);
  assert.strictEqual(c1, c2);
  assert.equal(source.calls.length, 1);
});

test('cover() is null for a file without one', async () => {
  const { h1 } = await vectorPage();
  assert.equal(await h1.cover(), null);
});

// SharedSource

test('SharedSource shares a transaction request in flight and keeps a validated answer', async () => {
  const source = new FakeSource(await vectorLedger(await vectorFile(1001)));
  const shared = new SharedSource(fakeClient(source));
  const [a, b] = await Promise.all([shared.transaction(MANIFEST_HASH), shared.transaction(MANIFEST_HASH.toLowerCase())]);
  assert.strictEqual(a, b);
  await shared.transaction(MANIFEST_HASH);
  assert.equal(source.transactionCalls, 1);
});

test('SharedSource asks again for what is unknown, unvalidated or failed', async () => {
  const source = new FakeSource([{ hash: 'AA', ledgerIndex: 5, account: WRITER, succeeded: true, validated: false }]);
  const shared = new SharedSource(fakeClient(source));
  assert.equal(await shared.transaction('BB'), null);
  await shared.transaction('BB');
  await shared.transaction('AA');
  await shared.transaction('AA');
  assert.equal(source.transactionCalls, 4);

  const throwing = new SharedSource({ transaction() { throw new Error('at once'); } });
  await assert.rejects(throwing.transaction('CC'), /at once/);
  await assert.rejects(throwing.transaction('CC'), /at once/);
});

test('SharedSource shares an earliestLedger request in flight and passes walks through', async () => {
  const source = new FakeSource([], 7);
  const shared = new SharedSource(fakeClient(source));
  let asked = 0;
  const client = fakeClient(source);
  client.earliestLedger = async () => { asked++; return 7; };
  const sharedOver = new SharedSource(client);
  assert.deepEqual(await Promise.all([sharedOver.earliestLedger(), sharedOver.earliestLedger()]), [7, 7]);
  assert.equal(asked, 1);
  await sharedOver.earliestLedger();
  assert.equal(asked, 2);

  const seen = [];
  for await (const tx of shared.accountTransactions(WRITER, 1, 2, true)) seen.push(tx);
  assert.deepEqual(seen, []);
  assert.deepEqual(source.calls, [{ account: WRITER, fromLedger: 1, toLedger: 2, forward: true }]);
});

// ReadQueue

test('the queue runs at most three tasks and lower priority values first', async () => {
  const queue = new ReadQueue();
  const started = [];
  const gates = [];
  let most = 0;
  [2, 2, 2, 2, 1, 2].forEach((priority, label) => {
    const g = gate();
    gates.push(g);
    queue.run(async () => { started.push(label); most = Math.max(most, queue.active); await g.promise; }, { priority });
  });
  assert.deepEqual(started, [0, 1, 2]);
  assert.equal(queue.waiting, 3);
  for (const g of gates) { g.open(); await settle(); }
  assert.deepEqual(started, [0, 1, 2, 4, 3, 5]);
  assert.equal(most, 3);
  assert.equal(queue.active, 0);
});

test('a NOW task starts at once even when three are running, and only one such', async () => {
  const queue = new ReadQueue();
  const open = fill(queue, 3);
  assert.equal(queue.active, 3);

  const now = gate();
  const started = [];
  queue.run(async () => { started.push('now'); await now.promise; }, { priority: PRIORITY.NOW });
  assert.equal(queue.active, 4);
  assert.deepEqual(started, ['now']);

  queue.run(async () => { started.push('second'); }, { priority: PRIORITY.NOW });
  queue.run(async () => { started.push('background'); }, { priority: PRIORITY.BACKGROUND });
  assert.equal(queue.waiting, 2);
  assert.deepEqual(started, ['now']);

  open();
  await settle();
  assert.deepEqual(started, ['now', 'second', 'background']);
  now.open();
  await settle();
  assert.equal(queue.active, 0);
});

test('the reserved slot is not for background tasks', async () => {
  const queue = new ReadQueue();
  const open = fill(queue, 3);
  const now = gate();
  queue.run(() => now.promise, { priority: PRIORITY.NOW });
  let started = false;
  queue.run(async () => { started = true; }, { priority: PRIORITY.AHEAD });
  now.open();
  await settle();
  assert.equal(started, false);
  assert.equal(queue.active, 3);
  open();
  await settle();
  assert.equal(started, true);
});

test('an aborted queued task rejects with AbortError and never starts', async () => {
  const queue = new ReadQueue();
  const release = fill(queue, 3);
  const c = new AbortController();
  let started = false;
  const p = queue.run(async () => { started = true; }, { signal: c.signal });
  assert.equal(queue.waiting, 1);
  c.abort();
  await assert.rejects(p, { name: 'AbortError' });
  assert.equal(queue.waiting, 0);
  release();
  await settle();
  assert.equal(started, false);
});

test('a task that is given an aborted signal does not start', async () => {
  const queue = new ReadQueue();
  const c = new AbortController();
  c.abort();
  let started = false;
  await assert.rejects(queue.run(async () => { started = true; }, { signal: c.signal }), { name: 'AbortError' });
  assert.equal(started, false);
});

test('a running task receives the signal, and a failing task gives its slot back', async () => {
  const queue = new ReadQueue(1);
  const c = new AbortController();
  assert.strictEqual(await queue.run(async (signal) => signal, { signal: c.signal }), c.signal);
  assert.equal((await queue.run(async (signal) => signal.aborted)), false);
  await assert.rejects(queue.run(() => { throw new Error('sync'); }), /sync/);
  await assert.rejects(queue.run(async () => { throw new Error('async'); }), /async/);
  assert.equal(queue.active, 0);
  assert.equal(await queue.run(async () => 'next'), 'next');
});

test('a queue runs at least one task', () => {
  assert.throws(() => new ReadQueue(0), RangeError);
});

// AbortableSource

function pagedSource() {
  return {
    returned: false,
    calls: 0,
    async transaction(hash) { return { hash }; },
    async earliestLedger() { return 5; },
    async *accountTransactions() {
      this.calls++;
      try { for (let i = 0; i < 5; i++) yield { i }; } finally { this.returned = true; }
    },
  };
}

test('an aborted walk ends between pages', async () => {
  const paged = pagedSource();
  const c = new AbortController();
  const walk = new AbortableSource(paged, c.signal).accountTransactions(WRITER, 1000, 1026, true);
  await walk.next();
  c.abort();
  await assert.rejects(walk.next(), { name: 'AbortError' });
  assert.equal(paged.returned, true);
});

test('a walk that is not aborted runs to its end, and transaction and earliestLedger pass through', async () => {
  const paged = pagedSource();
  const source = new AbortableSource(paged, new AbortController().signal);
  const seen = [];
  for await (const tx of source.accountTransactions(WRITER, 1, 2, true)) seen.push(tx.i);
  assert.deepEqual(seen, [0, 1, 2, 3, 4]);
  assert.equal((await source.transaction('AB')).hash, 'AB');
  assert.equal(await source.earliestLedger(), 5);
});

test('a walk started after the abort asks the node for nothing', async () => {
  const paged = pagedSource();
  const c = new AbortController();
  c.abort();
  await assert.rejects(new AbortableSource(paged, c.signal).accountTransactions(WRITER, 1, 2, true).next(), { name: 'AbortError' });
  assert.equal(paged.calls, 0);
});

test('a consumer that stops early closes the underlying walk', async () => {
  const paged = pagedSource();
  for await (const tx of new AbortableSource(paged, new AbortController().signal).accountTransactions(WRITER, 1, 2, true)) {
    if (tx.i === 1) break;
  }
  assert.equal(paged.returned, true);
});
