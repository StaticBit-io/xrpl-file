import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import { TEMPLATE, STYLES } from '../src/element/template.mjs';

test('no element module assigns markup built from data', async () => {
  for (const file of await readdir(new URL('../src/element/', import.meta.url), { recursive: true })) {
    if (!file.endsWith('.mjs')) continue;
    const source = await readFile(new URL(`../src/element/${file}`, import.meta.url), 'utf8');
    for (const line of source.split('\n').filter((l) => /innerHTML|outerHTML|insertAdjacentHTML|document\.write/.test(l))) {
      assert.match(line, /\.innerHTML = TEMPLATE;/, `${file}: ${line.trim()}`);
    }
  }
});

test('the markup carries no style element: a strict CSP would drop it', () => {
  assert.doesNotMatch(TEMPLATE, /<style/i);
  assert.doesNotMatch(STYLES, /<style|<\/style/i);
});

test('the style text keeps the site hooks and the parts the markup exposes', () => {
  for (const name of ['accent', 'radius', 'background', 'foreground']) assert.match(STYLES, new RegExp(`--xrpl-file-${name},`));
  for (const part of ['loader', 'caption', 'message']) assert.match(TEMPLATE, new RegExp(`part="${part}"`));
});

test('every tier has its pieces in the markup, hidden or shown by the styles alone', () => {
  const ids = [
    'dlBadge', 'dlType', 'dlSize', 'dlNet',
    'progressBytes', 'progressBlocks',
    'msgIcon', 'msgShort', 'msgFull',
    'capMark', 'capWord', 'capNetwork', 'copyText', 'copyMark', 'copyStatus',
  ];
  for (const id of ids) assert.match(TEMPLATE, new RegExp(`id="${id}"`), id);
  const all = [...TEMPLATE.matchAll(/id="([^"]+)"/g)].map((m) => m[1]);
  assert.equal(new Set(all).size, all.length, 'ids are unique');
  assert.match(TEMPLATE, /class="lbl-full"[^>]*>Download</);
  assert.match(TEMPLATE, /class="lbl-short"[^>]*>Save</);
});

test('the icons are inline and carry no data', () => {
  assert.doesNotMatch(TEMPLATE, /<img[^>]*src=|<use|href=|url\(/i);
  for (const svg of TEMPLATE.match(/<svg[\s\S]*?<\/svg>/g)) assert.match(svg, /aria-hidden="true"/);
});

test('the styles switch tiers at 240 and 120 pixels of the element own width', () => {
  assert.match(STYLES, /container-type:\s*size/);
  assert.match(STYLES, /@container \(max-width: 239\.98px\)/);
  assert.match(STYLES, /@container \(max-width: 119\.98px\)/);
  assert.doesNotMatch(STYLES, /--xrpl-file-(?:compact|icon|tier|breakpoint)/);
});

test('no text in the styles is set below 11 pixels', () => {
  for (const [, value] of STYLES.matchAll(/font-size:\s*([^;]+);/g)) {
    const pixels = [...value.matchAll(/(\d+(?:\.\d+)?)px/g)].map((m) => Number(m[1]));
    const floor = value.startsWith('clamp(') ? pixels[0] : Math.min(...pixels);
    assert.ok(floor >= 11, value);
  }
});

test('the safety net: the box clips, single lines ellipsize, flex children may shrink', () => {
  assert.match(STYLES, /\.frame \{[^}]*overflow: hidden/);
  assert.match(STYLES, /\.view \{[^}]*overflow: hidden/);
  assert.match(STYLES, /text-overflow: ellipsis/);
  assert.match(STYLES, /\.center \{[^}]*min-width: 0/);
});
