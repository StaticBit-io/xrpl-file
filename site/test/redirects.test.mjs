import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { parseRedirects, applyRedirects, findShadowed, findHtmlRewriteTargets } from '../redirects.mjs';
import { LANGS } from '../src/js/languages.mjs';

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
  assert.deepEqual(applyRedirects(`/testnet/${HASH}`, rules), { status: 200, path: '/file' });
  assert.deepEqual(applyRedirects(`/21338/${HASH.toLowerCase()}/`, rules), { status: 200, path: '/file' });
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
  const bad = parseRedirects('/:network/:hash /file 200\n');
  assert.deepEqual(findShadowed(['index.html', 'js/a.mjs', 'ru/index.html'], bad), ['/js/a.mjs', '/ru/index.html']);
  assert.deepEqual(findShadowed(['index.html', 'js/a.mjs', 'ru/index.html', 'element/1.1.0/x.js'], rules), []);
});

test('findHtmlRewriteTargets reports 200 rules that point at .html, /index or /index.html', () => {
  const found = findHtmlRewriteTargets(parseRedirects(
    '/a /file.html 200\n/b /x/index.html 200\n/c /x/index 200\n/d /file 200\n/e /page.html 301\n/ru/:f /ru/:f 200\n',
  ));
  assert.deepEqual(found.map((rule) => rule.source), ['/a', '/b', '/c']);
  assert.deepEqual(findHtmlRewriteTargets(rules), []);
});

test('every language folder, active or not yet, is passed through to its own files', () => {
  for (const lang of LANGS.filter((code) => code !== 'en')) {
    for (const path of [`/${lang}/index.html`, `/${lang}/page.html`]) {
      assert.deepEqual(applyRedirects(path, rules), { status: 200, path }, path);
    }
    assert.equal(applyRedirects(`/${lang}/`, rules), null, `the ${lang} home is a directory URL`);
  }
});

test('the icons are cached for a week, and no other cache block matches them', async () => {
  const headers = await readFile(new URL('../src/_headers', import.meta.url), 'utf8');
  for (const icon of ['/favicon.svg', '/favicon.ico', '/apple-touch-icon.png']) {
    const block = new RegExp(`^${icon.replaceAll('.', '\\.')}\\r?\\n  Cache-Control: public, max-age=604800$`, 'm');
    assert.match(headers, block, icon);
  }
  assert.ok(!/^\/\*\.(svg|ico|png)$/m.test(headers), 'a wildcard rule would add a second Cache-Control');
});

test('every language home is revalidated on every load, like the English one', async () => {
  const headers = await readFile(new URL('../src/_headers', import.meta.url), 'utf8');
  const blocks = headers.split(/\r?\n/).filter((line) => /^\//.test(line));
  for (const lang of LANGS.filter((code) => code !== 'en')) {
    assert.ok(blocks.includes(`/${lang}/`), `_headers has no block for /${lang}/`);
  }
});
