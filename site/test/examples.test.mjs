import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { EXAMPLES } from '../src/js/examples.mjs';
import { EXAMPLE_FILE_LINK, EXAMPLE_STRINGS } from '../src/js/examples-strings.mjs';
import { HERO_LINK } from '../src/js/config.mjs';
import { ACTIVE_LANGS } from '../src/js/languages.mjs';
import { renderExamples } from '../examples-markup.mjs';
import { build, ELEMENT_VERSION } from '../build.mjs';
import { builtSite } from './support/built-site.mjs';

const hrefOf = (link) => link.replace(/^xrpl-file:\/\//, '/');

test('everyActiveLanguageHasEveryExample', () => {
  const keys = EXAMPLES.map((example) => example.key);
  for (const lang of ACTIVE_LANGS) {
    assert.ok(EXAMPLE_STRINGS[lang], `${lang} has no strings`);
    assert.deepEqual(Object.keys(EXAMPLE_STRINGS[lang]).sort(), [...keys].sort(), `${lang} keys`);
    assert.ok(EXAMPLE_FILE_LINK[lang], `${lang} has no file page link text`);
    for (const key of keys) {
      assert.ok(EXAMPLE_STRINGS[lang][key].title && EXAMPLE_STRINGS[lang][key].line, `${lang}/${key}`);
    }
  }
});

test('anExampleMissingFromALanguageFails', () => {
  assert.throws(() => renderExamples('en', { strings: { 'stand-in-clip': { title: 'a', line: 'b' } } }), /no strings for example/);
});

test('cardsAreStaticHtml', () => {
  const html = renderExamples('en');
  for (const example of EXAMPLES) {
    assert.ok(html.includes(`src="${example.link}"`), example.key);
    assert.ok(html.includes(`<h2>${EXAMPLE_STRINGS.en[example.key].title}</h2>`), example.key);
    assert.ok(html.includes(`href="${hrefOf(example.link)}"`), example.key);
  }
  assert.ok(!html.includes('caption="off"'));
  assert.equal((html.match(/<figure /g) ?? []).length, EXAMPLES.length);
});

test('aspectRatioIsReservedForPicturesAndVideo', () => {
  for (const example of EXAMPLES) {
    const html = renderExamples('en', { examples: [example] });
    if (['drawing', 'video', 'logo'].includes(example.kind)) {
      assert.ok(html.includes(`style="aspect-ratio: ${example.width} / ${example.height}"`), example.key);
    } else {
      assert.ok(!html.includes('aspect-ratio'), example.key);
    }
  }
});

test('textIsEscaped', () => {
  const html = renderExamples('en', {
    examples: [EXAMPLES[0]],
    strings: { [EXAMPLES[0].key]: { title: 'A <b>"x"</b>', line: 'one & <two>' } },
    linkText: '<go>',
  });
  assert.ok(html.includes('A &lt;b&gt;&quot;x&quot;&lt;/b&gt;'));
  assert.ok(html.includes('one &amp; &lt;two&gt;'));
  assert.ok(html.includes('&lt;go&gt;'));
  assert.ok(!html.includes('<b>'));
});

test('theLogoEntryMatchesTheHero', () => {
  const logo = EXAMPLES.find((example) => example.link === HERO_LINK);
  assert.ok(logo);
  assert.equal(logo.kind, 'logo');
  assert.equal(logo.bytes, 122958);
  assert.equal(logo.width, 1422);
  assert.equal(logo.height, 1231);
});

test('theListHasTheKindsInPageOrder', () => {
  const order = ['drawing', 'video', 'svg', 'text', 'logo'];
  const ranks = EXAMPLES.map((example) => order.indexOf(example.kind));
  assert.ok(ranks.every((rank) => rank >= 0));
  assert.deepEqual(ranks, [...ranks].sort((a, b) => a - b));
});

test('renderedCardsHaveNoPlaceholderLeftOver', () => {
  for (const lang of ACTIVE_LANGS) {
    const html = renderExamples(lang);
    assert.ok(!html.includes('{'), lang);
    assert.ok(!html.includes('undefined'), lang);
  }
});

async function pageWithMarker(lang) {
  const root = await mkdtemp(join(tmpdir(), 'xf-site-examples-'));
  const siteDir = join(root, 'site');
  const webDistDir = join(root, 'web-dist');
  await mkdir(join(siteDir, 'src'), { recursive: true });
  await mkdir(join(siteDir, 'data'), { recursive: true });
  await mkdir(join(webDistDir, ELEMENT_VERSION), { recursive: true });
  await writeFile(join(siteDir, 'data', 'price.json'), '{}');
  await writeFile(join(webDistDir, ELEMENT_VERSION, 'xrpl-file-element.js'), '// element');
  await writeFile(join(webDistDir, ELEMENT_VERSION, 'integrity.json'), JSON.stringify({ 'xrpl-file-element.js': 'sha384-X' }));
  await writeFile(join(siteDir, 'src', 'index.html'), '<!doctype html>');
  for (const code of ACTIVE_LANGS.filter((l) => l !== 'en')) {
    await mkdir(join(siteDir, 'src', code), { recursive: true });
    await writeFile(join(siteDir, 'src', code, 'index.html'), '<!doctype html>');
  }
  await writeFile(join(siteDir, 'src', 'examples.html'), `<!doctype html><html lang="${lang}"><body><!--@examples--></body></html>`);
  return { root, siteDir, webDistDir, outDir: join(root, 'dist') };
}

test('buildFillsTheMarker', async () => {
  const f = await pageWithMarker('en');
  try {
    await build(f);
    const page = await readFile(join(f.outDir, 'examples.html'), 'utf8');
    assert.ok(!page.includes('<!--@examples-->'));
    for (const example of EXAMPLES) assert.ok(page.includes(example.link), example.key);
  } finally {
    await rm(f.root, { recursive: true, force: true });
  }
});

test('buildRefusesAPageInALanguageWithoutStrings', async () => {
  const f = await pageWithMarker('fr');
  try {
    await assert.rejects(build(f), /examples marker/);
  } finally {
    await rm(f.root, { recursive: true, force: true });
  }
});

test('theRealBuildStillSucceeds', async () => {
  const { outDir } = await builtSite();
  assert.ok(outDir);
});
