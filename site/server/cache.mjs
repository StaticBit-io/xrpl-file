// The card handler's cache on the server, in place of Workers' caches.default. One file per entry,
// written to a temporary name and renamed, so a reader sees a whole entry or none. It never fails a
// request: the handler sends a robot to the generic card when `match` rejects, so every error here is
// a miss or a skipped write, and a line in the journal.
import { createHash, randomBytes } from 'node:crypto';
import { mkdir, readdir, readFile, rename, rm, stat, unlink, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

const MAX_AGE = /(?:^|[\s,])max-age=(\d+)/;
const MISSING = new Set(['ENOENT', 'ENOTDIR']);

function maxAgeOf(response) {
  const match = MAX_AGE.exec(response.headers.get('cache-control') ?? '');
  return match ? Number(match[1]) : 0;
}

function decodeEntry(bytes) {
  const newline = bytes.indexOf(0x0a);
  if (newline < 0) throw new Error('no header line');
  const meta = JSON.parse(bytes.subarray(0, newline).toString('utf8'));
  if (!Number.isInteger(meta.status) || !Array.isArray(meta.headers) || !Number.isFinite(meta.storedAt) || !Number.isFinite(meta.maxAge)) {
    throw new Error('bad header line');
  }
  return { meta, body: bytes.subarray(newline + 1) };
}

/**
 * @param {{ dir: string, maxBytes?: number, generation: string, isPageKey: (key: string) => boolean,
 *   now?: () => number, log?: (line: string) => void }} options
 *   Page entries embed the built file page, so they live under `pages/<generation>` and a release
 *   with another file page starts without them; other entries live under `other`.
 */
export async function createDiskCache({ dir, maxBytes = 256 * 1024 * 1024, generation, isPageKey, now = Date.now, log = () => {} }) {
  const pagesDir = join(dir, 'pages', generation);
  const otherDir = join(dir, 'other');
  // file path -> { size, used }; `total` also counts writes in progress.
  const index = new Map();
  let total = 0;

  try {
    await mkdir(pagesDir, { recursive: true });
    await mkdir(otherDir, { recursive: true });
    for (const name of await readdir(join(dir, 'pages'))) {
      if (name !== generation) await rm(join(dir, 'pages', name), { recursive: true, force: true });
    }
    for (const folder of [pagesDir, otherDir]) {
      for (const name of await readdir(folder)) {
        const file = join(folder, name);
        if (name.endsWith('.tmp')) {
          await unlink(file).catch(() => {});
          continue;
        }
        const info = await stat(file);
        index.set(file, { size: info.size, used: info.mtimeMs });
        total += info.size;
      }
    }
  } catch (error) {
    log(`cache ${dir}: ${error.code ?? ''} ${error.message}`);
  }

  const fileFor = (key) => join(isPageKey(key) ? pagesDir : otherDir, `${createHash('sha256').update(key).digest('hex')}.entry`);

  async function forget(file) {
    const known = index.get(file);
    if (known) {
      total -= known.size;
      index.delete(file);
    }
    await unlink(file).catch(() => {});
  }

  async function evict() {
    if (total <= maxBytes) return;
    const oldestFirst = [...index.entries()].sort((a, b) => a[1].used - b[1].used);
    for (const [file] of oldestFirst) {
      if (total <= maxBytes) break;
      await forget(file);
    }
  }

  async function match(key) {
    const file = fileFor(key);
    let bytes;
    try {
      bytes = await readFile(file);
    } catch (error) {
      if (!MISSING.has(error?.code)) log(`cache read ${file}: ${error.code ?? ''} ${error.message}`);
      return undefined;
    }
    let entry;
    try {
      entry = decodeEntry(bytes);
    } catch (error) {
      log(`cache entry ${file} damaged: ${error.message}`);
      await forget(file);
      return undefined;
    }
    if (now() > entry.meta.storedAt + entry.meta.maxAge * 1000) {
      await forget(file);
      return undefined;
    }
    const known = index.get(file);
    if (known) known.used = now();
    else {
      index.set(file, { size: bytes.length, used: now() });
      total += bytes.length;
    }
    return new Response(entry.body, { status: entry.meta.status, headers: entry.meta.headers });
  }

  async function put(key, response) {
    const maxAge = maxAgeOf(response);
    if (maxAge <= 0) return;
    const file = fileFor(key);
    const temp = `${file}.${randomBytes(6).toString('hex')}.tmp`;
    let size = 0;
    try {
      const body = Buffer.from(await response.arrayBuffer());
      const meta = { status: response.status, headers: [...response.headers], storedAt: now(), maxAge };
      const bytes = Buffer.concat([Buffer.from(JSON.stringify(meta) + '\n', 'utf8'), body]);
      size = bytes.length;
      total += size;
      await writeFile(temp, bytes);
      await rename(temp, file);
      const previous = index.get(file);
      if (previous) total -= previous.size;
      index.set(file, { size, used: now() });
      size = 0;
      await evict();
    } catch (error) {
      total -= size;
      await unlink(temp).catch(() => {});
      log(`cache write ${file}: ${error.code ?? ''} ${error.message}`);
    }
  }

  return { match, put };
}
