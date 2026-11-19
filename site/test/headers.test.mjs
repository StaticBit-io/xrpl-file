import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { headersFor, parseHeaders } from '../headers.mjs';

const rules = parseHeaders(await readFile(new URL('../src/_headers', import.meta.url), 'utf8'));

test('every matching block adds its headers', () => {
  const element = headersFor('/element/1.3.0/x.js', rules);
  assert.deepEqual(
    element.filter(([name]) => name !== 'Cache-Control'),
    [['X-Content-Type-Options', 'nosniff'], ['Access-Control-Allow-Origin', '*']],
  );
  assert.ok(element.some(([name, value]) => name === 'Cache-Control' && value === 'public, max-age=31536000, immutable'));
  assert.ok(headersFor('/index.html', rules).some(([name, value]) => name === 'Cache-Control' && value === 'no-cache'));
  assert.equal(headersFor('/file', rules).some(([name]) => name === 'Cache-Control'), false);
});

test('a placeholder matches one segment and a splat matches the rest', () => {
  const custom = parseHeaders('/a/:b\n  X-One: 1\n/c/*\n  X-Two: 2\n');
  assert.deepEqual(headersFor('/a/z', custom), [['X-One', '1']]);
  assert.deepEqual(headersFor('/a/z/y', custom), []);
  assert.deepEqual(headersFor('/c/z/y', custom), [['X-Two', '2']]);
});

test('the examples pages are revalidated like the other pages', () => {
  for (const path of ['/examples', '/ru/examples', '/de/examples', '/es/examples', '/pt/examples', '/zh/examples']) {
    assert.ok(headersFor(path, rules).some(([name, value]) => name === 'Cache-Control' && value === 'no-cache'), path);
  }
});
