// Assembles the deployable site into site/dist:
//   site/src/**                  -> /            (pages, scripts, styles, _redirects, _headers)
//   web/dist/<version>/*         -> /element/<version>/   (the element and the reader bundle)
//   site/data/price.json         -> /data/price.json
// The site loads the element and the reader from that one copy; nothing else carries reader code.
//
//   node site/build.mjs            (run `cd web && node build.mjs` first)
import { cp, mkdir, readdir, readFile, rm, stat } from 'node:fs/promises';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findShadowed, parseRedirects } from './redirects.mjs';

export const ELEMENT_VERSION = '1.1.0';
// Cloudflare Pages rejects a deployment with any file over 25 MiB.
export const MAX_FILE_BYTES = 25 * 1024 * 1024;
const HOST_FILES = new Set(['_redirects', '_headers']);

async function isDirectory(path) {
  try {
    return (await stat(path)).isDirectory();
  } catch {
    return false;
  }
}

async function listFiles(root, dir = root) {
  const files = [];
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) files.push(...(await listFiles(root, full)));
    else files.push(full);
  }
  return files;
}

function isInside(parent, child) {
  const rel = relative(resolve(parent), resolve(child));
  return rel === '' || (!rel.startsWith('..') && !isAbsolute(rel));
}

async function checkSizes(outDir, maxFileBytes) {
  const tooBig = [];
  for (const file of await listFiles(outDir)) {
    const { size } = await stat(file);
    if (size > maxFileBytes) tooBig.push(`${relative(outDir, file).split(sep).join('/')} (${size} bytes)`);
  }
  if (tooBig.length > 0) {
    throw new Error(`files over the ${maxFileBytes}-byte limit: ${tooBig.join(', ')}`);
  }
}

async function checkRewrites(outDir) {
  let text;
  try {
    text = await readFile(join(outDir, '_redirects'), 'utf8');
  } catch {
    return;
  }
  const files = (await listFiles(outDir))
    .map((file) => relative(outDir, file).split(sep).join('/'))
    .filter((file) => !HOST_FILES.has(file));
  const shadowed = findShadowed(files, parseRedirects(text));
  if (shadowed.length > 0) {
    throw new Error(
      `_redirects would send these static files somewhere else: ${shadowed.join(', ')}. ` +
        'Add a pass-through rule (/<folder>/:file /<folder>/:file 200) above the file-page rule.',
    );
  }
}

/**
 * @param {{ siteDir: string, webDistDir: string, outDir: string, elementVersion?: string, maxFileBytes?: number }} options
 */
export async function build({ siteDir, webDistDir, outDir, elementVersion = ELEMENT_VERSION, maxFileBytes = MAX_FILE_BYTES }) {
  const elementDir = join(webDistDir, elementVersion);
  if (!(await isDirectory(elementDir))) {
    throw new Error(`element build ${elementVersion} not found at ${elementDir}; run "node build.mjs" in web/ first`);
  }
  const priceFile = join(siteDir, 'data', 'price.json');
  if (!(await stat(priceFile).then((s) => s.isFile(), () => false))) {
    throw new Error(`${priceFile} not found; see site/README.md for how to generate it`);
  }
  if (isInside(outDir, siteDir) || isInside(outDir, webDistDir)) {
    throw new Error(`refusing to build into ${outDir}: it contains the sources`);
  }

  await rm(outDir, { recursive: true, force: true });
  try {
    await cp(join(siteDir, 'src'), outDir, { recursive: true });
    await cp(elementDir, join(outDir, 'element', elementVersion), { recursive: true });
    await mkdir(join(outDir, 'data'), { recursive: true });
    await cp(priceFile, join(outDir, 'data', 'price.json'));
    await checkSizes(outDir, maxFileBytes);
    await checkRewrites(outDir);
  } catch (error) {
    // A half-built or refused tree must not be mistaken for something deployable.
    await rm(outDir, { recursive: true, force: true });
    throw error;
  }
}

const here = dirname(fileURLToPath(import.meta.url));

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const outDir = join(here, 'dist');
  try {
    await build({ siteDir: here, webDistDir: join(here, '..', 'web', 'dist'), outDir });
    console.log(`site built into ${outDir}`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
