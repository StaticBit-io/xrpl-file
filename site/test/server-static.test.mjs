import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createStaticSite } from '../server/static.mjs';
import { parseRedirects } from '../redirects.mjs';
import { parseHeaders } from '../headers.mjs';
import { ELEMENT_VERSION } from '../build.mjs';
import { builtSite } from './support/built-site.mjs';

const RELEASE = 'c'.repeat(40);
const HASH = '9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5';

async function site(options = {}) {
  const { outDir } = await builtSite();
  return createStaticSite({
    root: outDir,
    redirects: parseRedirects(await readFile(join(outDir, '_redirects'), 'utf8')),
    headers: parseHeaders(await readFile(join(outDir, '_headers'), 'utf8')),
    release: RELEASE,
    ...options,
  });
}

const get = (path, init) => new Request(`https://xrpl-file.staticbit.io${path}`, init);

// What the live Pages site answered on 2026-11-12 and 2026-11-15. Every row also carries
// Access-Control-Allow-Origin: *, Referrer-Policy and nosniff, checked below.
const LIVE = [
  ['/', 200, 'no-cache'],
  ['/ru/', 200, 'no-cache'],
  ['/file', 200, 'public, max-age=0, must-revalidate'],
  ['/js/route.mjs', 200, 'public, max-age=0, must-revalidate'],
  [`/element/${ELEMENT_VERSION}/xrpl-file-element.js`, 200, 'public, max-age=31536000, immutable'],
  ['/favicon.svg', 200, 'public, max-age=604800'],
  ['/og-banner.png', 200, 'public, max-age=604800'],
  ['/nope', 404, 'no-store'],
  [`/mainnet/${HASH}`, 200, 'public, max-age=0, must-revalidate'],
  [`/mainnet/${HASH}/`, 200, 'public, max-age=0, must-revalidate'],
  ['/index.html', 308, 'no-cache', '/'],
  ['/ru/index.html', 308, 'no-cache', '/ru/'],
  ['/file.html', 308, 'no-cache', '/file'],
  ['/ru', 308, null, '/ru/'],
  ['/_redirects', 404, 'no-store'],
];

test('responses match the live Pages headers', async () => {
  const s = await site();
  for (const [path, status, cacheControl, location] of LIVE) {
    const response = await s.respond(get(path));
    assert.equal(response.status, status, path);
    assert.equal(response.headers.get('cache-control'), cacheControl, path);
    assert.equal(response.headers.get('location'), location ?? null, path);
    assert.equal(response.headers.get('access-control-allow-origin'), '*', path);
    assert.equal(response.headers.get('referrer-policy'), 'strict-origin-when-cross-origin', path);
    assert.equal(response.headers.get('x-content-type-options'), 'nosniff', path);
  }
});

test('a redirect keeps the query string', async () => {
  const s = await site();
  assert.equal((await s.respond(get('/index.html?x=1'))).headers.get('location'), '/?x=1');
  assert.equal((await s.respond(get('/ru?x=1'))).headers.get('location'), '/ru/?x=1');
});

test('every response names the release', async () => {
  const s = await site();
  for (const path of ['/', '/index.html', '/nope']) {
    assert.equal((await s.respond(get(path))).headers.get('x-xrpl-file-release'), RELEASE, path);
  }
});

test('an ETag revalidates to 304', async () => {
  const s = await site();
  const first = await s.respond(get('/file'));
  const etag = first.headers.get('etag');
  assert.match(etag, /^"[0-9a-f]{32}"$/);
  for (const value of [etag, `W/${etag}`, '*', `"other", ${etag}`]) {
    const again = await s.respond(get('/file', { headers: { 'If-None-Match': value } }));
    assert.equal(again.status, 304, value);
    assert.equal(again.headers.get('etag'), etag, value);
    assert.equal((await again.arrayBuffer()).byteLength, 0, value);
  }
  assert.equal((await s.respond(get('/file', { headers: { 'If-None-Match': '"other"' } }))).status, 200);
  const post = await s.respond(get('/file', { method: 'POST' }));
  assert.equal(post.status, 405);
  assert.equal(post.headers.get('allow'), 'GET, HEAD');
});

test('HEAD has the headers and no body', async () => {
  const s = await site();
  const response = await s.respond(get('/file', { method: 'HEAD' }));
  assert.equal(response.status, 200);
  assert.equal(response.headers.get('content-type'), 'text/html; charset=utf-8');
  assert.ok(Number(response.headers.get('content-length')) > 0);
  assert.equal((await response.arrayBuffer()).byteLength, 0);
});

test('a read error is a 500 and a log line, not a 404', async () => {
  const lines = [];
  const eio = async () => {
    throw Object.assign(new Error('disk'), { code: 'EIO' });
  };
  const s = await site({ fs: { stat: eio }, log: (line) => lines.push(line) });
  const response = await s.respond(get('/file'));
  assert.equal(response.status, 500);
  assert.equal(lines.length, 1);
  assert.match(lines[0], /EIO|disk/);
});
