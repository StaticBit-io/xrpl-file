import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { createHash } from 'node:crypto';
import { cp, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { build } from '../build.mjs';
import { ENDPOINTS } from '../src/ledger.mjs';
import { MANIFEST_FORMAT } from '../src/manifest.mjs';

const webDir = join(dirname(fileURLToPath(import.meta.url)), '..');
const VERSION = '0.0.0-test';
const PLACEHOLDER = '__MP4BOX_INTEGRITY__';
const temps = [];

async function tempDir() {
  const dir = await mkdtemp(join(tmpdir(), 'xrpl-file-build-'));
  temps.push(dir);
  return dir;
}

// A copy of web/ that a test can bend: only what the build reads (src, vendor), none of node_modules.
async function copyOfWebWith(file, text) {
  const copy = await tempDir();
  await cp(join(webDir, 'src'), join(copy, 'src'), { recursive: true });
  await cp(join(webDir, 'vendor'), join(copy, 'vendor'), { recursive: true });
  if (file) {
    await mkdir(dirname(join(copy, file)), { recursive: true });
    await writeFile(join(copy, file), text);
  }
  return copy;
}

async function copyOfWebAppending(file, text) {
  const copy = await copyOfWebWith();
  await writeFile(join(copy, file), (await readFile(join(copy, file), 'utf8')) + text);
  return copy;
}

after(async () => {
  for (const dir of temps) await rm(dir, { recursive: true, force: true });
});

// Just enough of a page for the bundle to run to the end of its top level: the element class extends
// HTMLElement and registers itself, and nothing else is touched until an element is connected.
function browserStubs(defined) {
  class HTMLElement {}
  const registry = new Map();
  return {
    HTMLElement,
    customElements: {
      get: (name) => registry.get(name),
      define: (name, constructor) => {
        registry.set(name, constructor);
        defined.push(name);
      },
    },
    document: { currentScript: { src: 'https://cdn.example/1.0.0/xrpl-file-element.js' }, readyState: 'complete' },
    URL,
    TextDecoder,
    TextEncoder,
    AbortController,
    console,
  };
}

test('the bundle is a classic script that defines the element once', async () => {
  const out = await tempDir();
  await build({ webDir, outDir: out, version: VERSION });
  const code = await readFile(join(out, 'xrpl-file-element.js'), 'utf8');
  assert.doesNotThrow(() => new vm.Script(code));
  assert.ok(code.startsWith(`/*! xrpl-file element ${VERSION} | MIT */\n`));

  const defined = [];
  const context = vm.createContext(browserStubs(defined));
  vm.runInContext(code, context);
  vm.runInContext(code, context);
  assert.deepEqual(defined, ['xrpl-file']);
});

test('the bundle is plain ASCII, so the page encoding cannot change it', async () => {
  const out = await tempDir();
  await build({ webDir, outDir: out, version: VERSION });
  const bytes = await readFile(join(out, 'xrpl-file-element.js'));
  assert.ok(bytes.every((byte) => byte < 0x80));
  assert.ok(bytes.includes(Buffer.from('\\u2713 ')), 'the check mark survives as an escape');
  assert.ok(bytes.includes(Buffer.from(' \\u00b7 ')), 'the dot survives as an escape');
  assert.ok(bytes.includes(Buffer.from('\\u043f\\u0440\\u043e\\u0432\\u0435\\u0440\\u0435\\u043d\\u043e')), 'Cyrillic survives as escapes');
});

test('the output directory holds the five files and nothing else', async () => {
  const out = await tempDir();
  await build({ webDir, outDir: out, version: VERSION });
  assert.deepEqual((await readdir(out)).sort(),
    ['integrity.json', 'mp4box-LICENSE.txt', 'mp4box.min.js', 'xrpl-file-element.js', 'xrpl-file-reader.mjs']);
  assert.deepEqual(await readFile(join(out, 'mp4box-LICENSE.txt')), await readFile(join(webDir, 'vendor', 'mp4box-LICENSE.txt')));
});

// Evaluates the published mp4box copy in a bare context and reports what it added to the global scope.
async function runPublishedMp4box(file) {
  const code = await readFile(file, 'utf8');
  const context = vm.createContext({ console, setTimeout, clearTimeout, TextDecoder, TextEncoder });
  const globals = () => vm.runInContext('Object.getOwnPropertyNames(globalThis)', context);
  const before = new Set(globals());
  vm.runInContext(code, context);
  return { context, added: globals().filter((name) => !before.has(name)) };
}

test('the published mp4box keeps the vendored bytes verbatim inside a wrapper, licence comment first', async () => {
  const out = await tempDir();
  await build({ webDir, outDir: out, version: VERSION });
  const vendored = await readFile(join(webDir, 'vendor', 'mp4box-0.5.4.min.js'));
  const published = await readFile(join(out, 'mp4box.min.js'));
  assert.ok(published.subarray(0, 24).toString().startsWith('/*! mp4box 19-03-2025 */'), 'the vendored licence comment is still the first thing');
  assert.ok(published.includes(vendored.subarray(vendored.indexOf('*/') + 2)), 'everything after the comment is carried over untouched');
  assert.ok(published.length > vendored.length);
});

test('the published mp4box adds MP4Box to the page and nothing else', async () => {
  const out = await tempDir();
  await build({ webDir, outDir: out, version: VERSION });
  const { context, added } = await runPublishedMp4box(join(out, 'mp4box.min.js'));
  assert.deepEqual(Array.from(added), ['MP4Box']);
  assert.equal(typeof context.MP4Box.createFile, 'function');
  assert.equal(vm.runInContext('typeof Log', context), 'undefined');
  assert.equal(vm.runInContext('typeof ISOFile', context), 'undefined');
  assert.equal(vm.runInContext('typeof DataStream', context), 'undefined');
});

test('the raw vendored script is what leaks, which is what the wrapper is for', async () => {
  const { added } = await runPublishedMp4box(join(webDir, 'vendor', 'mp4box-0.5.4.min.js'));
  for (const name of ['Log', 'DataStream', 'ISOFile', 'BoxParser', 'MP4Box']) assert.ok(added.includes(name), name);
});

test('a host page that declares the same names with let or const does not collide with the published mp4box', async () => {
  const out = await tempDir();
  await build({ webDir, outDir: out, version: VERSION });
  const code = await readFile(join(out, 'mp4box.min.js'), 'utf8');
  const context = vm.createContext({ console, setTimeout, clearTimeout, TextDecoder, TextEncoder });
  vm.runInContext('const Log = "host"; const DataStream = "host"; let ISOFile = "host";', context);
  assert.doesNotThrow(() => vm.runInContext(code, context));
  assert.equal(vm.runInContext('Log', context), 'host');
  assert.equal(typeof context.MP4Box.createFile, 'function');
});

// A moov whose only child claims to be larger than the moov: the parser logs it through Log.error and carries on.
function brokenMoov() {
  const box = (size, type, body = []) => {
    const bytes = new Uint8Array(size);
    new DataView(bytes.buffer).setUint32(0, size);
    for (let i = 0; i < 4; i++) bytes[4 + i] = type.charCodeAt(i);
    bytes.set(body, 8);
    return bytes;
  };
  const ftyp = box(24, 'ftyp', [0x69, 0x73, 0x6f, 0x6d, 0, 0, 2, 0, 0x69, 0x73, 0x6f, 0x6d]);
  const trak = box(16, 'trak');
  new DataView(trak.buffer).setUint32(0, 100);
  const moov = box(24, 'moov', trak);
  const all = new Uint8Array(ftyp.length + moov.length);
  all.set(ftyp);
  all.set(moov, ftyp.length);
  const buffer = all.buffer;
  buffer.fileStart = 0;
  return buffer;
}

async function consoleOutputOf(file) {
  const heard = [];
  const spy = { ...console, error: (...args) => heard.push(['error', ...args]), warn: (...args) => heard.push(['warn', ...args]) };
  const context = vm.createContext({ console: spy, window: {}, setTimeout, clearTimeout, TextDecoder, TextEncoder });
  vm.runInContext(await readFile(file, 'utf8'), context);
  const parser = context.MP4Box.createFile();
  parser.appendBuffer(brokenMoov());
  parser.flush();
  return heard;
}

test('the wrapper mutes the library log it owns', async () => {
  const raw = await consoleOutputOf(join(webDir, 'vendor', 'mp4box-0.5.4.min.js'));
  assert.ok(raw.some(([level, , , text]) => level === 'error' && /greater than its container size/.test(text)), 'the raw script does log this');

  const out = await tempDir();
  await build({ webDir, outDir: out, version: VERSION });
  assert.deepEqual(await consoleOutputOf(join(out, 'mp4box.min.js')), []);
});

test("integrity.json holds the SHA-384 of the three scripts, and the element carries mp4box's", async () => {
  const out = await tempDir();
  const result = await build({ webDir, outDir: out, version: VERSION });
  const integrity = JSON.parse(await readFile(join(out, 'integrity.json'), 'utf8'));
  assert.deepEqual(Object.keys(integrity), ['xrpl-file-element.js', 'mp4box.min.js', 'xrpl-file-reader.mjs']);
  for (const name of Object.keys(integrity)) {
    assert.equal(integrity[name], `sha384-${createHash('sha384').update(await readFile(join(out, name))).digest('base64')}`);
  }
  assert.deepEqual(result.integrity, integrity);

  const code = await readFile(join(out, 'xrpl-file-element.js'), 'utf8');
  assert.ok(!code.includes(PLACEHOLDER));
  assert.ok(code.includes('src/element/media/mp4box-loader.mjs'), 'the video view brings the loader into the bundle');
  assert.ok(code.includes(integrity['mp4box.min.js']));
  const vendoredHash = `sha384-${createHash('sha384').update(await readFile(join(webDir, 'vendor', 'mp4box-0.5.4.min.js'))).digest('base64')}`;
  assert.notEqual(integrity['mp4box.min.js'], vendoredHash, 'the hash covers the wrapped file a page actually loads');
});

test("the loader's placeholder gets mp4box's hash, and the running bundle sees it", async () => {
  const web = await copyOfWebAppending('src/element/media/mp4box-loader.mjs', '\nglobalThis.__hash = MP4BOX_INTEGRITY;\n');
  const out = await tempDir();
  const { integrity } = await build({ webDir: web, outDir: out, version: VERSION });
  const code = await readFile(join(out, 'xrpl-file-element.js'), 'utf8');
  assert.ok(!code.includes(PLACEHOLDER));
  assert.ok(code.includes(`MP4BOX_INTEGRITY = '${integrity['mp4box.min.js']}'`));

  const context = vm.createContext(browserStubs([]));
  vm.runInContext(code, context);
  assert.equal(context.__hash, integrity['mp4box.min.js']);
});

test('a placeholder that appears twice is refused, because a second copy would switch the check off', async () => {
  const web = await copyOfWebAppending('src/element/media/mp4box-loader.mjs', `\nexport const UNSET = '${PLACEHOLDER}';\n`);
  await assert.rejects(build({ webDir: web, outDir: await tempDir(), version: VERSION }), /__MP4BOX_INTEGRITY__.*2 times/);
});

test('import.meta.url becomes the script URL, and any other import.meta is refused', async () => {
  const web = await copyOfWebAppending('src/element/define.mjs', "\nglobalThis.__where = import.meta.url;\n");
  const out = await tempDir();
  await build({ webDir: web, outDir: out, version: VERSION });
  const code = await readFile(join(out, 'xrpl-file-element.js'), 'utf8');
  assert.ok(!code.includes('import.meta'));
  const context = vm.createContext(browserStubs([]));
  vm.runInContext(code, context);
  assert.equal(context.__where, 'https://cdn.example/1.0.0/xrpl-file-element.js');

  const other = await copyOfWebAppending('src/element/define.mjs', "\nglobalThis.__dir = import.meta.dirname;\n");
  await assert.rejects(build({ webDir: other, outDir: await tempDir(), version: VERSION }), /define\.mjs.*import\.meta\.dirname/);
});

test('two builds are byte for byte the same', async () => {
  const a = await tempDir();
  const b = await tempDir();
  await build({ webDir, outDir: a, version: VERSION });
  await build({ webDir, outDir: b, version: VERSION });
  for (const name of ['xrpl-file-element.js', 'xrpl-file-reader.mjs', 'integrity.json']) {
    assert.deepEqual(await readFile(join(a, name)), await readFile(join(b, name)));
  }
});

test('the same build into an existing version directory is accepted', async () => {
  const out = await tempDir();
  await build({ webDir, outDir: out, version: VERSION });
  await build({ webDir, outDir: out, version: VERSION });
});

test('a different build into an existing version directory is refused unless forced', async () => {
  const out = await tempDir();
  await build({ webDir, outDir: out, version: VERSION });
  await writeFile(join(out, 'xrpl-file-element.js'), 'stale');
  await assert.rejects(build({ webDir, outDir: out, version: VERSION }), /dist\/0\.0\.0-test exists with other content; raise the version in web\/package\.json, or pass --force/);
  assert.equal(await readFile(join(out, 'xrpl-file-element.js'), 'utf8'), 'stale', 'the refusal writes nothing');
  await build({ webDir, outDir: out, version: VERSION, force: true });
  assert.notEqual(await readFile(join(out, 'xrpl-file-element.js'), 'utf8'), 'stale');
});

test('a bare import fails the build naming the file', async () => {
  const web = await copyOfWebWith('src/element/define.mjs', "import x from 'lodash';\n");
  await assert.rejects(build({ webDir: web, outDir: await tempDir(), version: VERSION }), /define\.mjs.*lodash/);
  const named = await copyOfWebWith('src/element/define.mjs', "import { x } from 'lodash';\n");
  await assert.rejects(build({ webDir: named, outDir: await tempDir(), version: VERSION }), /define\.mjs.*lodash/);
});

test('a dynamic import fails the build naming the file', async () => {
  const web = await copyOfWebAppending('src/element/define.mjs', "\nconst later = () => import('./element.mjs');\n");
  await assert.rejects(build({ webDir: web, outDir: await tempDir(), version: VERSION }), /define\.mjs.*dynamic import/);
});

test('an import cycle fails the build naming the files', async () => {
  const web = await copyOfWebAppending('src/element/define.mjs', "\nimport { ping } from '../ping.mjs';\n");
  await writeFile(join(web, 'src', 'ping.mjs'), "import { pong } from './pong.mjs';\nexport const ping = () => pong;\n");
  await writeFile(join(web, 'src', 'pong.mjs'), "import { ping } from './ping.mjs';\nexport const pong = () => ping;\n");
  await assert.rejects(build({ webDir: web, outDir: await tempDir(), version: VERSION }), /cycle.*ping\.mjs.*pong\.mjs.*ping\.mjs/);
});

test('an import or export form the bundler does not know fails the build naming the file', async () => {
  for (const line of ["export default 1;", "export * from './hex.mjs';", "import './hex.mjs';", "import fallback from './hex.mjs';", "export { x };"]) {
    const web = await copyOfWebAppending('src/element/define.mjs', `\n${line}\n`);
    await assert.rejects(build({ webDir: web, outDir: await tempDir(), version: VERSION }), /define\.mjs/, line);
  }
});

test('a name the imported module does not export fails the build', async () => {
  const web = await copyOfWebAppending('src/element/define.mjs', "\nimport { nothingLikeThis } from '../hex.mjs';\n");
  await assert.rejects(build({ webDir: web, outDir: await tempDir(), version: VERSION }), /define\.mjs.*nothingLikeThis/);
});

test('every import and export form the sources use reaches the bundle correctly', async () => {
  const web = await copyOfWebAppending('src/element/define.mjs', "\nimport * as fixture from '../fixture/index.mjs';\nglobalThis.__fixture = fixture;\n");
  await mkdir(join(web, 'src', 'fixture'));
  await writeFile(join(web, 'src', 'fixture', 'leaf.mjs'),
    "export function twice(n) { return n * 2; }\n" +
    "export async function later() { return 1; }\n" +
    "export class Box { value = 7; }\n" +
    "export const LIMIT = 3;\n" +
    "export let counter = 0;\n" +
    "const hidden = 1;\n");
  await writeFile(join(web, 'src', 'fixture', 'index.mjs'),
    "import { twice as double, LIMIT } from './leaf.mjs';\n" +
    "import * as leaf from './leaf.mjs';\n" +
    "export { twice, Box as Container } from './leaf.mjs';\n" +
    "export * as everything from './leaf.mjs';\n" +
    "export const viaImport = double(LIMIT);\n" +
    "export const viaNamespace = leaf.LIMIT;\n");
  const out = await tempDir();
  await build({ webDir: web, outDir: out, version: VERSION });
  const context = vm.createContext(browserStubs([]));
  vm.runInContext(await readFile(join(out, 'xrpl-file-element.js'), 'utf8'), context);
  const fixture = context.__fixture;
  assert.equal(fixture.viaImport, 6);
  assert.equal(fixture.viaNamespace, 3);
  assert.equal(fixture.twice(4), 8);
  assert.equal(new fixture.Container().value, 7);
  assert.equal(fixture.everything.LIMIT, 3);
  assert.equal(typeof fixture.everything.later, 'function');
  assert.equal(fixture.everything.counter, 0);
  assert.equal('hidden' in fixture.everything, false);
});

test('the bundle names no host but the built-in nodes', async () => {
  const out = await tempDir();
  await build({ webDir, outDir: out, version: VERSION });
  const code = await readFile(join(out, 'xrpl-file-element.js'), 'utf8');
  const urls = code.match(/\b(?:https?|wss?):\/\/[^\s'"`)]+/g) ?? [];
  // MANIFEST_FORMAT is text a writer puts into a manifest for people to read; nothing fetches it.
  const allowed = new Set([...Object.values(ENDPOINTS).flat(), 'http://www.w3.org/2000/svg', MANIFEST_FORMAT]);
  // The node-attribute sentence names the scheme in words ("must be a wss:// address"), and in some languages
  // punctuation follows it directly ("wss://."): a scheme with no host after it names nothing to fetch.
  const bareScheme = /^wss?:\/\/(?!\w)/;
  assert.deepEqual(urls.filter((url) => !allowed.has(url) && !bareScheme.test(url)), []);
});

test('build emits xrpl-file-reader.mjs with its integrity and the version banner', async () => {
  const out = await tempDir();
  const { integrity } = await build({ webDir, outDir: out, version: '1.1.0' });
  const file = join(out, 'xrpl-file-reader.mjs');
  const bytes = await readFile(file);
  const code = bytes.toString('utf8');

  assert.ok(code.startsWith('/*! xrpl-file reader 1.1.0 | MIT */\n'));
  assert.equal(integrity['xrpl-file-reader.mjs'], `sha384-${createHash('sha384').update(bytes).digest('base64')}`);
  assert.deepEqual(JSON.parse(await readFile(join(out, 'integrity.json'), 'utf8')), integrity);
  assert.ok(bytes.every((byte) => byte < 0x80), 'plain ASCII, like the element');
  assert.doesNotMatch(code, /^\s*import\b/m, 'self-contained: nothing is imported');

  const module = await import(pathToFileURL(file).href);
  assert.deepEqual(Object.keys(module).sort(),
    ['ENDPOINTS', 'LedgerClient', 'XrplFileError', 'XrplFileReader', 'formatLink', 'parseLink']);
  assert.deepEqual(module.ENDPOINTS, ENDPOINTS);
  assert.equal(module.formatLink(module.parseLink('xrpl-file://TestNet/' + 'ab'.repeat(32))), 'xrpl-file://testnet/' + 'AB'.repeat(32));
  assert.ok(new module.XrplFileError('NotLoaded', 'x') instanceof Error);
  assert.equal(typeof module.XrplFileReader.prototype.read, 'function');
  assert.equal(typeof module.LedgerClient.prototype.close, 'function');

  const urls = code.match(/\b(?:https?|wss?):\/\/[^\s'"`)]+/g) ?? [];
  const allowed = new Set([...Object.values(ENDPOINTS).flat(), 'http://www.w3.org/2000/svg', MANIFEST_FORMAT]);
  assert.deepEqual(urls.filter((url) => !allowed.has(url)), [], 'only the built-in nodes are named');
});

test('the reader bundle is the shipped reader: both bundles carry the same module body', async () => {
  const out = await tempDir();
  await build({ webDir, outDir: out, version: VERSION });
  const reader = await readFile(join(out, 'xrpl-file-reader.mjs'), 'utf8');
  const element = await readFile(join(out, 'xrpl-file-element.js'), 'utf8');
  const body = (code) => /__define\('src\/reader\.mjs'[\s\S]*?\n\}\);\n/.exec(code)[0];
  assert.equal(body(reader), body(element));
});
