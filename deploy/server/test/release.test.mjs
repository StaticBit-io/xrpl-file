// The release and smoke scripts, run against temporary directories with sudo, systemctl, node and the
// smoke check replaced by stubs. Linux only (bash, flock, GNU tar): CI runs them, and locally
//   docker run --rm -v "$PWD":/w -w /w node:24 node --test deploy/server/test/*.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawn, spawnSync } from 'node:child_process';
import { chmod, mkdir, mkdtemp, readFile, readlink, readdir, rm, symlink, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { basename, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const linux = process.platform === 'linux';
const RELEASE = fileURLToPath(new URL('../xrpl-file-release', import.meta.url));
const SMOKE = fileURLToPath(new URL('../xrpl-file-smoke', import.meta.url));
const REPO = fileURLToPath(new URL('../../..', import.meta.url));
const C1 = '1'.repeat(40);
const C2 = '2'.repeat(40);
const C3 = '3'.repeat(40);

const STUBS = {
  sudo: '#!/bin/bash\necho "sudo $*" >> "$STUB_LOG"\n[ "$1" = -n ] && shift\nexec "$@"\n',
  systemctl: '#!/bin/bash\necho "systemctl $*" >> "$STUB_LOG"\n',
  node: '#!/bin/bash\necho "node PORT=$PORT $*" >> "$STUB_LOG"\nexec sleep 30\n',
  'xrpl-file-smoke':
    '#!/bin/bash\necho "smoke $1 $2" >> "$STUB_LOG"\n[ -f "$VERDICTS/$1" ] && [ "$(cat "$VERDICTS/$1")" = fail ] && exit 1\nexit 0\n',
};

/** A server root with stubs on PATH; `verdict(port, 'fail')` makes the smoke check fail there. */
async function stand() {
  const base = await mkdtemp(join(tmpdir(), 'xf-release-'));
  const root = join(base, 'srv');
  const bin = join(base, 'bin');
  const verdicts = join(base, 'verdicts');
  await mkdir(join(root, 'releases'), { recursive: true });
  await mkdir(bin);
  await mkdir(verdicts);
  for (const [name, body] of Object.entries(STUBS)) {
    await writeFile(join(bin, name), body);
    await chmod(join(bin, name), 0o755);
  }
  const log = join(base, 'log');
  await writeFile(log, '');
  const env = {
    ...process.env,
    PATH: `${bin}:${process.env.PATH}`,
    XRPL_FILE_ROOT: root,
    XRPL_FILE_CHECK_SECONDS: '2',
    XRPL_FILE_LOCK_WAIT: '1',
    STUB_LOG: log,
    VERDICTS: verdicts,
  };
  return {
    base,
    root,
    env,
    log: async () => (await readFile(log, 'utf8')).trim().split('\n').filter(Boolean),
    clearLog: () => writeFile(log, ''),
    verdict: (port, value) => writeFile(join(verdicts, String(port)), value),
    current: async () => basename(await readlink(join(root, 'current'))),
    lastGood: async () => basename(await readlink(join(root, 'last-good'))),
    releases: async () => (await readdir(join(root, 'releases'))).filter((n) => !n.startsWith('.')).sort(),
    cleanup: () => rm(base, { recursive: true, force: true }),
  };
}

/** A .tar.gz of a minimal release; `extra(dir)` may add hostile entries before packing. */
async function archive(base, { name = 'a', extra } = {}) {
  const dir = join(base, `pack-${name}-${Math.random().toString(16).slice(2)}`);
  await mkdir(join(dir, 'site', 'server'), { recursive: true });
  await mkdir(join(dir, 'site', 'dist'), { recursive: true });
  await writeFile(join(dir, 'site', 'server', 'main.mjs'), '// server\n');
  await writeFile(join(dir, 'site', 'dist', 'index.html'), 'home');
  if (extra) await extra(dir);
  const out = `${dir}.tar.gz`;
  execFileSync('tar', ['-czf', out, '-C', dir, '.']);
  return readFile(out);
}

function run(env, request, input = Buffer.alloc(0)) {
  const result = spawnSync('bash', [RELEASE], { env: { ...env, SSH_ORIGINAL_COMMAND: request }, input, encoding: 'utf8' });
  return { code: result.status, stderr: result.stderr, stdout: result.stdout };
}

async function released(s, commit) {
  const result = run(s.env, `release ${commit}`, await archive(s.base));
  assert.equal(result.code, 0, result.stderr);
  // Release directory names carry the time to the second.
  await new Promise((resolve) => setTimeout(resolve, 1100));
}

test('a good release becomes current and last good', { skip: !linux }, async () => {
  const s = await stand();
  try {
    const result = run(s.env, `release ${C1}`, await archive(s.base));
    assert.equal(result.code, 0, result.stderr);
    assert.match(await s.current(), new RegExp(`-${C1.slice(0, 12)}$`));
    assert.equal(await s.current(), await s.lastGood());
    const log = await s.log();
    const candidate = log.findIndex((l) => l.startsWith('node PORT=8081'));
    const candidateSmoke = log.findIndex((l) => l === `smoke 8081 ${C1}`);
    const restart = log.findIndex((l) => l === 'systemctl restart xrpl-file-site');
    const serviceSmoke = log.findIndex((l) => l === `smoke 8080 ${C1}`);
    assert.ok(candidate >= 0 && candidate < candidateSmoke && candidateSmoke < restart && restart < serviceSmoke, log.join('\n'));
  } finally {
    await s.cleanup();
  }
});

test('a candidate that fails its check never becomes current', { skip: !linux }, async () => {
  const s = await stand();
  try {
    await released(s, C1);
    const before = await s.current();
    await s.clearLog();
    await s.verdict(8081, 'fail');
    const result = run(s.env, `release ${C2}`, await archive(s.base));
    assert.notEqual(result.code, 0);
    assert.equal(await s.current(), before);
    assert.ok(!(await s.log()).some((l) => l.startsWith('systemctl')));
    assert.equal((await s.releases()).length, 1);
  } finally {
    await s.cleanup();
  }
});

test('a release that fails after the switch is replaced by the last good one', { skip: !linux }, async () => {
  const s = await stand();
  try {
    await released(s, C1);
    const good = await s.lastGood();
    await s.clearLog();
    await s.verdict(8080, 'fail');
    const result = run(s.env, `release ${C2}`, await archive(s.base));
    assert.notEqual(result.code, 0);
    assert.equal(await s.current(), good);
    assert.equal(await s.lastGood(), good);
    assert.equal((await s.log()).filter((l) => l === 'systemctl restart xrpl-file-site').length, 2);
  } finally {
    await s.cleanup();
  }
});

test('an interrupted run is repaired first', { skip: !linux }, async () => {
  const s = await stand();
  try {
    await released(s, C1);
    await released(s, C2);
    const good = await s.lastGood();
    const [older] = await s.releases();
    // A deploy cut off between the switch and its check leaves current on an unproven release.
    await symlink(join(s.root, 'releases', older), join(s.root, 'current.tmp'));
    execFileSync('mv', ['-T', join(s.root, 'current.tmp'), join(s.root, 'current')]);
    await s.clearLog();
    await s.verdict(8080, 'fail');
    run(s.env, 'rollback');
    const log = await s.log();
    assert.equal(log[0], 'sudo -n systemctl restart xrpl-file-site', log.join('\n'));
    assert.equal(await s.current(), good);
  } finally {
    await s.cleanup();
  }
});

test('a bad archive is refused before unpacking', { skip: !linux }, async () => {
  const s = await stand();
  try {
    const hostile = {
      symlink: (dir) => symlink('/etc/passwd', join(dir, 'site', 'dist', 'leak')),
      many: async (dir) => {
        for (let i = 0; i < 30; i++) await writeFile(join(dir, `f${i}`), 'x');
      },
    };
    for (const [name, extra] of Object.entries(hostile)) {
      const result = run({ ...s.env, XRPL_FILE_MAX_ENTRIES: '20' }, `release ${C1}`, await archive(s.base, { name, extra }));
      assert.notEqual(result.code, 0, name);
      assert.deepEqual(await s.releases(), [], name);
    }
    // Names tar itself would write outside the directory, built by hand.
    for (const evil of ['../escape', '/abs']) {
      const dir = join(s.base, `evil-${evil.length}`);
      await mkdir(dir, { recursive: true });
      await writeFile(join(dir, 'x'), 'x');
      const out = join(s.base, `evil-${evil.length}.tar.gz`);
      execFileSync('tar', ['-czf', out, '-C', dir, '--transform', `s|^x$|${evil}|`, '-P', 'x']);
      const result = run(s.env, `release ${C1}`, await readFile(out));
      assert.notEqual(result.code, 0, evil);
      assert.deepEqual(await s.releases(), [], evil);
    }
    assert.ok(!existsSync(join(s.base, 'escape')));
  } finally {
    await s.cleanup();
  }
});

test('requests other than release and rollback are refused', { skip: !linux }, async () => {
  const s = await stand();
  try {
    for (const request of ['', 'release abc', `release ${C1}; id`, `release ${"a".repeat(40).toUpperCase()}`, 'bash', 'rollback now']) {
      const result = run(s.env, request);
      assert.equal(result.code, 2, request);
      assert.match(result.stderr, /refused/, request);
    }
  } finally {
    await s.cleanup();
  }
});

test('two runs never interleave', { skip: !linux }, async () => {
  const s = await stand();
  try {
    await released(s, C1);
    const before = await s.current();
    await writeFile(join(s.root, '.lock'), '');
    const holder = spawn('flock', [join(s.root, '.lock'), 'sleep', '5']);
    await new Promise((resolve) => setTimeout(resolve, 300));
    const result = run(s.env, `release ${C2}`, await archive(s.base));
    holder.kill();
    assert.notEqual(result.code, 0);
    assert.match(result.stderr, /another release/);
    assert.equal(await s.current(), before);
  } finally {
    await s.cleanup();
  }
});

test('old releases are pruned but last good and its predecessor stay', { skip: !linux }, async () => {
  const s = await stand();
  try {
    for (let i = 0; i < 7; i++) await released(s, String(i).repeat(40));
    const all = await s.releases();
    assert.equal(all.length, 5);
    assert.ok(all.includes(await s.lastGood()));
    await s.verdict(8081, 'fail');
    run(s.env, `release ${C3}`, await archive(s.base));
    assert.equal((await readdir(join(s.root, 'releases'))).filter((n) => n.startsWith('.')).length, 0);
  } finally {
    await s.cleanup();
  }
});

test('rollback goes to the last good release before the current one', { skip: !linux }, async () => {
  const s = await stand();
  try {
    await released(s, C1);
    const first = await s.current();
    await released(s, C2);
    const result = run(s.env, 'rollback');
    assert.equal(result.code, 0, result.stderr);
    assert.equal(await s.current(), first);
    assert.equal(await s.lastGood(), first);
  } finally {
    await s.cleanup();
  }
});

function freePort() {
  return new Promise((resolve) => {
    const probe = createServer();
    probe.listen(0, '127.0.0.1', () => {
      const { port } = probe.address();
      probe.close(() => resolve(port));
    });
  });
}

test('the smoke checks pass against a real release and catch a wrong commit', { skip: !linux }, async () => {
  const work = await mkdtemp(join(tmpdir(), 'xf-smoke-'));
  let child;
  try {
    execFileSync('node', ['web/build.mjs'], { cwd: REPO, stdio: 'ignore' });
    execFileSync('node', ['site/build.mjs'], { cwd: REPO, stdio: 'ignore' });
    execFileSync('node', ['site/server/pack.mjs', join(work, 'r.tar.gz'), C1], { cwd: REPO, stdio: 'ignore' });
    await mkdir(join(work, 'r'));
    execFileSync('tar', ['-xzf', join(work, 'r.tar.gz'), '-C', join(work, 'r')]);
    const port = await freePort();
    child = spawn('node', [join(work, 'r', 'site', 'server', 'main.mjs')], {
      env: { ...process.env, PORT: String(port), CACHE_DIR: join(work, 'cache'), SITE_LEDGER: 'off' },
      stdio: 'ignore',
    });
    const env = { ...process.env, XRPL_FILE_SMOKE_DIST: join(work, 'r', 'site', 'dist') };
    let passed = false;
    for (let i = 0; i < 50 && !passed; i++) {
      passed = spawnSync('bash', [SMOKE, String(port), C1], { env }).status === 0;
      if (!passed) await new Promise((resolve) => setTimeout(resolve, 200));
    }
    assert.ok(passed, spawnSync('bash', [SMOKE, String(port), C1], { env, encoding: 'utf8' }).stderr);
    const wrong = spawnSync('bash', [SMOKE, String(port), C2], { env, encoding: 'utf8' });
    assert.notEqual(wrong.status, 0);
    assert.match(wrong.stderr, /release/);
  } finally {
    child?.kill('SIGKILL');
    await rm(work, { recursive: true, force: true });
  }
});
