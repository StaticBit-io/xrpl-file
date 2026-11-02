// Assembles the npm package of the <xrpl-file> element into web/npm/<version>/: the files of
// web/dist/<version>/ copied byte for byte (so the `integrity` values hold on jsDelivr, which serves
// npm packages), plus package.json, a README.md taken from the repository README and the LICENSE.
//
//   node web/build.mjs && node web/pack.mjs
//
// This only writes a directory. Publishing it (`npm publish` there) is irreversible for a version
// and is the owner's step; nothing here talks to a registry.
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const DIST_FILES = ['xrpl-file-element.js', 'mp4box.min.js', 'mp4box-LICENSE.txt', 'xrpl-file-reader.mjs', 'integrity.json'];
const ELEMENT_FILE = 'xrpl-file-element.js';
const REPOSITORY = 'https://github.com/StaticBit-io/xrpl-file';
const DESCRIPTION = 'The <xrpl-file> custom element: shows a file stored in XRP Ledger memos, verified in the browser, from an xrpl-file:// link.';
const SECTION_HEADING = '## Embed a file';
const VERSION_PATTERN = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/;
const PACKAGE_NAME_PATTERN = /^(?:@[a-z0-9-~][a-z0-9-._~]*\/)?[a-z0-9-~][a-z0-9-._~]*$/;

/** The package name lives in the site's config, the one place the owner edits it. */
async function elementPackageName(webDir) {
  const config = await import(pathToFileURL(join(webDir, '..', 'site', 'src', 'js', 'config.mjs')).href);
  return config.ELEMENT_PACKAGE;
}

function replaceOnce(text, pattern, replacement, what) {
  if (!pattern.test(text)) throw new Error(`README.md: the "Embed a file" section no longer has ${what}; update web/pack.mjs together with it`);
  return text.replace(pattern, () => replacement);
}

function snippetOf({ name, version, integrity }) {
  return [
    '```html',
    `<script src="https://cdn.jsdelivr.net/npm/${name}@${version}/${ELEMENT_FILE}"`,
    `        integrity="${integrity}"`,
    '        crossorigin="anonymous" defer></script>',
    '<xrpl-file src="xrpl-file://testnet/…"></xrpl-file>',
    '```',
  ].join('\n');
}

/** The repository README's "Embed a file" section, reworded for a reader who installs nothing and builds nothing. */
function packageReadme({ name, version, integrity, repoReadme }) {
  const text = repoReadme.replace(/\r\n/g, '\n');
  const start = text.indexOf(`\n${SECTION_HEADING}\n`);
  if (start < 0) throw new Error(`README.md has no "${SECTION_HEADING}" section to put into the package`);
  const next = text.indexOf('\n## ', start + SECTION_HEADING.length + 2);
  let section = text.slice(start + 1, next < 0 ? text.length : next).trimEnd();

  section = replaceOnce(section, /Build it, put the files of one version on a host, and add two lines:/, 'Add two lines to the page:', 'the sentence that introduces the snippet');
  section = replaceOnce(section, /```html\n[\s\S]*?\n```/, snippetOf({ name, version, integrity }), 'the html snippet');
  section = replaceOnce(section, /```bash\ncd web && npm run build\n```\n\n/, '', 'the build command');
  section = replaceOnce(section, /The build writes `web\/dist\/<version>\/`:/, 'The package holds:', 'the sentence that lists the built files');
  // The paragraph on the repository's demo pages names paths that do not exist in the package, and a
  // published version's README cannot be corrected afterwards.
  section = replaceOnce(section, /\n\n`web\/demo` has pages[^\n]*(?:\n[^\n]+)*/, '', 'the paragraph about web/demo');

  return `# ${name}\n\n${section}\n\nSource and licence: ${REPOSITORY}\n`;
}

/**
 * @param {object} options
 * @param {string} options.webDir the `web/` directory
 * @param {string} [options.distRoot] where `<version>/` of the build lives; default `web/dist`
 * @param {string} [options.outRoot] where `<version>/` of the package goes; default `web/npm`
 * @param {string} [options.repoRoot] holds README.md and LICENSE; default the parent of `web/`
 * @param {string} [options.packageName] default: ELEMENT_PACKAGE from site/src/js/config.mjs
 * @returns {Promise<{ outDir: string, name: string, version: string, files: string[] }>}
 */
export async function pack({ webDir, distRoot = join(webDir, 'dist'), outRoot = join(webDir, 'npm'), repoRoot = join(webDir, '..'), packageName }) {
  const { version } = JSON.parse(await readFile(join(webDir, 'package.json'), 'utf8'));
  if (!VERSION_PATTERN.test(version)) throw new Error(`'${version}' is not a version npm accepts`);

  const name = packageName ?? (await elementPackageName(webDir));
  if (typeof name !== 'string' || !PACKAGE_NAME_PATTERN.test(name)) throw new Error(`'${name}' is not a package name npm accepts`);

  const distDir = join(distRoot, version);
  const copies = new Map();
  for (const file of DIST_FILES) {
    try {
      copies.set(file, await readFile(join(distDir, file)));
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
      throw new Error(`${join(distDir, file)} is missing; run \`node web/build.mjs\` to build ${version} first`);
    }
  }

  const integrity = JSON.parse(copies.get('integrity.json').toString('utf8'))[ELEMENT_FILE];
  if (!integrity) throw new Error(`integrity.json of ${version} has no value for ${ELEMENT_FILE}; rebuild with \`node web/build.mjs --force\``);

  const readme = packageReadme({ name, version, integrity, repoReadme: await readFile(join(repoRoot, 'README.md'), 'utf8') });
  const license = await readFile(join(repoRoot, 'LICENSE'));

  const shipped = [...DIST_FILES, 'README.md', 'LICENSE'];
  const manifest = {
    name,
    version,
    description: DESCRIPTION,
    license: 'MIT',
    repository: { type: 'git', url: REPOSITORY },
    homepage: REPOSITORY,
    keywords: ['xrpl', 'xrp-ledger', 'web-component', 'custom-element'],
    files: shipped,
    // A scoped package is private to its scope unless it is published as public.
    ...(name.startsWith('@') ? { publishConfig: { access: 'public' } } : {}),
  };

  const outDir = join(outRoot, version);
  await rm(outDir, { recursive: true, force: true });
  await mkdir(outDir, { recursive: true });
  for (const [file, bytes] of copies) await writeFile(join(outDir, file), bytes);
  await writeFile(join(outDir, 'README.md'), readme);
  await writeFile(join(outDir, 'LICENSE'), license);
  await writeFile(join(outDir, 'package.json'), `${JSON.stringify(manifest, null, 2)}\n`);

  return { outDir, name, version, files: [...shipped, 'package.json'] };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const { outDir, name, version, files } = await pack({ webDir: dirname(fileURLToPath(import.meta.url)) });
    console.log(`packed ${name}@${version} into ${outDir}`);
    for (const file of files) console.log(file);
  } catch (error) {
    console.error(`pack failed: ${error.message}`);
    process.exitCode = 1;
  }
}
