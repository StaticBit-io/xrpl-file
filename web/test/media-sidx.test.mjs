import assert from 'node:assert/strict';
import { test } from 'node:test';
import { findSidx, groupAt, parseSidx } from '../src/element/media/sidx.mjs';
import { ascii, read } from './media-helpers.mjs';

test('every sidx group starts at a moof', () => {
  const bytes = read('fragmented-sidx.mp4');
  const { groups } = parseSidx(bytes, findSidx(bytes));
  assert.ok(groups.length > 1);
  for (const g of groups) assert.equal(ascii(bytes.subarray(g.offset + 4, g.offset + 8)), 'moof');
});

test('the groups cover the file after the index and add up to the clip', () => {
  const bytes = read('fragmented-sidx.mp4');
  const index = parseSidx(bytes, findSidx(bytes));
  const last = index.groups.at(-1);
  assert.equal(last.offset + last.size, bytes.length);
  assert.ok(Math.abs(last.startTime + last.duration - 4) < 0.2);
});

test('groupAt picks the last group that starts at or before the moment', () => {
  const groups = [{ startTime: 0 }, { startTime: 1 }, { startTime: 2 }];
  assert.equal(groupAt(groups, 0), 0);
  assert.equal(groupAt(groups, 1.5), 1);
  assert.equal(groupAt(groups, 2), 2);
  assert.equal(groupAt(groups, 99), 2);
  assert.equal(groupAt(groups, -1), 0);
});

test('a file without a sidx has none to find', () => {
  assert.equal(findSidx(read('fragmented.mp4')), null);
  assert.equal(findSidx(read('plain-start.mp4')), null);
});

test('a sidx is found in the first bytes of the file as well as the whole file', () => {
  const bytes = read('fragmented-sidx.mp4');
  const at = findSidx(bytes);
  assert.equal(findSidx(bytes.subarray(0, at + 8)), at);
  assert.equal(findSidx(bytes.subarray(0, at - 1)), null);
});

test('parseSidx refuses a box that is not a sidx', () => {
  const bytes = read('fragmented-sidx.mp4');
  assert.throws(() => parseSidx(bytes, 0), /not a sidx/);
});
