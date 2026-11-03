import test from 'node:test';
import assert from 'node:assert/strict';
import { handleCardRequest } from '../cards/handler.mjs';
import { FakeSource, smallFileLedger } from '../../web/test/vectors.mjs';
import { decodeManifest, encodeManifest } from '../../web/src/manifest.mjs';
import { hexUtf8, utf8Hex } from '../../web/src/hex.mjs';
import { builtSite } from './support/built-site.mjs';

const ORIGIN = 'https://xrpl-file.staticbit.io';
const CHROME = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36';
const TELEGRAM = 'TelegramBot (like TwitterBot)';
const YEAR = 'public, max-age=31536000, immutable';

const ascii = (text) => [...text].map((c) => c.charCodeAt(0));
function image(head, size) {
  const bytes = new Uint8Array(size).fill(0x5a);
  bytes.set(head);
  return bytes;
}
const PNG = image([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], 1500);
const WEBP = image([...ascii('RIFF'), 0x10, 0, 0, 0, ...ascii('WEBP'), ...ascii('VP8 ')], 1500);
const MP4 = image([0, 0, 0, 0x18, ...ascii('ftypmp42')], 5000);
const PDF = image(ascii('%PDF-1.7'), 3000);

// Every test reads its own file: the handler keeps in-flight picture reads per isolate, and a read
// one test left running must not be joined by the next.
async function file({ label, bytes, type, name = null, cover = null, firstLedger = 1000 }) {
  const f = await smallFileLedger({ label, bytes, type, firstLedger, cover });
  const tx = f.transactions.find((t) => t.hash === f.hash);
  tx.memoDataHex = utf8Hex(encodeManifest({ ...decodeManifest(hexUtf8(tx.memoDataHex)), name }));
  return f;
}

/** A source whose answers wait for `open()`: a read the test can hold in flight. */
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

/**
 * Deps over an in-memory cache and the built site. `sources` hands out the source of each
 * openSource call in turn (the last one repeats); a function in it is called and may throw.
 */
async function fakeDeps({ ledger = [], sources = null, ...overrides } = {}) {
  const { filePage, banner } = await builtSite();
  const store = new Map();
  const pending = [];
  const deps = {
    origin: ORIGIN,
    sourceOpened: 0,
    sourceClosed: 0,
    cacheCalls: 0,
    passedThrough: [],
    store,
    filePage,
    banner,
    assets: {
      async fetch(request) {
        const path = new URL(request.url).pathname;
        if (path === '/file') return new Response(filePage, { headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-cache' } });
        if (path === '/og-banner.png') return new Response(banner, { headers: { 'Content-Type': 'image/png', 'Cache-Control': 'public, max-age=604800' } });
        return new Response('not found', { status: 404 });
      },
    },
    async passThrough(request) {
      deps.passedThrough.push(request);
      return new Response('static', { status: 404 });
    },
    cache: {
      async match(key) {
        deps.cacheCalls++;
        const hit = store.get(key);
        return hit && new Response(hit.body, { status: hit.status, headers: hit.headers });
      },
      async put(key, response) {
        deps.cacheCalls++;
        store.set(key, { status: response.status, headers: new Headers(response.headers), body: await response.arrayBuffer() });
      },
    },
    waitUntil(promise) { pending.push(promise); },
    async settled() {
      while (pending.length > 0) await pending.shift();
    },
    openSource() {
      const at = deps.sourceOpened++;
      const list = sources ?? [new FakeSource(ledger)];
      let source = list[Math.min(at, list.length - 1)];
      if (typeof source === 'function') source = source();
      return { source, close() { deps.sourceClosed++; } };
    },
    ...overrides,
  };
  return deps;
}

const get = (path, userAgent, init = {}) => new Request(`${ORIGIN}${path}`, { ...init, headers: { 'user-agent': userAgent } });
const pageKey = (hash) => `${ORIGIN}/__cards/page/mainnet/${hash}`;
const pictureKey = (hash) => `${ORIGIN}/__cards/picture/mainnet/${hash}`;

test('a browser gets the static file page and the ledger is never asked', async () => {
  const deps = await fakeDeps({ sources: [() => { throw new Error('node down'); }] });
  const hash = 'AB'.repeat(32);
  for (const path of [`/mainnet/${hash}`, `/testnet/${hash}/`]) {
    const response = await handleCardRequest(get(path, CHROME), deps);
    assert.equal(response.status, 200);
    assert.equal(await response.text(), deps.filePage);
  }
  const noAgent = await handleCardRequest(new Request(`${ORIGIN}/mainnet/${hash}`), deps);
  assert.equal(await noAgent.text(), deps.filePage);
  assert.equal(deps.sourceOpened, 0);
  assert.equal(deps.cacheCalls, 0);
});

test('a robot gets the file card, cached for a year, and the picture read starts in the background', async () => {
  const png = await file({ label: 'h-robot', bytes: PNG, type: 'image/png', name: 'logo.png' });
  const deps = await fakeDeps({ ledger: png.transactions });
  const response = await handleCardRequest(get(`/mainnet/${png.hash}`, TELEGRAM), deps);
  assert.equal(response.status, 200);
  assert.equal(response.headers.get('content-type'), 'text/html; charset=utf-8');
  assert.equal(response.headers.get('cache-control'), YEAR);
  const html = await response.text();
  assert.ok(html.includes('<meta property="og:title" content="logo.png">'));
  assert.ok(html.includes(`<meta property="og:image" content="${ORIGIN}/og/mainnet/${png.hash}">`));
  await deps.settled();
  assert.equal(deps.store.get(pageKey(png.hash)).headers.get('cache-control'), YEAR);
  const picture = deps.store.get(pictureKey(png.hash));
  assert.equal(picture.headers.get('content-type'), 'image/png');
  assert.deepEqual(new Uint8Array(picture.body), PNG);
  assert.equal(deps.sourceOpened, deps.sourceClosed, 'every source opened is closed');

  const again = await handleCardRequest(get(`/mainnet/${png.hash}`, 'Twitterbot/1.0'), deps);
  assert.equal(await again.text(), html);
  assert.equal(deps.sourceOpened, 2, 'the second robot is answered from the cache');
});

test('a robot on a video with a cover: the cover is cached under the picture at once', async () => {
  const coverFile = await smallFileLedger({ label: 'h-cover', bytes: WEBP, type: 'image/webp', firstLedger: 990 });
  const video = await file({ label: 'h-video', bytes: MP4, type: 'video/mp4', name: 'clip.mp4', cover: coverFile.hash });
  const deps = await fakeDeps({ ledger: [...coverFile.transactions, ...video.transactions] });
  const html = await (await handleCardRequest(get(`/mainnet/${video.hash}`, TELEGRAM), deps)).text();
  assert.ok(html.includes('<meta name="twitter:card" content="summary_large_image">'));
  await deps.settled();
  assert.equal(deps.sourceOpened, 1, 'the page step read the cover itself');

  const picture = await handleCardRequest(get(`/og/mainnet/${video.hash}`, TELEGRAM), deps);
  assert.equal(picture.headers.get('content-type'), 'image/webp');
  assert.equal(picture.headers.get('x-content-type-options'), 'nosniff');
  assert.deepEqual(new Uint8Array(await picture.arrayBuffer()), WEBP);
  assert.equal(deps.sourceOpened, 1);
});

test('a robot while the node is unreachable gets the generic card, cached for 300 s; the next miss asks again', async () => {
  const deps = await fakeDeps({ sources: [() => { throw new Error('node down'); }] });
  const hash = 'CD'.repeat(32);
  const response = await handleCardRequest(get(`/mainnet/${hash}`, TELEGRAM), deps);
  assert.equal(response.status, 200);
  assert.equal(await response.text(), deps.filePage);
  assert.equal(response.headers.get('cache-control'), 'public, max-age=300');
  await deps.settled();
  assert.equal(deps.store.get(pageKey(hash)).headers.get('cache-control'), 'public, max-age=300');

  deps.store.clear();
  await handleCardRequest(get(`/mainnet/${hash}`, TELEGRAM), deps);
  assert.equal(deps.sourceOpened, 2);
});

test('a node that fails mid-read is unavailable too', async () => {
  const deps = await fakeDeps({ sources: [{ async transaction() { throw new Error('socket closed'); }, async earliestLedger() { return 1; } }] });
  const hash = 'CE'.repeat(32);
  const response = await handleCardRequest(get(`/mainnet/${hash}`, TELEGRAM), deps);
  assert.equal(response.headers.get('cache-control'), 'public, max-age=300');
  assert.equal(deps.sourceClosed, 1);
});

test('an unknown hash: generic card cached for 3600 s', async () => {
  const deps = await fakeDeps();
  const hash = 'EF'.repeat(32);
  const response = await handleCardRequest(get(`/mainnet/${hash}`, TELEGRAM), deps);
  assert.equal(await response.text(), deps.filePage);
  assert.equal(response.headers.get('cache-control'), 'public, max-age=3600');
});

test('a robot past the deadline gets the generic card, and the source is closed', async () => {
  const silent = { transaction: () => new Promise(() => {}), earliestLedger: () => new Promise(() => {}) };
  const deps = await fakeDeps({ sources: [silent], htmlDeadlineMs: 20 });
  const response = await handleCardRequest(get(`/mainnet/${'0F'.repeat(32)}`, TELEGRAM), deps);
  assert.equal(await response.text(), deps.filePage);
  assert.equal(response.headers.get('cache-control'), 'public, max-age=300');
  assert.equal(deps.sourceClosed, 1);
});

test('/og/ serves a picture with its type and nosniff, then from the cache', async () => {
  const png = await file({ label: 'h-og', bytes: PNG, type: 'image/png' });
  const deps = await fakeDeps({ ledger: png.transactions });
  const response = await handleCardRequest(get(`/og/mainnet/${png.hash}`, CHROME), deps);
  assert.equal(response.status, 200);
  assert.equal(response.headers.get('content-type'), 'image/png');
  assert.equal(response.headers.get('cache-control'), YEAR);
  assert.equal(response.headers.get('x-content-type-options'), 'nosniff');
  assert.deepEqual(new Uint8Array(await response.arrayBuffer()), PNG);
  await deps.settled();

  const cached = await handleCardRequest(get(`/og/mainnet/${png.hash}`, CHROME), deps);
  assert.deepEqual(new Uint8Array(await cached.arrayBuffer()), PNG);
  assert.equal(cached.headers.get('x-content-type-options'), 'nosniff');
  assert.equal(deps.sourceOpened, 1);
});

test('/og/ joins the read the page started instead of starting a second one', async () => {
  const png = await file({ label: 'h-join', bytes: PNG, type: 'image/png', name: 'logo.png' });
  const held = gated(new FakeSource(png.transactions));
  const deps = await fakeDeps({ sources: [new FakeSource(png.transactions), held.source] });
  await handleCardRequest(get(`/mainnet/${png.hash}`, TELEGRAM), deps);
  assert.equal(deps.sourceOpened, 2, 'the page step and the picture read it started');

  const picture = handleCardRequest(get(`/og/mainnet/${png.hash}`, TELEGRAM), deps);
  await new Promise((resolve) => setTimeout(resolve, 10));
  held.open();
  const response = await picture;
  assert.deepEqual(new Uint8Array(await response.arrayBuffer()), PNG);
  assert.equal(deps.sourceOpened, 2, '/og/ opened no source of its own');
  await deps.settled();
});

test('/og/ past its deadline answers the banner (max-age 300) and the read still lands in the cache', async () => {
  const png = await file({ label: 'h-late', bytes: PNG, type: 'image/png' });
  const held = gated(new FakeSource(png.transactions));
  const deps = await fakeDeps({ sources: [held.source], pictureDeadlineMs: 20 });
  const response = await handleCardRequest(get(`/og/mainnet/${png.hash}`, TELEGRAM), deps);
  assert.equal(response.status, 200);
  assert.equal(response.headers.get('content-type'), 'image/png');
  assert.equal(response.headers.get('cache-control'), 'public, max-age=300');
  assert.equal(response.headers.get('x-content-type-options'), 'nosniff');
  assert.deepEqual(new Uint8Array(await response.arrayBuffer()), deps.banner);

  held.open();
  await deps.settled();
  assert.deepEqual(new Uint8Array(deps.store.get(pictureKey(png.hash)).body), PNG);
});

test('/og/ for a PNG manifest over non-PNG bytes answers the banner, and remembers that briefly', async () => {
  const lying = await file({ label: 'h-lie', bytes: PDF, type: 'image/png' });
  const deps = await fakeDeps({ ledger: lying.transactions });
  const response = await handleCardRequest(get(`/og/mainnet/${lying.hash}`, TELEGRAM), deps);
  assert.deepEqual(new Uint8Array(await response.arrayBuffer()), deps.banner);
  assert.equal(response.headers.get('cache-control'), 'public, max-age=300');
  await deps.settled();
  assert.equal(deps.store.get(pictureKey(lying.hash)).headers.get('cache-control'), 'public, max-age=300');

  const again = await handleCardRequest(get(`/og/mainnet/${lying.hash}`, TELEGRAM), deps);
  assert.deepEqual(new Uint8Array(await again.arrayBuffer()), deps.banner);
  assert.equal(deps.sourceOpened, 1, 'the cached "no picture" answers without a read');
});

test('/og/ while the node is down answers the banner', async () => {
  const deps = await fakeDeps({ sources: [() => { throw new Error('node down'); }] });
  const response = await handleCardRequest(get(`/og/mainnet/${'1D'.repeat(32)}`, TELEGRAM), deps);
  assert.equal(response.status, 200);
  assert.deepEqual(new Uint8Array(await response.arrayBuffer()), deps.banner);
});

test('a two-segment path that is not a card is the static file page, as the redirect rule made it', async () => {
  const deps = await fakeDeps({ sources: [() => { throw new Error('node down'); }] });
  for (const path of ['/mainnet/xyz', '/testnet/xyz/', '/og/mainnet']) {
    for (const agent of [CHROME, TELEGRAM]) {
      const response = await handleCardRequest(get(path, agent), deps);
      assert.equal(await response.text(), deps.filePage, `${path} ${agent}`);
    }
  }
  assert.equal(deps.sourceOpened, 0);
  assert.equal(deps.passedThrough.length, 0);
});

test('other paths and other methods go to passThrough untouched', async () => {
  const deps = await fakeDeps({ sources: [() => { throw new Error('node down'); }] });
  const hash = 'AB'.repeat(32);
  const requests = [
    get(`/mainnet/${hash}/extra`, TELEGRAM),
    get(`/og/mainnet/${hash}/`, TELEGRAM),
    get('/og/', TELEGRAM),
    get('/mainnet/', TELEGRAM),
    new Request(`${ORIGIN}/mainnet/${hash}`, { method: 'POST', body: 'x', headers: { 'user-agent': TELEGRAM } }),
  ];
  for (const request of requests) {
    const response = await handleCardRequest(request, deps);
    assert.equal(response.status, 404);
  }
  assert.deepEqual(deps.passedThrough, requests);
  assert.equal(deps.sourceOpened, 0);
  assert.equal(deps.cacheCalls, 0);
});

test('HEAD to a card returns headers and no body', async () => {
  const png = await file({ label: 'h-head', bytes: PNG, type: 'image/png', name: 'logo.png' });
  const deps = await fakeDeps({ ledger: png.transactions });
  const page = await handleCardRequest(get(`/mainnet/${png.hash}`, TELEGRAM, { method: 'HEAD' }), deps);
  assert.equal(page.status, 200);
  assert.equal(page.headers.get('content-type'), 'text/html; charset=utf-8');
  assert.equal(await page.text(), '');
  await deps.settled();

  const picture = await handleCardRequest(get(`/og/mainnet/${png.hash}`, TELEGRAM, { method: 'HEAD' }), deps);
  assert.equal(picture.headers.get('content-type'), 'image/png');
  assert.equal(await picture.text(), '');

  const banner = await handleCardRequest(get(`/og/mainnet/${'2E'.repeat(32)}`, TELEGRAM, { method: 'HEAD' }), deps);
  assert.equal(banner.headers.get('cache-control'), 'public, max-age=300');
  assert.equal(await banner.text(), '');
  await deps.settled();
});

test('a picture read whose source fails to open does not stay joined: the next request reads again', async () => {
  const png = await file({ label: 'h-reopen', bytes: PNG, type: 'image/png' });
  const deps = await fakeDeps({ sources: [() => { throw new Error('node down'); }, new FakeSource(png.transactions)] });
  const first = await handleCardRequest(get(`/og/mainnet/${png.hash}`, TELEGRAM), deps);
  assert.deepEqual(new Uint8Array(await first.arrayBuffer()), deps.banner);
  await deps.settled();
  deps.store.clear();
  const second = await handleCardRequest(get(`/og/mainnet/${png.hash}`, TELEGRAM), deps);
  assert.deepEqual(new Uint8Array(await second.arrayBuffer()), PNG);
  assert.equal(deps.sourceOpened, 2);
});
