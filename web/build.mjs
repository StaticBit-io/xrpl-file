// Builds the <xrpl-file> element into web/dist/<version>/: one classic script, the vendored mp4box
// beside it, and the SHA-384 hashes a page puts in its `integrity` attributes.
//
// The bundler is a module registry, not a general bundler. It handles exactly the import/export
// forms web/src uses and fails the build on anything else, so a new form is a decision, not a
// silent miscompile. Limits worth knowing: every `import { a }` is read once when the importing
// module starts (no live bindings), and `export let` exposes a getter, not a binding.
import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, join, posix } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ENTRY = 'src/element/define.mjs';
const BUNDLE_NAME = 'xrpl-file-element.js';
const MP4BOX_NAME = 'mp4box.min.js';
const LICENSE_NAME = 'mp4box-LICENSE.txt';
const MP4BOX_SOURCE = 'vendor/mp4box-0.5.4.min.js';
const LICENSE_SOURCE = 'vendor/mp4box-LICENSE.txt';
const PLACEHOLDER = '__MP4BOX_INTEGRITY__';
const VERSION_PATTERN = /^[0-9A-Za-z][0-9A-Za-z.+-]*$/;

const NAME = '[A-Za-z_$][\\w$]*';
// The quote is always capture group 2: every form opens with one capture (the names, or the namespace).
const SPEC = String.raw`(['"])([^'"\n]+)\2`;
const END = '[ \\t]*;?';
const NAMED_IMPORT = new RegExp(`^import\\s*\\{([^}]*)\\}\\s*from\\s*${SPEC}${END}`, 'gm');
const NAMESPACE_IMPORT = new RegExp(`^import\\s*\\*\\s*as\\s+(${NAME})\\s+from\\s*${SPEC}${END}`, 'gm');
const NAMED_REEXPORT = new RegExp(`^export\\s*\\{([^}]*)\\}\\s*from\\s*${SPEC}${END}`, 'gm');
const NAMESPACE_REEXPORT = new RegExp(`^export\\s*\\*\\s*as\\s+(${NAME})\\s+from\\s*${SPEC}${END}`, 'gm');
const DECLARATION = new RegExp(`^export\\s+(async\\s+function\\s*\\*?|function\\s*\\*?|class|const|let)\\s+(${NAME})`, 'gm');
const LEFTOVER = /^[ \t]*(?:import|export)\b.*$/m;

const sri = (bytes) => `sha384-${createHash('sha384').update(bytes).digest('base64')}`;

function parseSpecifiers(path, list) {
  return list.split(',').map((part) => part.trim()).filter(Boolean).map((part) => {
    const match = new RegExp(`^(${NAME})(?:\\s+as\\s+(${NAME}))?$`).exec(part);
    if (!match) throw new Error(`${path}: cannot bundle the import/export specifier '${part}'`);
    return { imported: match[1], local: match[2] ?? match[1] };
  });
}

function resolveSpecifier(path, spec) {
  if (!spec.startsWith('./') && !spec.startsWith('../')) {
    throw new Error(`${path}: cannot bundle the bare import '${spec}'; the build follows relative imports only`);
  }

  const resolved = posix.join(posix.dirname(path), spec);
  if (resolved.startsWith('../')) throw new Error(`${path}: the import '${spec}' leaves web/`);
  return resolved;
}

// Turns one module's source into a registry body, and reports what it imports and exports.
function transform(path, rawSource) {
  let source = rawSource.replace(/^﻿/, '').replace(/\r\n/g, '\n');

  if (/\bimport\s*\(/.test(source)) throw new Error(`${path}: a dynamic import() cannot be bundled`);
  source = source.replace(/\bimport\.meta\.url\b/g, '__scriptUrl');
  const meta = /\bimport\.meta(\.\w+)?/.exec(source);
  if (meta) throw new Error(`${path}: ${meta[0]} cannot be bundled; only import.meta.url is replaced`);

  const deps = [];
  const imports = [];
  const entries = [];
  let reexports = 0;

  const use = (spec) => {
    const target = resolveSpecifier(path, spec);
    if (!deps.includes(target)) deps.push(target);
    return target;
  };
  const reexportName = () => `__re${reexports++}`;

  source = source
    .replace(NAMED_IMPORT, (_, list, quote, spec) => {
      const target = use(spec);
      const names = parseSpecifiers(path, list);
      imports.push({ target, names: names.map((n) => n.imported) });
      const pattern = names.map((n) => (n.imported === n.local ? n.local : `${n.imported}: ${n.local}`)).join(', ');
      return `const { ${pattern} } = __import('${target}');`;
    })
    .replace(NAMESPACE_IMPORT, (_, name, quote, spec) => `const ${name} = __import('${use(spec)}');`)
    .replace(NAMED_REEXPORT, (_, list, quote, spec) => {
      const target = use(spec);
      const names = parseSpecifiers(path, list);
      imports.push({ target, names: names.map((n) => n.imported) });
      const holder = reexportName();
      for (const n of names) entries.push({ code: `${n.local}: ${holder}.${n.imported}`, name: n.local });
      return `const ${holder} = __import('${target}');`;
    })
    .replace(NAMESPACE_REEXPORT, (_, name, quote, spec) => {
      const holder = reexportName();
      entries.push({ code: `${name}: ${holder}`, name });
      return `const ${holder} = __import('${use(spec)}');`;
    })
    .replace(DECLARATION, (_, kind, name) => {
      entries.push({ code: kind === 'let' ? `get ${name}() { return ${name}; }` : name, name });
      return `${kind} ${name}`;
    });

  const left = LEFTOVER.exec(source);
  if (left) throw new Error(`${path}: cannot bundle this import/export form: ${left[0].trim()}`);

  const names = new Set();
  for (const { name } of entries) {
    if (names.has(name)) throw new Error(`${path}: '${name}' is exported twice`);
    names.add(name);
  }

  return { source, deps, imports, entries, names };
}

// Dependencies first, in source order, so the output order is a function of the sources alone.
async function collect(webDir, entry) {
  const modules = new Map();
  const order = [];

  async function visit(path, chain) {
    if (chain.includes(path)) throw new Error(`import cycle: ${[...chain, path].join(' -> ')}`);
    if (modules.has(path)) return;

    let text;
    try {
      text = await readFile(join(webDir, path), 'utf8');
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
      throw new Error(`${chain.at(-1) ?? 'the build'}: cannot find the module '${path}'`);
    }

    const module = transform(path, text);
    modules.set(path, module);
    for (const dep of module.deps) await visit(dep, [...chain, path]);
    order.push(path);
  }

  await visit(entry, []);

  for (const path of order) {
    for (const { target, names } of modules.get(path).imports) {
      for (const name of names) {
        if (!modules.get(target).names.has(name)) throw new Error(`${path}: '${name}' is not exported by ${target}`);
      }
    }
  }

  return order.map((path) => ({ path, module: modules.get(path) }));
}

function renderModule(path, { source, entries }) {
  const exported = entries.length > 0 ? `__export({ ${entries.map((e) => e.code).join(', ')} });\n` : '';
  return `__define('${path}', function (__import, __export) {\n${source}${source.endsWith('\n') ? '' : '\n'}${exported}});\n`;
}

// The page's encoding must not matter to a script served from elsewhere.
const asciiOnly = (text) => text.replace(/[^\x00-\x7f]/g, (c) => `\\u${c.charCodeAt(0).toString(16).padStart(4, '0')}`);

function assemble(version, modules, mp4boxIntegrity) {
  const parts = [
    `/*! xrpl-file element ${version} | MIT */\n`,
    `(function () {\n'use strict';\n`,
    `const __scriptUrl = document.currentScript && document.currentScript.src;\n`,
    `const __factories = new Map();\n`,
    `const __loaded = new Map();\n`,
    `function __define(path, factory) { __factories.set(path, factory); }\n`,
    `function __import(path) {\n`,
    `  let exports = __loaded.get(path);\n`,
    `  if (exports) return exports;\n`,
    `  exports = {};\n`,
    `  __loaded.set(path, exports);\n`,
    `  __factories.get(path)(__import, (names) => Object.defineProperties(exports, Object.getOwnPropertyDescriptors(names)));\n`,
    `  return exports;\n`,
    `}\n`,
    ...modules.map(({ path, module }) => renderModule(path, module)),
    `__import('${ENTRY}');\n`,
    `})();\n`,
  ];

  const text = parts.join('');
  const copies = text.split(PLACEHOLDER).length - 1;
  // A second copy (the loader compares against the placeholder to tell "unset") would be replaced
  // too, and the comparison would then call the real hash unset: the integrity check would be off.
  if (copies > 1) throw new Error(`${PLACEHOLDER} appears ${copies} times in the bundle; the build writes it into exactly one place`);

  return asciiOnly(text.split(PLACEHOLDER).join(mp4boxIntegrity));
}

async function readIfExists(file) {
  try {
    return await readFile(file);
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    throw error;
  }
}

/**
 * Builds the element into `outDir`.
 *
 * @param {object} options
 * @param {string} options.webDir the `web/` directory
 * @param {string} options.outDir where `xrpl-file-element.js`, `mp4box.min.js`, `mp4box-LICENSE.txt` and `integrity.json` go
 * @param {string} options.version the package version, written into the banner and the directory name
 * @param {boolean} [options.force] overwrite files that differ; for a version that has not been released
 * @returns {Promise<{ integrity: Record<string, string> }>}
 */
export async function build({ webDir, outDir, version, force = false }) {
  if (!VERSION_PATTERN.test(version)) throw new Error(`'${version}' is not a version the build can put in a path`);

  const mp4box = await readFile(join(webDir, MP4BOX_SOURCE));
  const license = await readFile(join(webDir, LICENSE_SOURCE));
  const mp4boxIntegrity = sri(mp4box);

  const modules = await collect(webDir, ENTRY);
  const bundle = Buffer.from(assemble(version, modules, mp4boxIntegrity), 'utf8');
  const integrity = { [BUNDLE_NAME]: sri(bundle), [MP4BOX_NAME]: mp4boxIntegrity };

  const files = new Map([
    [BUNDLE_NAME, bundle],
    [MP4BOX_NAME, mp4box],
    [LICENSE_NAME, license],
    ['integrity.json', Buffer.from(`${JSON.stringify(integrity, null, 2)}\n`, 'utf8')],
  ]);

  if (!force) {
    for (const [name, bytes] of files) {
      const existing = await readIfExists(join(outDir, name));
      if (existing && !existing.equals(bytes)) {
        throw new Error(`dist/${version} exists with other content; raise the version in web/package.json, or pass --force for a version not yet released`);
      }
    }
  }

  await mkdir(outDir, { recursive: true });
  for (const [name, bytes] of files) await writeFile(join(outDir, name), bytes);

  return { integrity };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const webDir = dirname(fileURLToPath(import.meta.url));
  const { version } = JSON.parse(await readFile(join(webDir, 'package.json'), 'utf8'));
  const outDir = join(webDir, 'dist', version);
  try {
    const { integrity } = await build({ webDir, outDir, version, force: process.argv.includes('--force') });
    console.log(`built ${outDir}`);
    for (const [name, hash] of Object.entries(integrity)) console.log(`${name}  ${hash}`);
  } catch (error) {
    console.error(`build failed: ${error.message}`);
    process.exitCode = 1;
  }
}
