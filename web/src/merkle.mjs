// Leaf = SHA256(0x00 ‖ data), node = SHA256(0x01 ‖ left ‖ right); an unpaired element is
// promoted, never duplicated. The same rules as the .NET MerkleTree, held together by the
// shared vectors.
const LEAF_PREFIX = 0x00;
const NODE_PREFIX = 0x01;

async function sha256(bytes) {
  return new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
}

function prefixed(prefix, ...parts) {
  const out = new Uint8Array(1 + parts.reduce((sum, p) => sum + p.length, 0));
  out[0] = prefix;
  let at = 1;
  for (const part of parts) {
    out.set(part, at);
    at += part.length;
  }
  return out;
}

export async function leaf(data) {
  return sha256(prefixed(LEAF_PREFIX, data));
}

export async function node(left, right) {
  return sha256(prefixed(NODE_PREFIX, left, right));
}

export async function root(hashes) {
  if (hashes.length === 0) throw new Error('a tree needs at least one leaf');
  let level = hashes;
  while (level.length > 1) {
    const next = [];
    for (let i = 0; i + 1 < level.length; i += 2) next.push(await node(level[i], level[i + 1]));
    if (level.length % 2 === 1) next.push(level[level.length - 1]);
    level = next;
  }
  return level[0];
}
