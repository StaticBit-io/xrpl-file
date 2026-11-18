import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createDiskCache } from '../server/cache.mjs';

const isPageKey = (key) => key.includes('/page/');

async function withDir(fn) {
  const dir = await mkdtemp(join(tmpdir(), 'xf-cache-'));
  try {
    await fn(dir);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
}

const entry = (body, maxAge = 300, extra = {}) =>
  new Response(body, { headers: { 'Cache-Control': `public, max-age=${maxAge}`, ...extra } });

async function filesUnder(dir) {
  return (await readdir(dir, { recursive: true, withFileTypes: true })).filter((e) => e.isFile());
}

test('an entry comes back with its status, headers and body until its max-age', async () => {
  await withDir(async (dir) => {
    let clock = 1_000_000;
    const cache = await createDiskCache({ dir, generation: 'a', isPageKey, now: () => clock });
    await cache.put('k', entry('abc', 300, { 'X-A': '1' }));
    clock += 299_000;
    const hit = await cache.match('k');
    assert.equal(hit.status, 200);
    assert.equal(hit.headers.get('x-a'), '1');
    assert.equal(await hit.text(), 'abc');
    clock += 2_000;
    assert.equal(await cache.match('k'), undefined);
  });
});

test('a response without max-age is not stored', async () => {
  await withDir(async (dir) => {
    const cache = await createDiskCache({ dir, generation: 'a', isPageKey });
    await cache.put('k', new Response('x', { headers: { 'Cache-Control': 'no-cache' } }));
    assert.equal(await cache.match('k'), undefined);
  });
});

test('a cache that cannot be read or written is a miss, never an error', async () => {
  await withDir(async (dir) => {
    const file = join(dir, 'not-a-dir');
    await writeFile(file, 'x');
    const lines = [];
    const cache = await createDiskCache({ dir: file, generation: 'a', isPageKey, log: (line) => lines.push(line) });
    await cache.put('k', entry('abc'));
    assert.equal(await cache.match('k'), undefined);
    assert.ok(lines.length > 0);
  });
});

test('a damaged entry is a miss and is deleted', async () => {
  await withDir(async (dir) => {
    const cache = await createDiskCache({ dir, generation: 'a', isPageKey });
    await cache.put('k', entry('abc'));
    const [stored] = await filesUnder(dir);
    await writeFile(join(stored.parentPath, stored.name), '{not json');
    assert.equal(await cache.match('k'), undefined);
    assert.equal((await filesUnder(dir)).length, 0);
  });
});

test('page entries belong to their generation', async () => {
  await withDir(async (dir) => {
    const first = await createDiskCache({ dir, generation: 'a', isPageKey });
    await first.put('https://x/__cards/v1/page/mainnet/H', entry('page'));
    await first.put('https://x/__cards/v1/picture/mainnet/H', entry('picture'));
    const second = await createDiskCache({ dir, generation: 'b', isPageKey });
    assert.equal(await second.match('https://x/__cards/v1/page/mainnet/H'), undefined);
    assert.equal(await (await second.match('https://x/__cards/v1/picture/mainnet/H')).text(), 'picture');
    assert.deepEqual(await readdir(join(dir, 'pages')), ['b']);
  });
});

test('past the cap the least recently used entries go', async () => {
  await withDir(async (dir) => {
    let clock = 1_000_000;
    const cache = await createDiskCache({ dir, maxBytes: 3500, generation: 'a', isPageKey, now: () => clock });
    const kb = 'x'.repeat(1000);
    for (const key of ['one', 'two', 'three']) {
      await cache.put(key, entry(kb));
      clock += 1000;
    }
    assert.ok(await cache.match('one'));
    clock += 1000;
    await cache.put('four', entry(kb));
    assert.equal(await cache.match('two'), undefined);
    assert.ok(await cache.match('one'));
    assert.ok(await cache.match('four'));
  });
});

test('a write is whole or absent', async () => {
  await withDir(async (dir) => {
    const cache = await createDiskCache({ dir, generation: 'a', isPageKey });
    const first = 'A'.repeat(200_000);
    const second = 'B'.repeat(200_000);
    await Promise.all([cache.put('k', entry(first)), cache.put('k', entry(second))]);
    const body = await (await cache.match('k')).text();
    assert.ok(body === first || body === second);
    assert.equal((await filesUnder(dir)).filter((e) => e.name.endsWith('.tmp')).length, 0);
  });
});
