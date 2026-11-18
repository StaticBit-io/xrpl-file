// The real site built into a temporary directory once per test process, against a stand-in element
// build: the card tests need the built file page and banner, not the element's code.
import { rmSync } from 'node:fs';
import { mkdtemp, mkdir, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build, ELEMENT_VERSION } from '../../build.mjs';

let built = null;

async function buildOnce() {
  const root = await mkdtemp(join(tmpdir(), 'xf-site-cards-'));
  const webDistDir = join(root, 'web-dist');
  const elementDir = join(webDistDir, ELEMENT_VERSION);
  await mkdir(elementDir, { recursive: true });
  await writeFile(join(elementDir, 'xrpl-file-element.js'), '// element');
  await writeFile(join(elementDir, 'integrity.json'), JSON.stringify({ 'xrpl-file-element.js': 'sha384-TESTVALUE' }));
  const outDir = join(root, 'dist');
  await build({ siteDir: fileURLToPath(new URL('../..', import.meta.url)), webDistDir, outDir, allowTestnetExamples: true });
  process.once('exit', () => rmSync(root, { recursive: true, force: true }));
  return {
    outDir,
    filePage: await readFile(join(outDir, 'file.html'), 'utf8'),
    banner: new Uint8Array(await readFile(join(outDir, 'og-banner.png'))),
  };
}

/** @returns {Promise<{ outDir: string, filePage: string, banner: Uint8Array }>} */
export function builtSite() {
  built ??= buildOnce();
  return built;
}
