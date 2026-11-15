import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createApp, createSiteServer, shutdown } from '../server/app.mjs';
import { parseRoutes, routedToFunction } from '../server/routes.mjs';
import { toRequest, writeResponse } from '../server/node-http.mjs';
import { createDiskCache } from '../server/cache.mjs';
import { FakeSource, smallFileLedger } from '../../web/test/vectors.mjs';
import { decodeManifest, encodeManifest } from '../../web/src/manifest.mjs';
import { hexUtf8, utf8Hex } from '../../web/src/hex.mjs';
import { builtSite } from './support/built-site.mjs';

const ORIGIN = 'https://xrpl-file.staticbit.io';
const RELEASE = 'd'.repeat(40);
const CHROME = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36';
const TELEGRAM = 'TelegramBot (like TwitterBot)';
const PNG = new Uint8Array(1500).fill(0x5a);
PNG.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

let labels = 0;
async function pngFile(name) {
  const f = await smallFileLedger({ label: `app-${labels++}`, bytes: PNG, type: 'image/png', firstLedger: 1000 });
  const tx = f.transactions.find((t) => t.hash === f.hash);
  tx.memoDataHex = utf8Hex(encodeManifest({ ...decodeManifest(hexUtf8(tx.memoDataHex)), name }));
  return f;
}

/** A source whose answers wait for `open()`. */
function gated(source) {
  let open;
  const gate = new Promise((resolve) => { open = resolve; });
  return {
    open: () => open(),
    source: {
      async transaction(hash) { await gate; return source.transaction(hash); },
      async *accountTransactions(...args) { await gate; yield* source.accountTransactions(...args); },
      async earliestLedger() { await gate; return source.earliestLedger(); },
    },
  };
}

async function tempDir() {
  return mkdtemp(join(tmpdir(), 'xf-app-'));
}

/** An app over the built site; `sources` are handed out in turn by openLedger (the last repeats). */
async function app({ sources, cache, ...options }) {
  const { outDir } = await builtSite();
  const counts = { opened: 0, closed: 0 };
  const dir = await tempDir();
  const instance = await createApp({
    siteRoot: outDir,
    origin: ORIGIN,
    release: RELEASE,
    cache: cache ?? (await createDiskCache({ dir, generation: 'g', isPageKey: (key) => key.includes('/page/') })),
    openLedger() {
      const source = sources[Math.min(counts.opened, sources.length - 1)];
      counts.opened++;
      // A FakeSource keeps its methods on the prototype, so the wrapper inherits instead of copying.
      return Object.assign(Object.create(source), { close() { counts.closed++; } });
    },
    ...options,
  });
  return { app: instance, counts, dir };
}

const get = (path, userAgent) => new Request(`${ORIGIN}${path}`, { headers: { 'user-agent': userAgent } });

test('a robot gets the card, a person the static page', async () => {
  const f = await pngFile('logo.png');
  const { app: a } = await app({ sources: [new FakeSource(f.transactions)] });
  const card = await (await a.handle(get(`/mainnet/${f.hash}`, TELEGRAM))).text();
  assert.match(card, /<meta property="og:title" content="logo\.png">/);
  const person = await a.handle(get(`/mainnet/${f.hash}`, CHROME));
  assert.equal(await person.text(), (await builtSite()).filePage);
  assert.equal(person.headers.get('x-xrpl-file-release'), RELEASE);
  await a.settle(5000);
});

test('routes follow _routes.json, exclude first', async () => {
  const routes = parseRoutes(JSON.stringify({ version: 1, include: ['/mainnet/*', '/og/*'], exclude: [] }));
  assert.equal(routedToFunction('/og/mainnet/x', routes), true);
  assert.equal(routedToFunction('/js/route.mjs', routes), false);
  const excluding = parseRoutes(JSON.stringify({ version: 1, include: ['/mainnet/*', '/og/*'], exclude: ['/og/*'] }));
  assert.equal(routedToFunction('/og/mainnet/x', excluding), false);
  assert.throws(() => parseRoutes(JSON.stringify({ version: 1, include: [] })));
  // Pages answers a two-segment path that is not a card with the file page; so does the server.
  const { app: a } = await app({ sources: [new FakeSource([])] });
  const nothing = await a.handle(get('/og/nothing', TELEGRAM));
  assert.equal(nothing.status, 200);
  assert.equal(await nothing.text(), (await builtSite()).filePage);
});

test('a forged Host never reaches a card', async () => {
  for (const url of ['/mainnet/AB', 'http://evil.example/mainnet/AB']) {
    const request = toRequest({ url, method: 'GET', headers: { host: 'evil.example', 'user-agent': TELEGRAM } }, ORIGIN);
    assert.equal(request.url, `${ORIGIN}/mainnet/AB`, url);
    assert.equal(request.headers.get('user-agent'), TELEGRAM);
  }
  const f = await pngFile('forged.png');
  const { app: a } = await app({ sources: [new FakeSource(f.transactions)] });
  const forged = toRequest({ url: `/mainnet/${f.hash}`, method: 'GET', headers: { host: 'evil.example', 'user-agent': TELEGRAM } }, ORIGIN);
  const html = await (await a.handle(forged)).text();
  assert.ok(html.includes(`content="${ORIGIN}/og/mainnet/${f.hash}"`));
  assert.ok(!html.includes('evil.example'));
  await a.settle(5000);
});

test('past four reads a robot gets the generic card at once', async () => {
  const files = await Promise.all([0, 1, 2, 3, 4].map((i) => pngFile(`cap${i}.png`)));
  const gates = files.slice(0, 4).map((f) => gated(new FakeSource(f.transactions)));
  const { app: a, counts } = await app({ sources: [...gates.map((g) => g.source), new FakeSource(files[4].transactions)] });
  const held = files.slice(0, 4).map((f) => a.handle(get(`/mainnet/${f.hash}`, TELEGRAM)));
  await new Promise((resolve) => setTimeout(resolve, 20));
  const started = Date.now();
  const fifth = await a.handle(get(`/mainnet/${files[4].hash}`, TELEGRAM));
  assert.ok(Date.now() - started < 100, 'answered at once');
  assert.equal(await fifth.text(), (await builtSite()).filePage);
  assert.equal(counts.opened, 4);
  gates.forEach((g) => g.open());
  await Promise.all(held);
  await a.settle(5000);
  assert.equal(counts.closed, counts.opened);
});

test('past the card-request limit the static response answers', async () => {
  const f = await pngFile('limit.png');
  const g = gated(new FakeSource(f.transactions));
  const { app: a, counts } = await app({ sources: [g.source], maxCardRequests: 1 });
  const first = a.handle(get(`/mainnet/${f.hash}`, TELEGRAM));
  await new Promise((resolve) => setTimeout(resolve, 20));
  const second = await a.handle(get(`/mainnet/${f.hash}`, TELEGRAM));
  assert.equal(await second.text(), (await builtSite()).filePage);
  assert.equal(counts.opened, 1);
  g.open();
  await first;
  await a.settle(5000);
});

test('a cache that cannot be written is a miss, not the generic card', async () => {
  const dir = await tempDir();
  const blocker = join(dir, 'file');
  await writeFile(blocker, 'x');
  const cache = await createDiskCache({ dir: blocker, generation: 'g', isPageKey: () => true });
  const f = await pngFile('nocache.png');
  const { app: a } = await app({ sources: [new FakeSource(f.transactions)], cache });
  assert.match(await (await a.handle(get(`/mainnet/${f.hash}`, TELEGRAM))).text(), /content="nocache\.png"/);
  await a.settle(5000);
  await rm(dir, { recursive: true, force: true });
});

test('shutdown finishes started responses and waits for background work, at most the limit', async () => {
  const f = await pngFile('drain.png');
  const card = gated(new FakeSource(f.transactions));
  const picture = gated(new FakeSource(f.transactions));
  const { app: a } = await app({ sources: [card.source, picture.source] });
  const server = createSiteServer(a, { origin: ORIGIN });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address();
  const response = fetch(`http://127.0.0.1:${port}/mainnet/${f.hash}`, { headers: { 'user-agent': TELEGRAM } });
  await new Promise((resolve) => setTimeout(resolve, 50));
  setTimeout(() => card.open(), 100);
  const started = Date.now();
  const stopping = shutdown(server, a, 1000);
  const answered = await response;
  assert.equal(answered.status, 200);
  assert.match(await answered.text(), /content="drain\.png"/);
  await stopping;
  assert.ok(Date.now() - started < 1500, `shutdown took ${Date.now() - started} ms`);
  picture.open();
  await a.settle(5000);
});

test('HEAD writes no body', async () => {
  const written = { chunks: [] };
  const res = {
    writeHead(status, headers) { written.status = status; written.headers = headers; },
    write(chunk) { written.chunks.push(chunk); },
    end(chunk) { if (chunk) written.chunks.push(chunk); written.ended = true; },
  };
  await writeResponse(res, new Response('body', { headers: { 'X-A': '1' } }), 'HEAD');
  assert.equal(written.status, 200);
  assert.equal(written.headers['x-a'], '1');
  assert.equal(written.chunks.length, 0);
  assert.equal(written.ended, true);
});
