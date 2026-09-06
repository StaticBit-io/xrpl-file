import { test } from 'node:test';
import assert from 'node:assert/strict';
import { encodeManifest, decodeManifest, inconsistency } from '../src/manifest.mjs';
import { MANIFEST_BYTES } from '../src/tree.mjs';

const ROOT = 'A'.repeat(64);
const sample = (name = 'cover.png') => ({ type: 'image/png', size: 734512, chunk: 1000, root: ROOT, firstLedger: 106497159, lastLedger: 106497394, blocks: 1, lists: 0, name });

test('encode writes the fields in the documented order', () => {
  assert.equal(
    encodeManifest(sample()),
    `{"v":1,"type":"image/png","size":734512,"chunk":1000,"root":"${ROOT}","ledgers":[106497159,106497394],"blocks":1,"lists":0,"name":"cover.png"}`,
  );
  assert.ok(!encodeManifest(sample(null)).includes('"name"'));
});

test('decode round-trips and upper-cases the root', () => {
  assert.deepEqual(decodeManifest(encodeManifest(sample()).replace(ROOT, ROOT.toLowerCase())), sample());
});

test('a long name is cut to the longest prefix that fits, by whole characters', () => {
  const encoded = encodeManifest(sample('n'.repeat(2000)));
  assert.ok(new TextEncoder().encode(encoded).length <= MANIFEST_BYTES);
  const emoji = decodeManifest(encodeManifest(sample('😀'.repeat(400)))).name;
  assert.equal([...emoji].every((c) => c.codePointAt(0) === 0x1F600), true);
});

test('a manifest that is not one is refused', () => {
  for (const bad of [
    'not json',
    '[]',
    `{"type":"a/b","size":1,"chunk":1000,"root":"${ROOT}","ledgers":[1,2],"blocks":1,"lists":0}`,
    `{"v":2,"type":"a/b","size":1,"chunk":1000,"root":"${ROOT}","ledgers":[1,2],"blocks":1,"lists":0}`,
    `{"v":1,"type":"a/b","size":1,"chunk":1000,"root":"AA","ledgers":[1,2],"blocks":1,"lists":0}`,
    `{"v":1,"type":"a/b","size":1,"chunk":1000,"root":"${ROOT}","ledgers":[2,1],"blocks":1,"lists":0}`,
    `{"v":1,"type":"a/b","size":"1","chunk":1000,"root":"${ROOT}","ledgers":[1,2],"blocks":1,"lists":0}`,
  ]) {
    assert.throws(() => decodeManifest(bad), bad);
  }
});

test('inconsistent counts are named', () => {
  assert.equal(inconsistency(sample()), null);
  assert.equal(inconsistency({ ...sample(), size: 2500500, blocks: 3, lists: 1 }), null);
  for (const m of [
    { ...sample(), blocks: 2 },
    { ...sample(), size: 2500500, blocks: 3, lists: 0 },
    { ...sample(), lists: 1 },
    { ...sample(), chunk: 1001 },
    { ...sample(), chunk: 0 },
    { ...sample(), size: -1 },
  ]) {
    assert.equal(typeof inconsistency(m), 'string');
  }
});
