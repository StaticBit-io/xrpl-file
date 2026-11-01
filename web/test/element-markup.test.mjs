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
    'dlBadge', 'dlType', 'dlSize', 'dlNet', 'dlLabel',
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

test('the styles switch tiers at 240 and 120 pixels of the element own width, and under 56 of its height', () => {
  assert.match(STYLES, /container-type:\s*size/);
  assert.match(STYLES, /@container \(width < 240px\) \{/);
  assert.match(STYLES, /@container \(width < 120px\) or \(height < 56px\) \{/);
  assert.match(STYLES, /@container \(height < 52px\) \{\s*\.badge \{ display: none; \}/);
  assert.doesNotMatch(STYLES, /\.98px/);
  assert.doesNotMatch(STYLES, /--xrpl-file-(?:compact|icon|tier|breakpoint)/);
});

test('the icon tier carries what a short box needs: the compact and icon blocks come in that order', () => {
  const compact = STYLES.indexOf('@container (width < 240px)');
  const icon = STYLES.indexOf('@container (width < 120px) or (height < 56px)');
  const badge = STYLES.indexOf('@container (height < 52px)');
  assert.ok(compact > 0 && icon > compact && badge > icon, 'later blocks win the cascade');
  const iconBlock = STYLES.slice(icon, badge);
  for (const piece of ['.badge { display: inline-block', '.download .btn { display: inline-flex', '.error .msg-icon { display: inline', '.caption.verified .cp-mark']) assert.ok(iconBlock.includes(piece), piece);
  assert.match(iconBlock, /\.btn:focus-visible \{ outline-offset: -2px; \}/);
});

test('every container-unit declaration in the full tier has a fixed one before it', () => {
  const full = STYLES.slice(0, STYLES.indexOf('@container'));
  for (const rule of full.match(/[^{}]+\{[^{}]*\}/g)) {
    const declarations = rule.slice(rule.indexOf('{') + 1, rule.lastIndexOf('}')).split(';').map((d) => d.trim()).filter(Boolean);
    declarations.forEach((declaration, index) => {
      if (!/[0-9](?:cqi|cqb|cqmin)/.test(declaration)) return;
      const property = declaration.slice(0, declaration.indexOf(':')).trim();
      const earlier = declarations.slice(0, index).filter((d) => d.startsWith(`${property}:`) && !/cq/.test(d));
      assert.ok(earlier.length > 0, `${property} in ${rule.trim().slice(0, 40)}`);
    });
  }
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

test('the download button is named by its visible word and then the file name, with no aria-label to contradict it', () => {
  const button = TEMPLATE.match(/<button[^>]*id="dlButton"[\s\S]*?<\/button>/)[0];
  assert.doesNotMatch(button, /aria-label/);
  assert.match(button, /class="lbl-full">Download<\/span><span class="lbl-short">Save<\/span> <span class="sr-name" id="dlLabel">/);
  assert.match(STYLES, /\.sr-name/);
});

test('the icon button shrinks to the box height so it cannot leave a 28 px box', () => {
  assert.match(STYLES, /\.download \.btn \{[^}]*height: min\(26px, calc\(100cqb - 6px\)\)/);
});
