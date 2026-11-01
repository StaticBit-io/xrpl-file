import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { loadVectors, vectorLedger, FakeSource, LINK, MANIFEST_HASH } from './vectors.mjs';
import { XrplFileReader } from '../src/reader.mjs';
import { parseLink } from '../src/link.mjs';
import { vectorBytes } from '../src/vector-stream.mjs';
import { split, blockHash, rootHex, blockCount, encodeEntries, BLOCK_CHUNKS, MANIFEST_BYTES } from '../src/tree.mjs';
import { decodeManifest, encodeManifest, inconsistency, MANIFEST_FORMAT } from '../src/manifest.mjs';
import { hex, utf8Hex } from '../src/hex.mjs';

test('the stream is sha256 counter mode over the seed', async () => {
  const block0 = createHash('sha256').update(Buffer.concat([Buffer.from('s'), Buffer.from([0, 0, 0, 0])])).digest();
  const block1 = createHash('sha256').update(Buffer.concat([Buffer.from('s'), Buffer.from([0, 0, 0, 1])])).digest();
  assert.deepEqual(await vectorBytes('s', 5), new Uint8Array(block0.subarray(0, 5)));
  assert.deepEqual(await vectorBytes('s', 35), new Uint8Array([...block0, ...block1.subarray(0, 3)]));
});

test('every file vector is reproduced by the library', async () => {
  const set = await loadVectors();
  assert.equal(set.version, 1);

  for (const vector of set.files) {
    const bytes = await vectorBytes(vector.seed, vector.size);
    assert.equal(createHash('sha256').update(bytes).digest('hex').toUpperCase(), vector.sha256, vector.seed);

    const chunks = split(bytes, vector.chunk);
    assert.equal(chunks.length, vector.chunks);
    assert.equal(blockCount(chunks.length), vector.blocks);

    const entries = [];
    for (let block = 0; block < vector.blocks; block++) {
      const h = hex(await blockHash(chunks.slice(block * BLOCK_CHUNKS, (block + 1) * BLOCK_CHUNKS)));
      assert.equal(h, vector.blockHashes[block], `${vector.seed} block ${block}`);
      entries.push({ i: block, h, l: vector.ledger.blockLedgers[block] });
    }

    assert.equal(await rootHex(entries), vector.root, vector.seed);
    if (vector.blocks > 1) assert.deepEqual(encodeEntries(entries), vector.lists);
    else assert.deepEqual(vector.lists, []);

    const manifest = decodeManifest(vector.manifest);
    assert.equal(inconsistency(manifest), null);
    assert.equal(manifest.root, vector.root);
    assert.equal(encodeManifest(manifest), vector.manifest);
  }
});

test('every file vector starts its manifest with the format', async () => {
  const set = await loadVectors();
  for (const vector of set.files) {
    assert.ok(vector.manifest.startsWith(`{"format":"${MANIFEST_FORMAT}","v":1,`), vector.seed);
  }
});

test('the long-name variant fills the manifest budget exactly and keeps the format and the cover', async () => {
  const set = await loadVectors();
  const variant = set.manifestVariants.find((v) => v.note === 'format, cover and a long name');
  assert.ok(variant);
  assert.equal(new TextEncoder().encode(variant.manifest).length, MANIFEST_BYTES);
  assert.ok(variant.manifest.startsWith(`{"format":"${MANIFEST_FORMAT}",`));
  assert.equal(encodeManifest(decodeManifest(variant.manifest)), variant.manifest);
});

test('every manifest variant decodes as its base vector with the cover and name it names, and reads', async () => {
  const set = await loadVectors();
  assert.ok(set.manifestVariants.length > 0);

  for (const variant of set.manifestVariants) {
    const vector = set.files.find((f) => f.size === variant.of);
    const decoded = decodeManifest(variant.manifest);
    assert.equal(decoded.cover, variant.cover, variant.note);
    assert.equal(decoded.name, variant.name, variant.note);
    const base = decodeManifest(vector.manifest);
    assert.deepEqual({ ...decoded, cover: null, name: base.name }, { ...base, cover: null }, variant.note);

    const ledger = await vectorLedger(vector);
    ledger.find((tx) => tx.hash === MANIFEST_HASH).memoDataHex = utf8Hex(variant.manifest);
    const result = await new XrplFileReader(new FakeSource(ledger)).read(parseLink(LINK));
    assert.equal(createHash('sha256').update(result.bytes).digest('hex').toUpperCase(), vector.sha256, variant.note);
  }
});
