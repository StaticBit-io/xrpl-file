import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, copyFile, readFile, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { HOST_FILES, resolveRequest } from '../resolve.mjs';
import { parseRedirects } from '../redirects.mjs';

async function siteRoot() {
  const root = await mkdtemp(join(tmpdir(), 'xf-site-resolve-'));
  await mkdir(join(root, 'ru'));
  await copyFile(new URL('../src/_redirects', import.meta.url), join(root, '_redirects'));
  await writeFile(join(root, 'index.html'), 'home');
  await writeFile(join(root, 'file.html'), 'file page');
  await writeFile(join(root, '404.html'), 'missing');
  await writeFile(join(root, 'ru', 'index.html'), 'ru home');
  const rules = parseRedirects(await readFile(join(root, '_redirects'), 'utf8'));
  return { root, rules };
}

async function withSite(fn) {
  const site = await siteRoot();
  try {
    await fn(site);
  } finally {
    await rm(site.root, { recursive: true, force: true });
  }
}

const failing = (code) => async () => {
  throw Object.assign(new Error(`stat failed: ${code}`), { code });
};

test('an HTML redirect keeps the query string and uses the status it is given', async () => {
  await withSite(async ({ root, rules }) => {
    assert.deepEqual(await resolveRequest(root, '/index.html', rules, { search: '?x=1', htmlStatus: 308 }), {
      status: 308,
      location: '/?x=1',
    });
    assert.deepEqual(await resolveRequest(root, '/file.html', rules, { search: '', htmlStatus: 308 }), {
      status: 308,
      location: '/file',
    });
  });
});

test('host files are hidden when asked', async () => {
  await withSite(async ({ root, rules }) => {
    const hidden = await resolveRequest(root, '/_redirects', rules, { hidden: HOST_FILES });
    assert.equal(hidden.status, 404);
    assert.equal(hidden.body.toString(), 'missing');
    const shown = await resolveRequest(root, '/_redirects', rules);
    assert.equal(shown.status, 200);
  });
});

test('a missing file is a miss and any other read error is a 500', async () => {
  await withSite(async ({ root, rules }) => {
    const broken = await resolveRequest(root, '/file', rules, { stat: failing('EIO') });
    assert.equal(broken.status, 500);
    assert.equal(broken.error.code, 'EIO');
    const notDir = (path) => (path.endsWith('404.html') ? stat(path) : failing('ENOTDIR')());
    const missing = await resolveRequest(root, '/file', rules, { stat: notDir });
    assert.equal(missing.status, 404);
  });
});

test('host files stay hidden behind an encoded slash', async () => {
  await withSite(async ({ root, rules }) => {
    for (const path of ['/%2F_redirects', '/js/%2F..%2F_redirects', '//_headers']) {
      const result = await resolveRequest(root, path, rules, { hidden: HOST_FILES });
      assert.equal(result.status, 404, path);
    }
  });
});

test('a path the file system rejects is a miss, not a fault', async () => {
  await withSite(async ({ root, rules }) => {
    assert.equal((await resolveRequest(root, '/a%00b', rules)).status, 404);
    for (const code of ['ENAMETOOLONG', 'EINVAL']) {
      assert.equal((await resolveRequest(root, '/file', rules, { stat: failing(code) })).status, 404, code);
    }
  });
});

test('the examples pages are served at their pretty URLs and a trailing slash is not one of them', async () => {
  await withSite(async ({ root, rules }) => {
    await writeFile(join(root, 'examples.html'), 'examples');
    await writeFile(join(root, 'ru', 'examples.html'), 'ru examples');
    const en = await resolveRequest(root, '/examples', rules);
    assert.equal(en.status, 200);
    assert.equal(en.body.toString(), 'examples');
    const ru = await resolveRequest(root, '/ru/examples', rules);
    assert.equal(ru.status, 200);
    assert.equal(ru.body.toString(), 'ru examples');
    const slash = await resolveRequest(root, '/ru/examples/', rules);
    assert.equal(slash.status, 200);
    assert.equal(slash.body.toString(), 'file page');
  });
});
