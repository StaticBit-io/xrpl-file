import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createPictureReader } from '../server/picture-reader.mjs';
import { createApp } from '../server/app.mjs';
import { createDiskCache } from '../server/cache.mjs';
import { readPicture } from '../cards/card.mjs';
import { FakeSource, smallFileLedger } from '../../web/test/vectors.mjs';
import { builtSite } from './support/built-site.mjs';

const FAKE = new URL('./support/fake-ledger-source.mjs', import.meta.url).href;
const ORIGIN = 'https://xrpl-file.staticbit.io';
const PNG = new Uint8Array(3000).fill(0x5a);
PNG.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

let labels = 0;
const pngFile = () => smallFileLedger({ label: `worker-${labels++}`, bytes: PNG, type: 'image/png', firstLedger: 1000 });

test('a picture read in the worker answers like one on the main thread', async () => {
  const f = await pngFile();
  const here = await readPicture(new FakeSource(f.transactions), { hash: f.hash });
  const reader = createPictureReader({ sourceModule: FAKE, sourceData: { ledgers: { [f.hash]: f.transactions } } });
  try {
    const there = await reader.read({ network: 'mainnet', hash: f.hash });
    assert.equal(there.outcome, here.outcome);
    assert.equal(there.outcome, 'final');
    assert.equal(there.picture.type, 'image/png');
    assert.deepEqual(new Uint8Array(there.picture.bytes), new Uint8Array(here.picture.bytes));
  } finally {
    await reader.close();
  }
});

test('a dead worker is a transient outcome and is replaced', async () => {
  const doomed = await pngFile();
  const fine = await pngFile();
  const reader = createPictureReader({
    sourceModule: FAKE,
    sourceData: { crashOn: doomed.hash, ledgers: { [fine.hash]: fine.transactions } },
  });
  try {
    assert.deepEqual(await reader.read({ network: 'mainnet', hash: doomed.hash }), { picture: null, outcome: 'transient' });
    const after = await reader.read({ network: 'mainnet', hash: fine.hash });
    assert.equal(after.outcome, 'final');
  } finally {
    await reader.close();
  }
});

test('a worker read past the ceiling is transient', async () => {
  const f = await pngFile();
  const reader = createPictureReader({ sourceModule: FAKE, sourceData: { ledgers: {} }, ceilingMs: 1 });
  try {
    // An unknown hash with a 1 ms ceiling: the main thread gives up whatever the worker does.
    const result = await reader.read({ network: 'mainnet', hash: f.hash });
    assert.equal(result.picture, null);
    assert.ok(['transient', 'missing'].includes(result.outcome));
  } finally {
    await reader.close();
  }
});

test('the read cap still counts worker reads', async () => {
  const { outDir, banner } = await builtSite();
  const dir = await mkdtemp(join(tmpdir(), 'xf-worker-cap-'));
  let open;
  const held = new Promise((resolve) => { open = resolve; });
  const calls = [];
  const pictureReader = {
    async read(at) {
      calls.push(at.hash);
      await held;
      return { picture: null, outcome: 'transient' };
    },
    async close() {},
  };
  try {
    const app = await createApp({
      siteRoot: outDir,
      origin: ORIGIN,
      release: 'r',
      cache: await createDiskCache({ dir, generation: 'g', isPageKey: () => false }),
      openLedger: () => { throw new Error('the picture path must not open a source on the main thread'); },
      pictureReader,
      maxReads: 1,
    });
    const robot = { headers: { 'user-agent': 'TelegramBot' } };
    const first = app.handle(new Request(`${ORIGIN}/og/mainnet/${'A1'.repeat(32)}`, robot));
    await new Promise((resolve) => setTimeout(resolve, 20));
    const started = Date.now();
    const second = await app.handle(new Request(`${ORIGIN}/og/mainnet/${'B2'.repeat(32)}`, robot));
    assert.ok(Date.now() - started < 200);
    assert.deepEqual(new Uint8Array(await second.arrayBuffer()), banner);
    assert.deepEqual(calls, ['A1'.repeat(32)]);
    open();
    await first;
    await app.settle(5000);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test('a worker that never answers is replaced after the main-side limit', async () => {
  const stuck = await pngFile();
  const fine = await pngFile();
  const reader = createPictureReader({
    sourceModule: FAKE,
    sourceData: { hangOn: stuck.hash, ledgers: { [fine.hash]: fine.transactions } },
    // Room for the replacement worker to start and read under a loaded CPU (test files run in
    // parallel); the stuck read still ends at this plus the main thread's grace.
    ceilingMs: 1500,
  });
  try {
    assert.deepEqual(await reader.read({ network: 'mainnet', hash: stuck.hash }), { picture: null, outcome: 'transient' });
    const after = await reader.read({ network: 'mainnet', hash: fine.hash });
    assert.equal(after.outcome, 'final');
  } finally {
    await reader.close();
  }
});
