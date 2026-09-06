import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { loadVectors } from './vectors.mjs';
import { vectorBytes } from '../src/vector-stream.mjs';
import { split, blockHash, rootHex, blockCount, encodeEntries, BLOCK_CHUNKS } from '../src/tree.mjs';
import { decodeManifest, encodeManifest, inconsistency } from '../src/manifest.mjs';
import { hex } from '../src/hex.mjs';

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
