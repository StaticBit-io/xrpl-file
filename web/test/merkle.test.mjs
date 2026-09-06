import { test } from 'node:test';
import assert from 'node:assert/strict';
import { leaf, node, root } from '../src/merkle.mjs';
import { hex } from '../src/hex.mjs';

const encoder = new TextEncoder();
const leaves = async (count) => Promise.all(Array.from({ length: count }, (_, i) => leaf(encoder.encode(`leaf-${i}`))));

test('the root matches the platform known answers', async () => {
  const expected = {
    1: '305DF59F9590C3C9AC63D2B2743C388E3792449078CEBF7FB3DBE6471643B2B7',
    3: 'CF763A041C81CEEF1578A6083F75C61BEF2E0014F2A3E683A97FCFCA5BE7F19A',
    5: '00D21829A5503145348ABCF712513EACF2A274211AD83E970202BB5B6D80B286',
    1025: '59564AA4292CEA48C658B67D5D4E0E04694BF5517C6D41122963399C66F97B5A',
  };
  for (const [count, value] of Object.entries(expected)) {
    assert.equal(hex(await root(await leaves(Number(count)))), value);
  }
});

test('an unpaired element is promoted, not duplicated', async () => {
  const three = await leaves(3);
  assert.notDeepEqual(await root(three), await root([...three, three[2]]));
  assert.deepEqual(await root(three), await node(await node(three[0], three[1]), three[2]));
});

test('a node cannot pass as a leaf', async () => {
  const [l, r] = await leaves(2);
  const joined = new Uint8Array([...l, ...r]);
  assert.notDeepEqual(await node(l, r), await leaf(joined));
});

test('an empty tree is refused', async () => {
  await assert.rejects(root([]));
});
