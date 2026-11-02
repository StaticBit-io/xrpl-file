import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, copyFile, readFile, rm } from 'node:fs/promises';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createDevServer, listenOnFreePort, resolveRequest } from '../serve.mjs';
import { parseRedirects } from '../redirects.mjs';

const HASH = '32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59';

async function siteRoot() {
  const root = await mkdtemp(join(tmpdir(), 'xf-site-serve-'));
  for (const dir of ['ru', 'js', 'data']) await mkdir(join(root, dir));
  await copyFile(new URL('../src/_redirects', import.meta.url), join(root, '_redirects'));
  await writeFile(join(root, 'index.html'), 'home');
  await writeFile(join(root, 'file.html'), 'file page');
  await writeFile(join(root, '404.html'), 'missing');
  await writeFile(join(root, 'ru', 'index.html'), 'ru home');
  await writeFile(join(root, 'js', 'route.mjs'), 'js');
  await writeFile(join(root, 'data', 'price.json'), '{}');
  const rules = parseRedirects(await readFile(join(root, '_redirects'), 'utf8'));
  return { root, rules };
}

test('an existing two-segment file is served as itself, not as the file page', async () => {
  const { root, rules } = await siteRoot();
  try {
    for (const [path, body] of [['/ru/index.html', 'ru home'], ['/js/route.mjs', 'js'], ['/data/price.json', '{}']]) {
      const result = await resolveRequest(root, path, rules);
      assert.equal(result.status, 200, path);
      assert.equal(result.body.toString(), body, path);
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('a link path is rewritten to the file page, a missing static path is a 404', async () => {
  const { root, rules } = await siteRoot();
  try {
    const page = await resolveRequest(root, `/testnet/${HASH}`, rules);
    assert.equal(page.status, 200);
    assert.equal(page.body.toString(), 'file page');
    const missing = await resolveRequest(root, '/js/absent.mjs', rules);
    assert.equal(missing.status, 404);
    assert.equal(missing.body.toString(), 'missing');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('pages resolve as directories: / and /ru/ serve their index, /ru redirects to /ru/', async () => {
  const { root, rules } = await siteRoot();
  try {
    assert.equal((await resolveRequest(root, '/', rules)).body.toString(), 'home');
    assert.equal((await resolveRequest(root, '/ru/', rules)).body.toString(), 'ru home');
    assert.deepEqual(await resolveRequest(root, '/ru', rules), { status: 301, location: '/ru/' });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('a path that climbs out of the root is not served', async () => {
  const { root, rules } = await siteRoot();
  try {
    const result = await resolveRequest(root, '/js/..%2f..%2fsecret', rules);
    assert.equal(result.status, 404);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('on a busy port the server takes the next one and never the busy one', async () => {
  const blocker = createServer();
  await new Promise((resolve) => blocker.listen(0, '127.0.0.1', resolve));
  const busy = blocker.address().port;
  const { root } = await siteRoot();
  const server = createDevServer(root);
  try {
    const port = await listenOnFreePort(server, busy);
    assert.notEqual(port, busy);
    assert.ok(port > busy);
    assert.equal(server.address().address, '127.0.0.1');
  } finally {
    await new Promise((resolve) => server.close(resolve));
    await new Promise((resolve) => blocker.close(resolve));
    await rm(root, { recursive: true, force: true });
  }
});
