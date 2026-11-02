// Assembles the deployable site into site/dist:
//   site/src/**                  -> /            (pages, scripts, styles, fonts, _redirects, _headers)
//                                   with <!--@chrome:head|header|footer--> markers in a page replaced by
//                                   the shared markup of chrome-markup.mjs, and <!--@element-snippet--> replaced
//                                   by the embed snippets (jsDelivr and this site) with the element's integrity
//                                   from integrity.json
//   web/dist/<version>/*         -> /element/<version>/   (the element and the reader bundle)
//   site/data/price.json         -> /data/price.json
// The site loads the element and the reader from that one copy; nothing else carries reader code.
//
//   node site/build.mjs            (run `cd web && node build.mjs` first)
import { cp, mkdir, readdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderChrome } from './chrome-markup.mjs';
import { ACTIVE_LANGS, DEFAULT_LANG, LANGS, htmlLang, langFromTag } from './src/js/languages.mjs';
import { ELEMENT_FILE, alternativeSnippetHtml, snippetHtml } from './snippet.mjs';
import { findHtmlRewriteTargets, findShadowed, parseRedirects } from './redirects.mjs';

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

/** The URL a page is served at: index.html is its folder, other pages lose the .html (Pages' pretty URLs). */
export function pagePathOf(relativeFile) {
  const path = `/${relativeFile.split(sep).join('/')}`;
  if (path === '/index.html') return '/';
  if (path.endsWith('/index.html')) return path.slice(0, -'index.html'.length);
  return path.replace(/\.html$/, '');
}

const CHROME_MARKER = /<!--@chrome:([a-z-]+)-->/g;

/**
 * Every active language has its home page, and no language folder is built for a language that is not
 * active: such a page would ship without a menu entry or an alternate, and the language list says it
 * is not ready.
 */
async function checkLanguagePages(outDir) {
  for (const lang of LANGS) {
    if (lang === DEFAULT_LANG) continue;
    const active = ACTIVE_LANGS.includes(lang);
    const home = join(outDir, lang, 'index.html');
    if (active && !(await stat(home).then((s) => s.isFile(), () => false))) {
      throw new Error(`"${lang}" is in ACTIVE_LANGS but site/src/${lang}/index.html does not exist`);
    }
    if (!active && (await isDirectory(join(outDir, lang)))) {
      throw new Error(`site/src/${lang}/ exists but "${lang}" is not in ACTIVE_LANGS (site/src/js/languages.mjs); add it there or remove the folder`);
    }
  }
}

async function inlineChrome(outDir) {
  for (const file of await listFiles(outDir)) {
    const relative_ = relative(outDir, file);
    // The element build is published as is; a marker-looking string in it is not ours to rewrite.
    if (!file.endsWith('.html') || relative_.split(sep)[0] === 'element') continue;
    const html = await readFile(file, 'utf8');
    if (!CHROME_MARKER.test(html)) continue;
    CHROME_MARKER.lastIndex = 0;
    const segments = relative_.split(sep);
    const name = segments.join('/');
    const tag = /<html\b[^>]*\blang="([^"]*)"/i.exec(html)?.[1];
    const lang = langFromTag(tag);
    if (lang === null || htmlLang(lang) !== tag) {
      const tags = ACTIVE_LANGS.map((code) => `lang="${htmlLang(code)}"`).join(', ');
      throw new Error(`${name} uses a chrome marker but its <html> has none of ${tags}`);
    }
    if (!ACTIVE_LANGS.includes(lang)) {
      throw new Error(`${name} is written in "${lang}", which is not in ACTIVE_LANGS (site/src/js/languages.mjs)`);
    }
    const langFromStorage = /<html\b[^>]*\bdata-lang-source="storage"/i.test(html);
    // A page in a language folder must say it is in that language. A copied page that kept the old lang
    // is the usual mistake, and it would ship with the wrong chrome and the wrong alternates.
    const expected = LANGS.includes(segments[0]) && segments.length > 1 ? segments[0] : segments.length === 1 ? DEFAULT_LANG : null;
    if (!langFromStorage && expected !== null && lang !== expected) {
      throw new Error(`${name} is in ${expected === DEFAULT_LANG ? 'the site root' : `the ${expected}/ folder`} but its <html> says lang="${tag}"`);
    }
    const page = { lang, pagePath: pagePathOf(relative_), langFromStorage };
    let rendered;
    try {
      rendered = html.replace(CHROME_MARKER, (_, part) => renderChrome(part, page));
    } catch (error) {
      throw new Error(`${name}: ${error.message}`);
    }
    await writeFile(file, rendered);
  }
}

const SNIPPET_MARKER = '<!--@element-snippet-->';
const ALT_SNIPPET_MARKER = '<!--@element-snippet-alt-->';

/**
 * Fills the embed snippets of a page: both markers get the escaped snippet with the element's integrity
 * from integrity.json.
 */
async function inlineSnippet(outDir, elementDir, elementVersion) {
  let integrity = null;
  for (const file of await listFiles(outDir)) {
    const relative_ = relative(outDir, file);
    if (!file.endsWith('.html') || relative_.split(sep)[0] === 'element') continue;
    const html = await readFile(file, 'utf8');
    if (!html.includes(SNIPPET_MARKER) && !html.includes(ALT_SNIPPET_MARKER)) continue;
    if (integrity === null) {
      const values = JSON.parse(await readFile(join(elementDir, 'integrity.json'), 'utf8'));
      integrity = values[ELEMENT_FILE];
      if (typeof integrity !== 'string' || !integrity.startsWith('sha384-')) {
        throw new Error(`integrity.json of element ${elementVersion} has no sha384 value for ${ELEMENT_FILE}`);
      }
    }
    const options = { version: elementVersion, integrity };
    await writeFile(
      file,
      html
        .replaceAll(SNIPPET_MARKER, () => snippetHtml(options))
        .replaceAll(ALT_SNIPPET_MARKER, () => alternativeSnippetHtml(options))
    );
  }
}

const TEXT_FILE = /\.(?:html|mjs|js|css)$/;
const ELEMENT_PATH = /\/element\/([0-9A-Za-z][0-9A-Za-z.+-]*)\//g;

/** The versions named by `/element/<version>/` paths in a text. */
export function elementVersionsIn(text) {
  return [...text.matchAll(ELEMENT_PATH)].map((match) => match[1]);
}

/**
 * The element version is spelled out in several sources (the file page's script tag, the cloud and
 * the reader specifier) because a page cannot import a build constant. A bump that misses one would
 * ship a page loading a version the site no longer serves, so the build compares them all.
 */
export async function checkElementVersion(root, elementVersion) {
  const stale = [];
  for (const file of await listFiles(root)) {
    const name = relative(root, file).split(sep).join('/');
    if (!TEXT_FILE.test(name) || name.startsWith('element/')) continue;
    for (const version of new Set(elementVersionsIn(await readFile(file, 'utf8')))) {
      if (version !== elementVersion) stale.push(`${name} names /element/${version}/`);
    }
  }
  if (stale.length > 0) {
    throw new Error(`these sources do not name the element version ${elementVersion} the build serves: ${stale.join(', ')}`);
  }
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
  const rules = parseRedirects(text);
  const htmlTargets = findHtmlRewriteTargets(rules);
  if (htmlTargets.length > 0) {
    throw new Error(
      `_redirects has 200 rules that point at an .html or /index target (${htmlTargets.map((r) => r.destination).join(', ')}). ` +
        'Pages redirects such a rewrite to the pretty URL; use the extension-less path, e.g. /file.',
    );
  }
  const files = (await listFiles(outDir))
    .map((file) => relative(outDir, file).split(sep).join('/'))
    .filter((file) => !HOST_FILES.has(file));
  const shadowed = findShadowed(files, rules);
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
    await checkLanguagePages(outDir);
    await checkElementVersion(outDir, elementVersion);
    await inlineChrome(outDir);
    await inlineSnippet(outDir, elementDir, elementVersion);
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
