import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createReadReport } from '../src/element/report.mjs';

const BLOCK_BYTES = 4000;

function make({ size = 2500, chunk = 1000, blocks = 1, signal = new AbortController().signal } = {}) {
  const events = [];
  const manifest = { size, chunk, blocks, firstLedger: 100, lastLedger: 140 };
  const report = createReadReport({
    manifest, blockBytes: BLOCK_BYTES, signal, emit: (type, detail) => events.push({ type, detail }),
  });
  return { report, events };
}

test('manifest detail has exactly the six fields', () => {
  const { report, events } = make();
  report.manifest();
  report.manifest();
  assert.deepEqual(events, [{
    type: 'xrpl-file-manifest',
    detail: { size: 2500, chunk: 1000, chunks: 3, blocks: 1, firstLedger: 100, lastLedger: 140 },
  }]);
});

test('a chunk reported twice is emitted once', () => {
  const { report, events } = make();
  report.chunk({ index: 1, ledger: 120 });
  report.chunk({ index: 1, ledger: 120 });
  report.chunk({ index: 0, ledger: 110 });
  assert.deepEqual(events, [
    { type: 'xrpl-file-chunk', detail: { index: 1, ledger: 120 } },
    { type: 'xrpl-file-chunk', detail: { index: 0, ledger: 110 } },
  ]);
});

test('a block reported twice is emitted once', () => {
  const { report, events } = make({ size: 9000, blocks: 3 });
  report.block(1);
  report.block(1);
  assert.equal(events.length, 1);
  assert.deepEqual(events[0], { type: 'xrpl-file-block', detail: { index: 1, firstChunk: 4, lastChunk: 7 } });
});

test('block ranges: block 0 of a 3-chunk file is 0..2, the last block is clamped to the last chunk', () => {
  const small = make();
  small.report.block(0);
  assert.deepEqual(small.events[0].detail, { index: 0, firstChunk: 0, lastChunk: 2 });

  // 9 chunks in blocks of 4: the last block holds only chunk 8.
  const large = make({ size: 9000, blocks: 3 });
  large.report.block(2);
  assert.deepEqual(large.events[0].detail, { index: 2, firstChunk: 8, lastChunk: 8 });
});

test('nothing is emitted after the signal aborts', () => {
  const controller = new AbortController();
  const { report, events } = make({ signal: controller.signal });
  report.chunk({ index: 0, ledger: 110 });
  controller.abort();
  report.manifest();
  report.chunk({ index: 1, ledger: 120 });
  report.block(0);
  assert.equal(events.length, 1);
});
