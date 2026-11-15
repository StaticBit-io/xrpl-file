import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { RELEASE_PATHS } from '../server/release-files.mjs';
import { packRelease } from '../server/pack.mjs';
import { builtSite } from './support/built-site.mjs';

const REPO = fileURLToPath(new URL('../..', import.meta.url));
const COMMIT = 'a'.repeat(40);
const HASH = '9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5';

function freePort() {
  return new Promise((resolve) => {
    const probe = createServer();
    probe.listen(0, '127.0.0.1', () => {
      const { port } = probe.address();
      probe.close(() => resolve(port));
    });
  });
}

async function waitForServer(url, child, ms = 10_000) {
  const until = Date.now() + ms;
  while (Date.now() < until) {
    if (child.exitCode !== null) throw new Error(`server exited with ${child.exitCode}`);
    try {
      return await fetch(url);
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
  }
  throw new Error('server did not start');
}

test('every release path exists in the repository', () => {
  for (const path of RELEASE_PATHS.filter((p) => p !== 'site/dist')) {
    assert.ok(existsSync(join(REPO, path)), path);
  }
});

test('an unpacked release starts and serves the site', async () => {
  const { outDir } = await builtSite();
  const work = await mkdtemp(join(tmpdir(), 'xf-release-'));
  const archive = join(work, 'release.tar.gz');
  const unpacked = join(work, 'unpacked');
  const cacheDir = join(work, 'cache');
  let child;
  try {
    await packRelease({ out: archive, commit: COMMIT, repoRoot: REPO, distDir: outDir });
    await mkdir(unpacked);
    // Relative names: GNU tar in Git Bash reads "C:" in an absolute path as a remote host.
    execFileSync('tar', ['-xzf', 'release.tar.gz', '-C', 'unpacked'], { cwd: work });
    const port = await freePort();
    child = spawn(process.execPath, [join(unpacked, 'site', 'server', 'main.mjs')], {
      env: { ...process.env, PORT: String(port), CACHE_DIR: cacheDir, SITE_LEDGER: 'off' },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let output = '';
    child.stdout.on('data', (chunk) => { output += chunk; });
    child.stderr.on('data', (chunk) => { output += chunk; });
    const home = await waitForServer(`http://127.0.0.1:${port}/`, child).catch((error) => {
      throw new Error(`${error.message}\n${output}`);
    });
    assert.equal(home.status, 200);
    assert.equal(home.headers.get('x-xrpl-file-release'), COMMIT);
    const card = await fetch(`http://127.0.0.1:${port}/mainnet/${HASH}`, { headers: { 'user-agent': 'TelegramBot (like TwitterBot)' } });
    assert.equal(card.status, 200);
    assert.match(card.headers.get('content-type'), /text\/html/);
    const picture = await fetch(`http://127.0.0.1:${port}/og/mainnet/${HASH}`, { headers: { 'user-agent': 'TelegramBot (like TwitterBot)' } });
    assert.equal(picture.status, 200);
    assert.match(picture.headers.get('content-type'), /^image\//);
    // Windows has no SIGTERM to deliver to a handler; the drain is checked on Linux (CI).
    if (process.platform !== 'win32') {
      const exited = new Promise((resolve) => child.once('exit', (code) => resolve(code)));
      child.kill('SIGTERM');
      const code = await Promise.race([exited, new Promise((resolve) => setTimeout(() => resolve('timeout'), 6000))]);
      assert.equal(code, 0);
    }
  } finally {
    if (child && child.exitCode === null) child.kill('SIGKILL');
    await rm(work, { recursive: true, force: true });
  }
});
