import { test } from 'node:test';
import assert from 'node:assert/strict';
import { encodeManifest, decodeManifest, inconsistency } from '../src/manifest.mjs';
import { MANIFEST_BYTES } from '../src/tree.mjs';

const ROOT = 'A'.repeat(64);
const sample = (name = 'cover.png') => ({ type: 'image/png', size: 734512, chunk: 1000, root: ROOT, firstLedger: 106497159, lastLedger: 106497394, blocks: 1, lists: 0, cover: null, name });

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
    // A size past chunk * 2147483647 has more chunks than a chunk index can name. The .NET
    // reader refuses the same number, where its chunk arithmetic would overflow as well.
    { ...sample(), size: 1000 * 2147483647 + 1000, blocks: 2097152, lists: 1 },
  ]) {
    assert.equal(typeof inconsistency(m), 'string');
  }
  assert.equal(inconsistency({ ...sample(), size: 1000 * 2147483647, blocks: 2097152, lists: 1 }), null);
});

test('a count wider than the type the .NET reader gives it is not a count', () => {
  const base = { v: 1, type: 'a/b', size: 1, chunk: 1000, root: ROOT, ledgers: [1, 2], blocks: 1, lists: 0 };
  const json = (fields) => JSON.stringify({ ...base, ...fields });
  // blocks, lists and chunk are Int32 there and size an Int64; a number outside those is not one
  // this manifest may carry. Unbounded, "lists":1000000000 had the reader size an index array for
  // a billion memos -- eight gigabytes in the viewer's tab -- before reporting the mismatch.
  for (const bad of [
    json({ lists: 2147483648 }),
    json({ blocks: 4294967296 }),
    json({ chunk: 2147483648 }),
    json({ size: 9007199254740993 }),
    json({ lists: 1.5 }),
  ]) {
    assert.throws(() => decodeManifest(bad), bad);
  }
  assert.equal(decodeManifest(json({ blocks: 2, lists: 2147483647 })).lists, 2147483647);
});

const COVER = 'C'.repeat(64);

test('a cover is written after lists and before the name, and decodes upper-cased', () => {
  const json = encodeManifest({ ...sample(), cover: COVER });
  assert.ok(json.endsWith(`"lists":0,"cover":"${COVER}","name":"cover.png"}`));
  assert.equal(decodeManifest(json.replace(COVER, COVER.toLowerCase())).cover, COVER);
});

test('a cover that is not 64 hex characters reads as absent', () => {
  for (const value of ['123', '"AB"', 'null', '[]', '{}', `"${'C'.repeat(63)}Z"`]) {
    const json = encodeManifest(sample()).replace(',"name":', `,"cover":${value},"name":`);
    assert.deepEqual(decodeManifest(json), sample(), value);
  }
});

test('members this version does not define are ignored', () => {
  const json = encodeManifest(sample()).replace(',"name":', ',"future":{"tags":["a",1],"on":true},"x":null,"name":');
  assert.deepEqual(decodeManifest(json), sample());
});

test('the name is shortened after the cover is placed', () => {
  const json = encodeManifest({ ...sample('n'.repeat(2000)), cover: COVER });
  assert.equal(new TextEncoder().encode(json).length, MANIFEST_BYTES);
  assert.equal(decodeManifest(json).cover, COVER);
});

test('a malformed cover is never written', () => {
  assert.throws(() => encodeManifest({ ...sample(), cover: 'AB' }));
});
