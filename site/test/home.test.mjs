import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { HERO_ASPECT, HERO_LINK, PUBLISHER_URL } from '../src/js/config.mjs';

const pages = {
  en: await readFile(new URL('../src/index.html', import.meta.url), 'utf8'),
  ru: await readFile(new URL('../src/ru/index.html', import.meta.url), 'utf8'),
};

const ids = (html) => [...html.matchAll(/\bid="([^"]+)"/g)].map((match) => match[1]).sort();

test('config holds the hero file, its aspect and an unset publisher URL', () => {
  assert.equal(HERO_LINK, 'xrpl-file://testnet/32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59');
  assert.equal(HERO_ASPECT, 1);
  assert.equal(PUBLISHER_URL, null);
});

test('both home pages carry the same structure, in their own language', () => {
  assert.match(pages.en, /<html lang="en">/);
  assert.match(pages.ru, /<html lang="ru">/);
  assert.deepEqual(ids(pages.en), ids(pages.ru));
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

test('the publisher button ships disabled and marked "soon"', () => {
  assert.match(pages.en, /<button id="publisher"[^>]*\bdisabled\b[^>]*>.*soon/);
  assert.match(pages.ru, /<button id="publisher"[^>]*\bdisabled\b[^>]*>.*скоро/);
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
