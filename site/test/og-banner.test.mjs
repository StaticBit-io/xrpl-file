import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { BANNER_FILE, BANNER_HEIGHT, BANNER_WIDTH } from '../tools/og-banner.mjs';

// The banner is rendered by a browser (site/tools/og-banner.mjs), which is not byte-stable across
// versions, so only what link previews depend on is pinned: a PNG of the size the card tags declare.
test('the committed banner is a 1200 x 630 PNG', () => {
  const bytes = readFileSync(BANNER_FILE);
  assert.deepEqual([...bytes.subarray(0, 8)], [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  assert.equal(bytes.toString('ascii', 12, 16), 'IHDR');
  assert.equal(bytes.readUInt32BE(16), BANNER_WIDTH);
  assert.equal(bytes.readUInt32BE(20), BANNER_HEIGHT);
  // X refuses a card picture over 5 MB.
  assert.ok(bytes.length < 5 * 1024 * 1024);
});
