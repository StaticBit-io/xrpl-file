import test from 'node:test';
import assert from 'node:assert/strict';
import {
  STALL_MS,
  allVerified,
  blockVerified,
  checkStall,
  chunkArrived,
  createMosaic,
  developed,
  failed,
} from '../src/js/mosaic-state.mjs';

const bytesOf = (value, length = 1000) => new Uint8Array(length).fill(value);

test('a new state has one empty cell per chunk and is reading', () => {
  const state = createMosaic({ chunks: 179 });
  assert.equal(state.cells.length, 179);
  assert.ok(state.cells.every((cell) => cell === 'empty'));
  assert.equal(state.phase, 'reading');
  assert.equal(state.error, null);
});

test('createMosaic rejects a chunk count that cannot be drawn', () => {
  assert.throws(() => createMosaic({ chunks: 0 }), RangeError);
  assert.throws(() => createMosaic({ chunks: 1.5 }), RangeError);
  assert.throws(() => createMosaic({ chunks: Number.NaN }), RangeError);
});

test('cells light in arrival order', () => {
  let state = createMosaic({ chunks: 179 });
  const first = state;
  state = chunkArrived(state, { index: 5, bytes: bytesOf(255) });
  state = chunkArrived(state, { index: 2, bytes: bytesOf(0) });
  state = chunkArrived(state, { index: 7, bytes: Uint8Array.from([0, 255]) });

  assert.deepEqual([2, 5, 7].map((i) => state.cells[i]), ['lit', 'lit', 'lit']);
  assert.equal(state.cells.filter((cell) => cell === 'lit').length, 3);
  // Transitions are pure: the state they were given is not touched.
  assert.ok(first.cells.every((cell) => cell === 'empty'));
});

test('a chunk outside the grid is ignored, and a verified cell never goes back to lit', () => {
  let state = createMosaic({ chunks: 10 });
  assert.equal(chunkArrived(state, { index: 10, bytes: bytesOf(1) }), state);
  assert.equal(chunkArrived(state, { index: -1, bytes: bytesOf(1) }), state);
  state = chunkArrived(state, { index: 3, bytes: bytesOf(10) });
  state = blockVerified(state, { firstChunk: 0, lastChunk: 9 });
  state = chunkArrived(state, { index: 3, bytes: bytesOf(20) });
  assert.equal(state.cells[3], 'verified');
});

test('blockVerified marks exactly its range', () => {
  let state = createMosaic({ chunks: 20 });
  for (let i = 0; i < 20; i += 1) state = chunkArrived(state, { index: i, bytes: bytesOf(i) });
  state = blockVerified(state, { firstChunk: 5, lastChunk: 9 });
  state.cells.forEach((cell, i) => assert.equal(cell, i >= 5 && i <= 9 ? 'verified' : 'lit', `cell ${i}`));
  assert.equal(state.phase, 'reading');
});

test('blockVerified clamps a range that runs past the last chunk', () => {
  let state = createMosaic({ chunks: 6 });
  state = blockVerified(state, { firstChunk: 4, lastChunk: 1023 });
  assert.deepEqual(state.cells, ['empty', 'empty', 'empty', 'empty', 'verified', 'verified']);
});

test('all cells verified moves to developing', () => {
  let state = createMosaic({ chunks: 12 });
  for (let i = 0; i < 12; i += 1) state = chunkArrived(state, { index: i, bytes: bytesOf(i) });

  state = blockVerified(state, { firstChunk: 0, lastChunk: 5 });
  assert.equal(allVerified(state).phase, 'reading', 'half verified is not developing');

  state = blockVerified(state, { firstChunk: 6, lastChunk: 11 });
  const next = allVerified(state);
  assert.equal(next.phase, 'developing');
  assert.equal(developed(next).phase, 'done');
  assert.equal(developed(state).phase, 'reading', 'a mosaic that is not developing does not finish');
});

test('failed keeps the lit cells and records the error code', () => {
  let state = createMosaic({ chunks: 20 });
  state = chunkArrived(state, { index: 0, bytes: bytesOf(100) });
  state = chunkArrived(state, { index: 1, bytes: bytesOf(200) });
  const next = failed(state, Object.assign(new Error('history starts later'), { code: 'NodeLacksHistory' }));

  assert.equal(next.phase, 'failed');
  assert.equal(next.error.code, 'NodeLacksHistory');
  assert.deepEqual(next.cells, state.cells);
  assert.equal(next.cells.filter((cell) => cell === 'lit').length, 2);
});

test('failed gives an error without a code a name of its own, and never undoes a finished mosaic', () => {
  const state = createMosaic({ chunks: 4 });
  assert.equal(failed(state, new Error('boom')).error.code, 'Unknown');
  const finished = developed(allVerified(blockVerified(state, { firstChunk: 0, lastChunk: 3 })));
  assert.equal(finished.phase, 'done');
  assert.equal(failed(finished, { code: 'Stalled' }), finished);
});

test("a 20 s gap with no chunk fails with code 'Stalled'", () => {
  assert.equal(STALL_MS, 20_000);
  let state = createMosaic({ chunks: 179, now: 1000 });

  assert.equal(checkStall(state, 1000 + STALL_MS - 1), state, 'one millisecond early is still reading');
  const stalled = checkStall(state, 1000 + STALL_MS);
  assert.equal(stalled.phase, 'failed');
  assert.equal(stalled.error.code, 'Stalled');

  // A chunk restarts the gap.
  state = chunkArrived(state, { index: 0, bytes: bytesOf(9) }, 16_000);
  assert.equal(checkStall(state, 16_000 + STALL_MS - 1).phase, 'reading');
  const later = checkStall(state, 16_000 + STALL_MS);
  assert.equal(later.phase, 'failed');
  assert.equal(later.error.code, 'Stalled');
  assert.equal(later.cells[0], 'lit', 'the cell that did arrive stays');
});

test('a mosaic that has stopped reading cannot stall', () => {
  const state = createMosaic({ chunks: 2, now: 0 });
  const verified = blockVerified(state, { firstChunk: 0, lastChunk: 1 });
  const developing = allVerified(verified);
  assert.equal(checkStall(developing, 10 * STALL_MS), developing);
  const dead = failed(state, { code: 'ChunkMissing' });
  assert.equal(checkStall(dead, 10 * STALL_MS), dead);
});
