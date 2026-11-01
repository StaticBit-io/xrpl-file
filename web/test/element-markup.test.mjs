import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';

test('no element module assigns markup built from data', async () => {
  for (const file of await readdir(new URL('../src/element/', import.meta.url), { recursive: true })) {
    if (!file.endsWith('.mjs')) continue;
    const source = await readFile(new URL(`../src/element/${file}`, import.meta.url), 'utf8');
    for (const line of source.split('\n').filter((l) => /innerHTML|outerHTML|insertAdjacentHTML|document\.write/.test(l))) {
      assert.match(line, /\.innerHTML = TEMPLATE;/, `${file}: ${line.trim()}`);
    }
  }
});
