import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { build, MAX_FILE_BYTES } from '../build.mjs';

const GUARDED_REDIRECTS = '/js/:file /js/:file 200\n/data/:file /data/:file 200\n/:network/:hash /file.html 200\n';

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
  withFixture('/:network/:hash /file.html 200\n', async (f) => {
    await assert.rejects(
      build({ siteDir: f.siteDir, webDistDir: f.webDistDir, outDir: f.outDir, elementVersion: '1.1.0' }),
      /\/js\/a\.mjs/,
    );
    assert.equal(await exists(f.outDir), false);
  }));
