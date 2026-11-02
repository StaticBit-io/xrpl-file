import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { parseRedirects, applyRedirects, findShadowed } from '../redirects.mjs';

const HASH = '32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59';
const rules = parseRedirects(await readFile(new URL('../src/_redirects', import.meta.url), 'utf8'));

test('parseRedirects skips comments and blank lines and defaults the status to 302', () => {
  assert.deepEqual(
    parseRedirects('# note\n\n/a /b\n/c/:d /e/:d 200\n').map((r) => [r.source, r.destination, r.status]),
    [['/a', '/b', 302], ['/c/:d', '/e/:d', 200]],
  );
});

test('the first matching rule wins and a proxied target is not processed again', () => {
  const r = parseRedirects('/a /b 200\n/b /c 200\n');
  assert.deepEqual(applyRedirects('/a', r), { status: 200, path: '/b' });
  assert.deepEqual(applyRedirects('/b', r), { status: 200, path: '/c' });
});

test('a placeholder matches one segment, a splat matches the rest', () => {
  const r = parseRedirects('/p/:x /q/:x 301\n/s/* /t/:splat 301\n');
  assert.deepEqual(applyRedirects('/p/one', r), { status: 301, location: '/q/one' });
  assert.equal(applyRedirects('/p/one/two', r), null);
  assert.equal(applyRedirects('/p/', r), null);
  assert.deepEqual(applyRedirects('/s/a/b', r), { status: 301, location: '/t/a/b' });
});

test('the site file-page rule rewrites a link path, with or without a trailing slash', () => {
  assert.deepEqual(applyRedirects(`/testnet/${HASH}`, rules), { status: 200, path: '/file.html' });
  assert.deepEqual(applyRedirects(`/21338/${HASH.toLowerCase()}/`, rules), { status: 200, path: '/file.html' });
});

test('static two-segment paths are served as themselves, never rewritten to the file page', () => {
  for (const path of [
    '/ru/index.html', '/ru/page.html', '/js/route.mjs', '/js/xrpl.mjs', '/css/site.css',
    '/data/price.json', '/fonts/x.woff2', '/img/hero.svg',
  ]) {
    assert.deepEqual(applyRedirects(path, rules), { status: 200, path }, path);
  }
});

test('single-segment and deeper paths are untouched by the file-page rule', () => {
  for (const path of ['/', '/index.html', '/file.html', '/ru/', '/element/1.1.0/xrpl-file-element.js']) {
    assert.equal(applyRedirects(path, rules), null, path);
  }
});

test('findShadowed reports a file whose URL a rule sends elsewhere', () => {
  const bad = parseRedirects('/:network/:hash /file.html 200\n');
  assert.deepEqual(findShadowed(['index.html', 'js/a.mjs', 'ru/index.html'], bad), ['/js/a.mjs', '/ru/index.html']);
  assert.deepEqual(findShadowed(['index.html', 'js/a.mjs', 'ru/index.html', 'element/1.1.0/x.js'], rules), []);
});
