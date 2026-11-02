import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from '../build.mjs';
import { pack } from '../pack.mjs';
import { ELEMENT_PACKAGE } from '../../site/src/js/config.mjs';

const webDir = join(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = join(webDir, '..');
const DIST_FILES = ['xrpl-file-element.js', 'mp4box.min.js', 'mp4box-LICENSE.txt', 'xrpl-file-reader.mjs', 'integrity.json'];
const temps = [];

after(async () => {
  for (const dir of temps) await rm(dir, { recursive: true, force: true });
});

async function tempDir() {
  const dir = await mkdtemp(join(tmpdir(), 'xrpl-file-pack-'));
  temps.push(dir);
  return dir;
}

async function webVersion() {
  return JSON.parse(await readFile(join(webDir, 'package.json'), 'utf8')).version;
}

// A real build of the current version into a temp dist, so the test never leans on a manual run.
async function builtDist() {
  const distRoot = await tempDir();
  const version = await webVersion();
  await build({ webDir, outDir: join(distRoot, version), version });
  return { distRoot, version };
}

async function packed(options = {}) {
  const { distRoot, version } = await builtDist();
  const outRoot = await tempDir();
  const result = await pack({ webDir, distRoot, outRoot, ...options });
  return { distRoot, outRoot, version, ...result };
}

async function listAll(dir) {
  const entries = await readdir(dir, { recursive: true, withFileTypes: true });
  return entries
    .filter((entry) => entry.isFile())
    .map((entry) => relative(dir, join(entry.parentPath, entry.name)).replaceAll('\\', '/'))
    .sort();
}

async function readManifest(outDir) {
  return JSON.parse(await readFile(join(outDir, 'package.json'), 'utf8'));
}

test('the package directory holds exactly the dist files, package.json, README.md and LICENSE', async () => {
  const { outDir } = await packed();
  assert.deepEqual(await listAll(outDir), ['LICENSE', 'README.md', ...DIST_FILES, 'package.json'].sort());
});

test('the package directory is <outRoot>/<version>', async () => {
  const { outDir, outRoot, version } = await packed();
  assert.equal(outDir, join(outRoot, version));
});

test('package.json carries the name from the site config and the version of web/package.json', async () => {
  const { outDir, version } = await packed();
  const manifest = await readManifest(outDir);
  assert.equal(manifest.name, ELEMENT_PACKAGE);
  assert.equal(manifest.version, version);
  assert.equal(manifest.license, 'MIT');
  assert.equal(manifest.repository.url, 'https://github.com/StaticBit-io/xrpl-file');
  assert.equal(typeof manifest.description, 'string');
  assert.ok(manifest.description.length > 0);
  assert.equal(manifest.dependencies, undefined);
  assert.equal(manifest.devDependencies, undefined);
  assert.equal(manifest.scripts, undefined);
  assert.equal(manifest.private, undefined);
});

test('package.json lists every shipped file except itself under files', async () => {
  const { outDir } = await packed();
  const shipped = (await listAll(outDir)).filter((name) => name !== 'package.json');
  assert.deepEqual([...(await readManifest(outDir)).files].sort(), shipped);
});

test('a scoped name is publishable with public access, an unscoped one needs no publishConfig', async () => {
  const scoped = await readManifest((await packed({ packageName: '@staticbit/xrpl-file-element' })).outDir);
  assert.equal(scoped.name, '@staticbit/xrpl-file-element');
  assert.deepEqual(scoped.publishConfig, { access: 'public' });

  const plain = await readManifest((await packed({ packageName: 'xrpl-file-element' })).outDir);
  assert.equal(plain.publishConfig, undefined);
});

test('the dist files are byte-identical to web/dist/<version>', async () => {
  const { outDir, distRoot, version } = await packed();
  for (const name of DIST_FILES) {
    const original = await readFile(join(distRoot, version, name));
    const copy = await readFile(join(outDir, name));
    assert.ok(copy.equals(original), `${name} differs from the dist copy`);
  }
});

test('files that are not part of the dist set are left out of the package', async () => {
  const { distRoot, version } = await builtDist();
  await writeFile(join(distRoot, version, 'stray.txt'), 'not shipped');
  const { outDir } = await pack({ webDir, distRoot, outRoot: await tempDir() });
  assert.ok(!(await listAll(outDir)).includes('stray.txt'));
});

test('the licence is the repository LICENSE, byte for byte', async () => {
  const { outDir } = await packed();
  assert.ok((await readFile(join(outDir, 'LICENSE'))).equals(await readFile(join(repoRoot, 'LICENSE'))));
});

test('the README is the "Embed a file" section with the jsDelivr snippet and the real integrity', async () => {
  const { outDir, version } = await packed({ packageName: '@staticbit/xrpl-file-element' });
  const readme = await readFile(join(outDir, 'README.md'), 'utf8');
  const integrity = JSON.parse(await readFile(join(outDir, 'integrity.json'), 'utf8'));

  assert.match(readme, /^# @staticbit\/xrpl-file-element\n/);
  assert.ok(readme.includes(`src="https://cdn.jsdelivr.net/npm/@staticbit/xrpl-file-element@${version}/xrpl-file-element.js"`));
  assert.ok(readme.includes(`integrity="${integrity['xrpl-file-element.js']}"`));
  assert.ok(readme.includes('crossorigin="anonymous"'));
  assert.ok(!readme.includes('<host>'));
  assert.ok(!readme.includes('npm run build'));
  assert.ok(readme.includes('| Attribute | Values | Meaning |'));
  assert.ok(!readme.includes('## Reading a file'));
  assert.ok(!readme.includes('## Publishing a file'));
});

test('packing again replaces the directory instead of leaving old files in it', async () => {
  const { distRoot, outRoot, outDir } = await packed();
  await writeFile(join(outDir, 'leftover.txt'), 'old');
  await pack({ webDir, distRoot, outRoot });
  assert.ok(!(await listAll(outDir)).includes('leftover.txt'));
});

test('it refuses when web/dist/<version> is missing and says how to build it', async () => {
  const outRoot = await tempDir();
  const version = await webVersion();
  await assert.rejects(
    pack({ webDir, distRoot: await tempDir(), outRoot }),
    (error) => /node web\/build\.mjs/.test(error.message) && error.message.includes(version),
  );
  assert.deepEqual(await readdir(outRoot), []);
});

test('it refuses when one dist file is missing', async () => {
  const { distRoot, version } = await builtDist();
  await rm(join(distRoot, version, 'integrity.json'));
  await assert.rejects(pack({ webDir, distRoot, outRoot: await tempDir() }), /integrity\.json/);
});

test('it refuses a package name npm would not accept', async () => {
  const { distRoot } = await builtDist();
  await assert.rejects(pack({ webDir, distRoot, outRoot: await tempDir(), packageName: 'Not A Name' }), /package name/);
});

test('it refuses a README whose Embed section lost its snippet', async () => {
  const { distRoot } = await builtDist();
  const repo = await tempDir();
  await mkdir(join(repo, 'web'), { recursive: true });
  await writeFile(join(repo, 'README.md'), '# x\n\n## Embed a file\n\nno snippet here\n\n## Next\n');
  await writeFile(join(repo, 'LICENSE'), 'MIT');
  await assert.rejects(pack({ webDir, distRoot, outRoot: await tempDir(), repoRoot: repo }), /README/);
});
