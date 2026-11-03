import test from 'node:test';
import assert from 'node:assert/strict';

const { CUBE_CAP, startElementRead, seedBytes } = await import('../src/js/element-read.mjs');

const LINK = 'xrpl-file://testnet/2AEB485BB0FAECA2DF4B60F196D5199042288FEB124ED8D95F4313CF06200DB4';

function setup() {
  const element = new EventTarget();
  const calls = [];
  const listener = {};
  for (const name of ['onPhase', 'onManifest', 'onChunk', 'onBlockVerified', 'onDone', 'onFailed']) {
    listener[name] = (arg) => calls.push([name, arg]);
  }
  let time = 0;
  const read = startElementRead({ link: LINK, listener }, { element, now: () => (time += 1) });
  const send = (type, detail) => element.dispatchEvent(
    detail === undefined ? new Event(type) : new CustomEvent(type, { detail }),
  );
  const manifest = (count) => send('xrpl-file-manifest', {
    size: count * 1000, chunk: 1000, chunks: count, blocks: Math.ceil(count / 4), firstLedger: 100, lastLedger: 900,
  });
  const named = (name) => calls.filter(([n]) => n === name).map(([, arg]) => arg);
  return { element, read, send, manifest, calls, named };
}

test('123 chunks make 123 cubes, perCube 1', () => {
  const t = setup();
  t.manifest(123);
  const [m] = t.named('onManifest');
  assert.equal(m.chunks, 123);
  assert.equal(m.fileChunks, 123);
  assert.equal(m.perCube, 1);
  assert.equal(m.manifest.firstLedger, 100);
  assert.equal(typeof m.time, 'number');
});

test('20000 chunks make 239 cubes of 84', () => {
  const t = setup();
  t.manifest(20000);
  const [m] = t.named('onManifest');
  assert.equal(CUBE_CAP, 240);
  assert.equal(m.perCube, 84);
  assert.equal(m.chunks, 239);
  assert.equal(m.fileChunks, 20000);
});

test('a second manifest is ignored', () => {
  const t = setup();
  t.manifest(10);
  t.manifest(500);
  assert.equal(t.named('onManifest').length, 1);
  assert.equal(t.read.manifest.chunks, 10);
});

test('a cube appears on the ledger of the first chunk to arrive', () => {
  const t = setup();
  t.manifest(20000);
  t.send('xrpl-file-chunk', { index: 83, ledger: 900 });
  t.send('xrpl-file-chunk', { index: 0, ledger: 100 });
  const chunks = t.named('onChunk');
  assert.equal(chunks.length, 1);
  assert.equal(chunks[0].index, 0);
  assert.equal(chunks[0].ledger, 900);
  assert.deepEqual(chunks[0].bytes, seedBytes(0, 900));
});

test('chunks of different cubes each appear once, and none before the manifest', () => {
  const t = setup();
  t.send('xrpl-file-chunk', { index: 0, ledger: 100 });
  assert.equal(t.named('onChunk').length, 0);
  t.manifest(10);
  t.send('xrpl-file-chunk', { index: 3, ledger: 120 });
  t.send('xrpl-file-chunk', { index: 4, ledger: 130 });
  t.send('xrpl-file-chunk', { index: 3, ledger: 120 });
  assert.deepEqual(t.named('onChunk').map((c) => c.index), [3, 4]);
});

test('the last smaller cube turns lime', () => {
  const even = setup();
  even.manifest(250);
  const [m] = even.named('onManifest');
  assert.equal(m.perCube, 2);
  assert.equal(m.chunks, 125);
  even.send('xrpl-file-block', { index: 0, firstChunk: 248, lastChunk: 249 });
  assert.deepEqual(even.named('onBlockVerified').map((b) => [b.firstChunk, b.lastChunk]), [[124, 124]]);

  const odd = setup();
  odd.manifest(241);
  const [n] = odd.named('onManifest');
  assert.equal(n.perCube, 2);
  assert.equal(n.chunks, 121);
  odd.send('xrpl-file-block', { index: 0, firstChunk: 240, lastChunk: 240 });
  assert.deepEqual(odd.named('onBlockVerified').map((b) => [b.firstChunk, b.lastChunk]), [[120, 120]]);
});

test('a cube is not lime until every chunk of it is verified', () => {
  const t = setup();
  t.manifest(250);
  t.send('xrpl-file-block', { index: 0, firstChunk: 1, lastChunk: 2 });
  assert.deepEqual(t.named('onBlockVerified').map((b) => b.firstChunk), []);
  t.send('xrpl-file-block', { index: 1, firstChunk: 0, lastChunk: 0 });
  assert.deepEqual(t.named('onBlockVerified').map((b) => b.firstChunk), [0]);
  t.send('xrpl-file-block', { index: 2, firstChunk: 3, lastChunk: 3 });
  assert.deepEqual(t.named('onBlockVerified').map((b) => b.firstChunk), [0, 1]);
});

test('a block spanning several cubes verifies each whole cube it covers', () => {
  const t = setup();
  t.manifest(250);
  t.send('xrpl-file-block', { index: 0, firstChunk: 0, lastChunk: 5 });
  assert.deepEqual(t.named('onBlockVerified').map((b) => b.firstChunk), [0, 1, 2]);
});

test('load ends in done, error in failed with its code', async () => {
  const ok = setup();
  ok.manifest(10);
  ok.send('load');
  await ok.read.done;
  assert.equal(ok.read.phase, 'done');
  assert.deepEqual(ok.named('onPhase'), ['done']);
  assert.equal(ok.named('onDone').length, 1);

  const bad = setup();
  bad.manifest(10);
  bad.send('error', { code: 'NotFound', message: 'x' });
  await bad.read.done;
  assert.equal(bad.read.phase, 'failed');
  assert.deepEqual(bad.named('onPhase'), ['failed']);
  assert.deepEqual(bad.named('onFailed'), [{ error: { code: 'NotFound' } }]);

  // The element's own error detail names the failure in `name`, not `code`.
  const named = setup();
  named.send('error', { name: 'Tampered', message: 'x' });
  assert.deepEqual(named.named('onFailed'), [{ error: { code: 'Tampered' } }]);

  const bare = setup();
  bare.send('error');
  assert.deepEqual(bare.named('onFailed'), [{ error: { code: 'Unknown' } }]);
});

test('an error before the manifest fails cleanly', async () => {
  const t = setup();
  t.send('error', { code: 'NotFound' });
  await t.read.done;
  assert.equal(t.read.phase, 'failed');
  assert.equal(t.named('onManifest').length, 0);
  assert.equal(t.named('onFailed').length, 1);
});

test('nothing is reported after the read has ended', () => {
  const t = setup();
  t.manifest(10);
  t.send('load');
  t.send('xrpl-file-chunk', { index: 1, ledger: 100 });
  t.send('error', { code: 'X' });
  t.send('load');
  assert.equal(t.named('onChunk').length, 0);
  assert.equal(t.named('onFailed').length, 0);
  assert.equal(t.named('onDone').length, 1);
});

test('destroy removes the listeners and resolves done', async () => {
  const added = [];
  const removed = [];
  const element = new EventTarget();
  const add = element.addEventListener.bind(element);
  const remove = element.removeEventListener.bind(element);
  element.addEventListener = (type, fn, ...rest) => { added.push(type); add(type, fn, ...rest); };
  element.removeEventListener = (type, fn, ...rest) => { removed.push(type); remove(type, fn, ...rest); };
  const calls = [];
  const read = startElementRead({ link: LINK, listener: { onManifest: (a) => calls.push(a) } }, { element });
  read.destroy();
  await read.done;
  assert.deepEqual([...removed].sort(), [...added].sort());
  assert.equal(added.length, 5);
  element.dispatchEvent(new CustomEvent('xrpl-file-manifest', { detail: { size: 1, chunk: 1, chunks: 1, blocks: 1 } }));
  assert.equal(calls.length, 0);
});

test('seedBytes is deterministic and differs by ledger', () => {
  const a = seedBytes(5, 900);
  assert.equal(a.length, 32);
  assert.ok(a instanceof Uint8Array);
  assert.deepEqual(a, seedBytes(5, 900));
  assert.notDeepEqual(a, seedBytes(5, 901));
  assert.notDeepEqual(a, seedBytes(6, 900));
});
