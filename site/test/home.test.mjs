import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { HERO_ASPECT, HERO_LINK, PUBLISHER_URL } from '../src/js/config.mjs';

import { ACTIVE_LANGS, htmlLang } from '../src/js/languages.mjs';

const homeUrl = (lang) => new URL(`../src/${lang === 'en' ? '' : `${lang}/`}index.html`, import.meta.url);
const pages = Object.fromEntries(await Promise.all(ACTIVE_LANGS.map(async (lang) => [lang, await readFile(homeUrl(lang), 'utf8')])));

const ids = (html) => [...html.matchAll(/\bid="([^"]+)"/g)].map((match) => match[1]).sort();

test('config holds the hero file (the transparent StaticBit logo), its aspect and an unset publisher URL', () => {
  assert.equal(HERO_LINK, 'xrpl-file://testnet/2AEB485BB0FAECA2DF4B60F196D5199042288FEB124ED8D95F4313CF06200DB4');
  assert.equal(HERO_ASPECT, 1422 / 1231);
  assert.equal(PUBLISHER_URL, null);
});

test('the hero markup has what the point cloud draws into, in both languages', () => {
  for (const html of Object.values(pages)) {
    for (const id of ['hero-cloud', 'co-file', 'co-link', 'hero-card', 'hero-caption', 'hero-note']) {
      assert.ok(html.includes(`id="${id}"`), `#${id}`);
    }
    assert.match(html, /<canvas id="hero-cloud" class="cloud" role="img" aria-label="[^"]{20,}"><\/canvas>/);
    assert.match(html, /<div id="hero-card" class="hero-card" hidden><\/div>/, 'the card is hidden until the read is verified');
    assert.ok(html.includes('aria-hidden="true"></div>'), 'callouts are decoration for the label and the caption');
    assert.ok(!html.includes('hero-mosaic'));
  }
});

test('the home pages carry the same structure, each in its own language', () => {
  for (const lang of ACTIVE_LANGS) assert.match(pages[lang], new RegExp(`<html lang="${htmlLang(lang)}">`), lang);
  for (const lang of ACTIVE_LANGS) assert.deepEqual(ids(pages[lang]), ids(pages.en), `${lang} has the ids of the English page`);
  for (const html of Object.values(pages)) {
    assert.match(html, /<meta name="description" content="[^"]{40,}">/);
    assert.match(html, /<main id="main">/);
    for (const marker of ['head', 'header', 'footer']) assert.ok(html.includes(`<!--@chrome:${marker}-->`));
    assert.ok(html.includes('<!--@element-snippet-->') && html.includes('<!--@element-snippet-alt-->'));
  }
});

test('the sections come in the spec order', () => {
  for (const html of Object.values(pages)) {
    const order = ['hero-title', 'problem-title', 'how-title', 'price-title', 'permanent-title', 'developers-title'];
    const positions = order.map((id) => html.indexOf(`id="${id}"`));
    assert.ok(positions.every((position) => position > 0), 'every section heading is present');
    assert.deepEqual([...positions].sort((a, b) => a - b), positions);
  }
});

test('the publisher button ships disabled and marked "soon", in the language of the page', () => {
  assert.match(pages.en, /<button id="publisher"[^>]*\bdisabled\b[^>]*>.*soon/);
  assert.match(pages.ru, /<button id="publisher"[^>]*\bdisabled\b[^>]*>.*скоро/);
  for (const html of Object.values(pages)) {
    assert.match(html, /<button id="publisher"[^>]*\bdisabled\b[^>]*>.*<span class="tag">[^<]+<\/span>/);
  }
});

test('the drawings are coloured by classes only, never by literal colours', () => {
  for (const html of Object.values(pages)) {
    const svgs = html.match(/<svg[\s\S]*?<\/svg>/g) ?? [];
    assert.equal(svgs.length, 4);
    for (const svg of svgs) {
      assert.ok(svg.includes('role="img"') && svg.includes('<title'), 'each drawing has a text equivalent');
      assert.ok(!/#[0-9a-f]{3,8}\b|rgb\(|oklch\(|style=/i.test(svg));
    }
  }
});

test('no long dashes in the copy', () => {
  for (const html of Object.values(pages)) assert.ok(!html.includes('—'));
});

test('the check-a-link error is a live region that is in the page from the start', () => {
  for (const html of Object.values(pages)) {
    assert.match(html, /<p id="check-link-error" class="check__error" role="alert"><\/p>/);
  }
});

test('the Russian reserve line says most of it comes back, not nearly all', () => {
  assert.ok(pages.ru.includes('большая часть резерва возвращается'));
  assert.ok(!pages.ru.includes('почти весь'));
});

test('no visible home copy talks about the test network being reset', () => {
  for (const html of Object.values(pages)) {
    assert.ok(!/reset from time to time|test network|сбрасыва|тестовая сеть/i.test(html));
  }
});

test('the hero note says what is read and what is drawn, in every language, and says so when pads group ledgers', async () => {
  const { cloudStrings } = await import('../src/js/home-strings.mjs');
  const exact = { ledgers: 8, ledgersPerPad: 1, capped: false };
  const grouped = { ledgers: 340, ledgersPerPad: 34, capped: true };
  for (const lang of ACTIVE_LANGS) {
    const note = cloudStrings(lang, 'testnet').note;
    const plain = note(exact);
    assert.ok(plain.length > 0);
    const text = note(grouped);
    assert.notEqual(text, plain);
    assert.match(text, /340/);
    assert.match(text, /34/);
    assert.doesNotMatch(plain, /\d/, 'with one ledger per pad the note needs no numbers');
    assert.doesNotMatch(`${plain} ${text}`, /undefined|NaN|\{|\}/);
  }
  assert.match(cloudStrings('en', 'testnet').note(exact), /the ledger it was written in/);
  assert.match(cloudStrings('ru', 'testnet').note(exact), /Нарисованы, а не измерены/);
});

test('the hero note says nothing about cubes before the manifest has named the ledgers', async () => {
  const { cloudStrings } = await import('../src/js/home-strings.mjs');
  for (const lang of ACTIVE_LANGS) {
    assert.equal(cloudStrings(lang, 'testnet').note({ ledgers: null, ledgersPerPad: null, capped: false }), '', lang);
  }
});

test('the check-link error names the hash length and does not imply upper case only', async () => {
  const { CHECK_LINK_STRINGS } = await import('../src/js/home-strings.mjs');
  assert.equal(
    CHECK_LINK_STRINGS.en.invalid,
    'This is not an xrpl-file link. A link looks like xrpl-file://testnet/ followed by 64 hexadecimal characters.',
  );
  assert.equal(
    CHECK_LINK_STRINGS.ru.invalid,
    'Это не ссылка xrpl-file. Ссылка выглядит так: xrpl-file://testnet/ и 64 шестнадцатеричных символа.',
  );
  for (const lang of ACTIVE_LANGS) {
    assert.doesNotMatch(CHECK_LINK_STRINGS[lang].invalid, /A-F|a-f|0-9/, `${lang} names no character range`);
    assert.match(CHECK_LINK_STRINGS[lang].invalid, /64/);
  }
});

test('the problem section says the ledger entry holds only a link, without a pronoun that could mean the list', () => {
  assert.ok(pages.ru.includes('в записи есть только ссылка'));
  assert.ok(!pages.ru.includes('в нём есть только ссылка'));
  assert.match(pages.en, /the ledger entry holds only a link/);
});

test('the price intro has the same structure in English and Russian: short sentences, no colon', () => {
  const intro = (html) => /<h2 id="price-title">[^<]*<\/h2>\s*<p>([^<]*)<\/p>/.exec(html)[1];
  for (const lang of ACTIVE_LANGS) assert.ok(!intro(pages[lang]).includes(':'), `${lang} has a colon in the price intro`);
  const sentences = (text) => text.split(/(?<=[.!?])\s+/).length;
  assert.equal(sentences(intro(pages.en)), sentences(intro(pages.ru)));
});

test('the hero paragraph describes what the visitor sees: cubes while reading, then the file beside them', () => {
  const paragraph = (html) => /<p class="lead">[^<]*<\/p>\s*<p>([^<]*)<\/p>/.exec(html)[1];
  assert.equal(
    paragraph(pages.en),
    'The cubes show the chunks of a real file as they are read from the ledger. When every block has been checked against its fingerprint, the file itself appears beside them.',
  );
  assert.equal(
    paragraph(pages.ru),
    'Кубы изображают фрагменты настоящего файла и появляются по мере чтения из реестра. Когда каждый блок проверен по отпечатку, рядом появляется сам файл.',
  );
  for (const html of Object.values(pages)) assert.ok(!/picture|картинк/i.test(paragraph(html)), 'the hero is not a picture');
});

test('the home pages spell no canonical or hreflang by hand: the build writes them from the language list', () => {
  for (const [lang, html] of Object.entries(pages)) {
    assert.ok(!/rel="canonical"|rel="alternate"|hreflang/.test(html), lang);
  }
});
