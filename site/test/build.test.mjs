import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { build, MAX_FILE_BYTES } from '../build.mjs';
import { ELEMENT_PACKAGE, ELEMENT_PACKAGE_PUBLISHED } from '../src/js/config.mjs';

const GUARDED_REDIRECTS = '/ru/:file /ru/:file 200\n/js/:file /js/:file 200\n/data/:file /data/:file 200\n/:network/:hash /file 200\n';

async function fixture(redirects = GUARDED_REDIRECTS) {
  const root = await mkdtemp(join(tmpdir(), 'xf-site-build-'));
  const siteDir = join(root, 'site');
  const webDistDir = join(root, 'web-dist');
  await mkdir(join(siteDir, 'src', 'js'), { recursive: true });
  await mkdir(join(siteDir, 'data'), { recursive: true });
  await mkdir(join(webDistDir, '1.1.0'), { recursive: true });
  await writeFile(join(siteDir, 'src', 'index.html'), '<!doctype html>');
  await writeFile(join(siteDir, 'src', 'js', 'a.mjs'), 'export {};');
  await writeFile(join(siteDir, 'src', '_redirects'), redirects);
  await writeFile(join(siteDir, 'data', 'price.json'), '{"rows":[]}');
  await writeFile(join(webDistDir, '1.1.0', 'xrpl-file-element.js'), '// element');
  await writeFile(join(webDistDir, '1.1.0', 'xrpl-file-reader.mjs'), '// reader');
  await writeFile(
    join(webDistDir, '1.1.0', 'integrity.json'),
    JSON.stringify({ 'xrpl-file-element.js': 'sha384-TESTVALUE+/=', 'mp4box.min.js': 'sha384-OTHER' }),
  );
  return { root, siteDir, webDistDir, outDir: join(root, 'dist') };
}

const exists = async (path) => stat(path).then(() => true, () => false);

async function withFixture(redirects, body) {
  const f = await fixture(redirects);
  try {
    await body(f);
  } finally {
    await rm(f.root, { recursive: true, force: true });
  }
}

test('copies the element under /element/1.1.0, the site sources and the price table', () =>
  withFixture(undefined, async (f) => {
    await build({ siteDir: f.siteDir, webDistDir: f.webDistDir, outDir: f.outDir, elementVersion: '1.1.0' });
    assert.equal(await readFile(join(f.outDir, 'element', '1.1.0', 'xrpl-file-reader.mjs'), 'utf8'), '// reader');
    assert.equal(await readFile(join(f.outDir, 'element', '1.1.0', 'xrpl-file-element.js'), 'utf8'), '// element');
    assert.equal(await readFile(join(f.outDir, 'data', 'price.json'), 'utf8'), '{"rows":[]}');
    assert.ok(await exists(join(f.outDir, 'index.html')));
    assert.ok(await exists(join(f.outDir, 'js', 'a.mjs')));
    assert.ok(await exists(join(f.outDir, '_redirects')));
  }));

test('refuses a missing element version and leaves no output behind', () =>
  withFixture(undefined, async (f) => {
    await assert.rejects(
      build({ siteDir: f.siteDir, webDistDir: f.webDistDir, outDir: f.outDir, elementVersion: '9.9.9' }),
      /9\.9\.9/,
    );
    assert.equal(await exists(f.outDir), false);
  }));

test('refuses a file over the size limit and names it', () =>
  withFixture(undefined, async (f) => {
    await writeFile(join(f.siteDir, 'src', 'big.bin'), Buffer.alloc(2048));
    await assert.rejects(
      build({
        siteDir: f.siteDir, webDistDir: f.webDistDir, outDir: f.outDir, elementVersion: '1.1.0', maxFileBytes: 1024,
      }),
      /big\.bin/,
    );
    assert.equal(await exists(f.outDir), false);
  }));

test('the default limit is the Cloudflare Pages 25 MiB per file', () => {
  assert.equal(MAX_FILE_BYTES, 25 * 1024 * 1024);
});

test('refuses a static file that the file-page rewrite would shadow', () =>
  withFixture('/:network/:hash /file 200\n', async (f) => {
    await assert.rejects(
      build({ siteDir: f.siteDir, webDistDir: f.webDistDir, outDir: f.outDir, elementVersion: '1.1.0' }),
      /\/js\/a\.mjs/,
    );
    assert.equal(await exists(f.outDir), false);
  }));

test('refuses a 200 rule whose destination ends in .html, /index or /index.html', async () => {
  for (const destination of ['/file.html', '/x/index.html', '/x/index']) {
    const redirects = `/js/:file /js/:file 200\n/data/:file /data/:file 200\n/:network/:hash ${destination} 200\n`;
    await withFixture(redirects, async (f) => {
      await assert.rejects(
        build({ siteDir: f.siteDir, webDistDir: f.webDistDir, outDir: f.outDir, elementVersion: '1.1.0' }),
        /\.html|\/index/,
      );
      assert.equal(await exists(f.outDir), false);
    });
  }
});

const PAGE = (lang, body, htmlAttributes = '') =>
  `<!doctype html>\n<html lang="${lang}"${htmlAttributes}>\n<head>\n<!--@chrome:head-->\n</head>\n<body>\n<!--@chrome:header-->\n<main id="main">${body}</main>\n<!--@chrome:footer-->\n</body>\n</html>\n`;

async function buildWithPages(pages, check) {
  await withFixture(undefined, async (f) => {
    for (const [name, html] of Object.entries(pages)) {
      await mkdir(join(f.siteDir, 'src', name.includes('/') ? name.slice(0, name.lastIndexOf('/')) : '.'), { recursive: true });
      await writeFile(join(f.siteDir, 'src', name), html);
    }
    await build({ siteDir: f.siteDir, webDistDir: f.webDistDir, outDir: f.outDir, elementVersion: '1.1.0' });
    await check(f, (name) => readFile(join(f.outDir, name), 'utf8'));
  });
}

test('inlines the shared head, header and footer into a page and takes the language from <html lang>', () =>
  buildWithPages({ 'index.html': PAGE('en', 'Hello'), 'ru/index.html': PAGE('ru', 'Привет') }, async (f, read) => {
    const en = await read('index.html');
    assert.ok(!en.includes('<!--@chrome:'), 'no marker is left behind');
    assert.match(en, /<link rel="stylesheet" href="\/css\/site\.css">/);
    assert.match(en, /<a class="wordmark" href="\/"/);
    assert.match(en, /data-lang-link="en" href="\/" lang="en" hreflang="en" aria-current="true"/);
    assert.match(en, /data-lang-link="ru" href="\/ru\/"/);
    assert.match(en, /<main id="main">Hello<\/main>/);
    assert.ok(en.includes('Repository'));

    const ru = await read('ru/index.html');
    assert.match(ru, /<a class="wordmark" href="\/ru\/"/);
    assert.match(ru, /data-lang-link="ru" href="\/ru\/" lang="ru" hreflang="ru" aria-current="true"/);
    assert.ok(ru.includes('Репозиторий'));
    assert.ok(ru.includes('/fonts/onest-cyrillic.woff2'));
  }));

test('a page marked data-lang-source="storage" gets one URL for both languages and in-place chrome text', () =>
  buildWithPages({ 'file.html': PAGE('en', 'file', ' data-lang-source="storage"') }, async (f, read) => {
    const html = await read('file.html');
    assert.match(html, /data-i18n="repository"/);
    assert.match(html, /data-lang-link="en" href="\/"/);
    assert.match(html, /data-lang-link="ru" href="\/ru\/"/);
    assert.ok(!/aria-current/.test(html), 'chrome.mjs marks the current language once it knows the stored one');
  }));

test('derives the equivalent-language links from the page URL, with pretty URLs for .html pages', () =>
  buildWithPages({ 'about.html': PAGE('en', 'x'), 'ru/about.html': PAGE('ru', 'x') }, async (f, read) => {
    const en = await read('about.html');
    assert.match(en, /data-lang-link="en" href="\/about"/);
    assert.match(en, /data-lang-link="ru" href="\/ru\/about"/);
    const ru = await read('ru/about.html');
    assert.match(ru, /data-lang-link="en" href="\/about"/);
    assert.match(ru, /data-lang-link="ru" href="\/ru\/about"/);
  }));

test('leaves a page without markers untouched', () =>
  buildWithPages({ 'plain.html': '<!doctype html><title>x</title>' }, async (f, read) => {
    assert.equal(await read('plain.html'), '<!doctype html><title>x</title>');
  }));

test('ships no partials and does not rewrite the element build', () =>
  buildWithPages({ 'index.html': PAGE('en', 'x') }, async (f) => {
    assert.equal(await exists(join(f.outDir, 'partials')), false);
    assert.equal(await readFile(join(f.outDir, 'element', '1.1.0', 'xrpl-file-reader.mjs'), 'utf8'), '// reader');
  }));

test('refuses a marker on a page without a usable <html lang>, or an unknown marker, and leaves no output', async () => {
  for (const html of [
    '<!doctype html><html><head><!--@chrome:head--></head></html>',
    '<!doctype html><html lang="de"><head><!--@chrome:head--></head></html>',
    '<!doctype html><html lang="en"><head><!--@chrome:sidebar--></head></html>',
  ]) {
    await withFixture(undefined, async (f) => {
      await writeFile(join(f.siteDir, 'src', 'bad.html'), html);
      await assert.rejects(
        build({ siteDir: f.siteDir, webDistDir: f.webDistDir, outDir: f.outDir, elementVersion: '1.1.0' }),
        /bad\.html/,
      );
      assert.equal(await exists(f.outDir), false);
    });
  }
});

const SNIPPET_BODY =
  '<pre><code><!--@element-snippet--></code></pre><pre><code><!--@element-snippet-alt--></code></pre>' +
  '<!--@npm-unpublished--><p>NOT-PUBLISHED-NOTE</p><!--@/npm-unpublished-->';

test('built index.html contains the 1.1.0 snippet with the integrity from integrity.json', () =>
  buildWithPages({ 'index.html': PAGE('en', SNIPPET_BODY) }, async (f, read) => {
    const html = await read('index.html');
    for (const marker of ['<!--@element-snippet-->', '<!--@element-snippet-alt-->', '<!--@npm-unpublished-->']) {
      assert.ok(!html.includes(marker), `no ${marker} is left behind`);
    }
    const [primary, alternative] = html.match(/<pre><code>[\s\S]*?<\/code><\/pre>/g);

    assert.ok(primary.includes(`src="https://cdn.jsdelivr.net/npm/${ELEMENT_PACKAGE}@1.1.0/xrpl-file-element.js"`));
    // The snippet is pasted into other sites, so the alternative names the site's own origin.
    assert.ok(alternative.includes('src="https://xrpl-file.staticbit.io/element/1.1.0/xrpl-file-element.js"'));
    for (const snippet of [primary, alternative]) {
      assert.ok(snippet.includes('integrity="sha384-TESTVALUE+/="'));
      assert.ok(snippet.includes('crossorigin="anonymous"'));
      assert.ok(!snippet.includes('type="module"'), 'the element is a classic script');
      assert.match(snippet, /&lt;xrpl-file src="xrpl-file:\/\/testnet\/[0-9A-F]{64}"/);
    }
    assert.ok(!html.includes('sha384-OTHER'), 'only the value for the element script is used');
  }));

test('the "package not published" note is kept exactly while ELEMENT_PACKAGE_PUBLISHED is false', () =>
  buildWithPages({ 'index.html': PAGE('en', SNIPPET_BODY) }, async (f, read) => {
    assert.equal(ELEMENT_PACKAGE_PUBLISHED, false);
    assert.ok((await read('index.html')).includes('<p>NOT-PUBLISHED-NOTE</p>'));
  }));

test('refuses a snippet marker when integrity.json has no value for the element script', () =>
  withFixture(undefined, async (f) => {
    await writeFile(join(f.webDistDir, '1.1.0', 'integrity.json'), '{}');
    await writeFile(join(f.siteDir, 'src', 'index.html'), PAGE('en', '<code><!--@element-snippet--></code>'));
    await assert.rejects(
      build({ siteDir: f.siteDir, webDistDir: f.webDistDir, outDir: f.outDir, elementVersion: '1.1.0' }),
      /integrity/,
    );
    assert.equal(await exists(f.outDir), false);
  }));
