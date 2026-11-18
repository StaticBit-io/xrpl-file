// How much four concurrent 1 MB picture reads delay a person's page on this machine (spec, "One
// CPU"). The ledger is an in-memory FakeSource, so the number is CPU, not network. Picture reads run
// in the worker thread, as on the server; --main-thread runs them on the main thread for comparison.
//
//   node site/tools/bench-server.mjs [--main-thread]      (after node web/build.mjs && node site/build.mjs)
import { mkdtemp, rm, realpath } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { monitorEventLoopDelay } from 'node:perf_hooks';
import { createApp } from '../server/app.mjs';
import { createDiskCache } from '../server/cache.mjs';
import { createPictureReader } from '../server/picture-reader.mjs';
import { FakeSource, smallFileLedger } from '../../web/test/vectors.mjs';

const PICTURE_BYTES = 1_000_000;
const READS = 4;
const PROBE_MS = 10;
const ORIGIN = 'https://xrpl-file.staticbit.io';
const MAIN_THREAD = process.argv.includes('--main-thread');
const FAKE = new URL('../test/support/fake-ledger-source.mjs', import.meta.url).href;

const siteRoot = await realpath(fileURLToPath(new URL('../dist', import.meta.url)));
const png = new Uint8Array(PICTURE_BYTES).map((_, i) => (i * 31) & 0xff);
png.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

async function probe(app, ms) {
  const latencies = [];
  const until = performance.now() + ms;
  while (performance.now() < until) {
    const started = performance.now();
    await (await app.handle(new Request(`${ORIGIN}/file`))).arrayBuffer();
    latencies.push(performance.now() - started);
    await new Promise((resolve) => setTimeout(resolve, PROBE_MS));
  }
  latencies.sort((a, b) => a - b);
  return { max: latencies.at(-1), p99: latencies[Math.floor(latencies.length * 0.99)], count: latencies.length };
}

const dir = await mkdtemp(join(tmpdir(), 'xf-bench-'));
try {
  const files = [];
  for (let i = 0; i < READS; i++) files.push(await smallFileLedger({ label: `bench-${i}`, bytes: png, type: 'image/png', firstLedger: 1000 }));
  let opened = 0;
  const pictureReader = MAIN_THREAD
    ? null
    : createPictureReader({ sourceModule: FAKE, sourceData: { ledgers: Object.fromEntries(files.map((f) => [f.hash, f.transactions])) } });
  const app = await createApp({
    siteRoot,
    origin: ORIGIN,
    release: 'bench',
    cache: await createDiskCache({ dir, generation: 'bench', isPageKey: () => false }),
    openLedger: () => new FakeSource(files[opened++ % READS].transactions),
    pictureReader,
  });
  // Start the worker before measuring: its workerData here holds the four files (megabytes of hex),
  // which production never sends, and cloning it is main-thread work.
  await pictureReader?.read({ network: 'mainnet', hash: 'warm-up' });
  const quiet = await probe(app, 1000);
  const loop = monitorEventLoopDelay({ resolution: 1 });
  loop.enable();
  const started = performance.now();
  const reads = Promise.all(files.map((f) => app.handle(new Request(`${ORIGIN}/og/mainnet/${f.hash}`, { headers: { 'user-agent': 'TelegramBot' } }))));
  const loaded = await probe(app, 1500);
  const answers = await reads;
  const readMs = performance.now() - started;
  await app.settle(30_000);
  loop.disable();
  const types = answers.map((r) => r.headers.get('content-type'));
  await pictureReader?.close();
  console.log(JSON.stringify({
    mode: MAIN_THREAD ? 'main thread' : 'worker',
    quietMaxMs: +quiet.max.toFixed(1),
    loadedMaxMs: +loaded.max.toFixed(1),
    loadedP99Ms: +loaded.p99.toFixed(1),
    addedMaxMs: +(loaded.max - quiet.max).toFixed(1),
    eventLoopMaxBlockMs: +(loop.max / 1e6).toFixed(1),
    fourReadsMs: +readMs.toFixed(0),
    pictures: types,
  }));
} finally {
  await rm(dir, { recursive: true, force: true });
}
