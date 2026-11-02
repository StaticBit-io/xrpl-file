import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from '../build.mjs';

const webDir = join(dirname(fileURLToPath(import.meta.url)), '..');
const temps = [];

after(async () => {
  for (const dir of temps) await rm(dir, { recursive: true, force: true });
});

// The site serves /element/<version>/* as immutable and Pages rebuilds web/dist from scratch on
// every deploy, so a source change without a version bump would silently change the bytes behind
// a published version and break the integrity attribute of every page that embeds it.
test('the package version builds to the integrity recorded when it was released', async () => {
  const { version } = JSON.parse(await readFile(join(webDir, 'package.json'), 'utf8'));
  const recordPath = join(webDir, 'integrity', `${version}.json`);

  let record;
  try {
    record = JSON.parse(await readFile(recordPath, 'utf8'));
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    assert.fail(`web/integrity/${version}.json is missing; build ${version}, then copy web/dist/${version}/integrity.json there when the version is released`);
  }

  const outDir = await mkdtemp(join(tmpdir(), 'xrpl-file-released-'));
  temps.push(outDir);
  const { integrity } = await build({ webDir, outDir, version });

  assert.deepEqual(
    integrity,
    record,
    `web/src now builds ${version} to different bytes than the ones released. Bump the version in web/package.json, `
      + `or, for a version that has not been released, update web/integrity/${version}.json deliberately`,
  );
});

// The README's snippet is copied by hand into pages; a stale hash there makes the browser refuse the script.
test('the README snippet carries the integrity recorded for the package version', async () => {
  const { version } = JSON.parse(await readFile(join(webDir, 'package.json'), 'utf8'));
  const record = JSON.parse(await readFile(join(webDir, 'integrity', `${version}.json`), 'utf8'));
  const readme = await readFile(join(webDir, '..', 'README.md'), 'utf8');
  const snippet = new RegExp(`/${version.replaceAll('.', '\.')}/xrpl-file-element\.js" integrity="([^"]+)"`).exec(readme);
  assert.ok(snippet, `README.md has no snippet for ${version}`);
  assert.equal(snippet[1], record['xrpl-file-element.js'], `update the integrity in README.md to web/integrity/${version}.json`);
});
