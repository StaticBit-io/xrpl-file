import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  CHUNK_BYTES, LIST_BYTES, MANIFEST_BYTES, blockHash, rootHex, blockCount, chunkCount, split,
  encodeEntries, decodeEntries, chunkMarkerHex, listMarkerHex, chunkIndexOf, listIndexOf, isManifest, MANIFEST_MARKER_HEX,
} from '../src/tree.mjs';
import { leaf } from '../src/merkle.mjs';
import { hex, utf8Hex } from '../src/hex.mjs';

const encoder = new TextEncoder();

test('the budgets are the documented ones', () => {
  assert.equal(CHUNK_BYTES, 1000);
  assert.equal(LIST_BYTES, 982);
  assert.equal(MANIFEST_BYTES, 995);
});

test('a block hash matches the platform known answer', async () => {
  const chunks = Array.from({ length: 5 }, (_, i) => encoder.encode(`chunk-${i}`.padEnd(CHUNK_BYTES, 'x')));
  assert.equal(hex(await blockHash(chunks)), 'EED1B8043341A7F523DFF401B394D9C43B073226091649CD4F4C8BA018D5BFBD');
});

test('the root over block entries matches the platform known answer, in any order', async () => {
  const entries = [];
  for (let i = 0; i < 3; i++) entries.push({ i, h: hex(await leaf(new Uint8Array([i]))), l: 1000 + i * 40 });
  assert.equal(await rootHex(entries), '3B6CCCD7E3E023FF393006F030315EE7AD9EB111B022B41FBA7E5B7A3973F688');
  assert.equal(await rootHex([entries[2], entries[0], entries[1]]), await rootHex(entries));
});

test('counts', () => {
  assert.deepEqual([0, 1, 1024, 1025, 2501].map(blockCount), [0, 1, 1, 2, 3]);
  assert.deepEqual([0, 1, 1000, 1001, 2500500].map((s) => chunkCount(s, 1000)), [0, 1, 1, 2, 2501]);
});

test('split cuts full chunks and a short tail', () => {
  const file = new Uint8Array(2500).map((_, i) => i % 251);
  const chunks = split(file, 1000);
  assert.equal(chunks.length, 3);
  assert.equal(chunks[2].length, 500);
  assert.deepEqual(new Uint8Array([...chunks[0], ...chunks[1], ...chunks[2]]), file);
});

test('markers round-trip and refuse what is not a marker', () => {
  assert.equal(chunkMarkerHex(0), utf8Hex('xf/c/0'));
  assert.equal(chunkIndexOf(chunkMarkerHex(999999999999)), 999999999999);
  assert.equal(listIndexOf(listMarkerHex(3)), 3);
  assert.equal(chunkIndexOf(listMarkerHex(3)), null);
  for (const bad of [null, '', 'ZZ', utf8Hex('xf/c/'), utf8Hex('xf/c/01'), utf8Hex('xf/c/-1'), utf8Hex('vp/c/1')]) {
    assert.equal(chunkIndexOf(bad), null);
  }
  assert.throws(() => chunkMarkerHex(-1));
  assert.throws(() => chunkMarkerHex(1000000000000));
  assert.equal(isManifest(MANIFEST_MARKER_HEX), true);
  assert.equal(isManifest(MANIFEST_MARKER_HEX.toLowerCase()), true);
  assert.equal(isManifest(utf8Hex('xf/m/1')), false);
});

test('entries encode compactly, fit the budget and decode back', async () => {
  const entries = [];
  for (let i = 0; i < 100; i++) entries.push({ i, h: hex(await leaf(new Uint8Array([i]))), l: 106497159 + i * 11 });
  const payloads = encodeEntries(entries);
  assert.ok(payloads.length >= 10);
  for (const p of payloads) assert.ok(new TextEncoder().encode(p).length <= LIST_BYTES);
  assert.deepEqual(decodeEntries(payloads), entries);
  assert.equal(encodeEntries([{ i: 0, h: 'A'.repeat(64), l: 1000 }])[0], `[{"i":0,"h":"${'A'.repeat(64)}","l":1000}]`);
});

test('a broken list is refused, a lower-case hash is upper-cased', () => {
  for (const bad of ['not json', '[]', '[{"i":1,"h":"AA","l":1}]', `[{"i":1,"h":"${'0'.repeat(64)}","l":1}]`]) {
    assert.throws(() => decodeEntries([bad]));
  }
  assert.throws(() => decodeEntries([`[{"i":0,"h":"${'B'.repeat(64)}","l":1},{"i":0,"h":"${'B'.repeat(64)}","l":2}]`]));
  assert.equal(decodeEntries([`[{"i":0,"h":"${'c'.repeat(64)}","l":1}]`])[0].h, 'C'.repeat(64));
});
