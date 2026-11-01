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
