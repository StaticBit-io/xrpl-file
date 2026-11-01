import { test } from 'node:test';
import assert from 'node:assert/strict';
import { TEXT_LIMIT, textPreview } from '../src/element/text.mjs';

test('the preview never splits a multi-byte sequence at the limit', () => {
  const cyr = new TextEncoder().encode('я'.repeat(10)); // 20 bytes
  assert.deepEqual(textPreview(cyr, 5), { text: 'яя', truncated: true });
  assert.deepEqual(textPreview(cyr, 100), { text: 'я'.repeat(10), truncated: false });
});

test('a cut that lands on a sequence boundary keeps everything before it', () => {
  const cyr = new TextEncoder().encode('я'.repeat(10));
  assert.deepEqual(textPreview(cyr, 4), { text: 'яя', truncated: true });
});

test('a four-byte sequence is cut whole or not at all', () => {
  const emoji = new TextEncoder().encode('a😀😀'); // 1 + 4 + 4 bytes
  assert.deepEqual(textPreview(emoji, 7), { text: 'a😀', truncated: true });
});

test('invalid UTF-8 becomes replacement characters', () => {
  assert.equal(textPreview(new Uint8Array([0x61, 0xff, 0x62])).text, 'a�b');
});

test('markup stays source text', () => {
  assert.equal(textPreview(new TextEncoder().encode('<b>x</b>')).text, '<b>x</b>');
});

test('a file exactly at the limit is not truncated', () => {
  assert.equal(textPreview(new Uint8Array(TEXT_LIMIT).fill(0x61)).truncated, false);
  assert.equal(textPreview(new Uint8Array(TEXT_LIMIT + 1).fill(0x61)).truncated, true);
  assert.equal(TEXT_LIMIT, 1_048_576);
});

test('an empty file is empty text', () => {
  assert.deepEqual(textPreview(new Uint8Array()), { text: '', truncated: false });
});
