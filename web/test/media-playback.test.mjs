import { test } from 'node:test';
import assert from 'node:assert/strict';
import { XrplFileError } from '../src/link.mjs';
import {
  AHEAD_SECONDS, READ_AHEAD, KEEP_SECONDS, RETRY_CAP_MS,
  ReadAhead, retryDelay, shouldRead, seekDecision, nearestBuffered, overlayState, farRanges, rangeAt, continuation, intersectRanges,
} from '../src/element/media/playback.mjs';

test('the constants are the spec values', () => {
  assert.deepEqual([AHEAD_SECONDS, READ_AHEAD, KEEP_SECONDS, RETRY_CAP_MS], [30, 3, 120, 30_000]);
});

test('never more than three reads at once, from the frontier', () => {
  const r = new ReadAhead({ blocks: 10 }); r.restart(0, 0);
  assert.deepEqual([r.next(), r.next(), r.next(), r.next()].map((x) => x?.block ?? null), [0, 1, 2, null]);
});

test('blocks finished out of order are appended in order', () => {
  const r = new ReadAhead({ blocks: 10 }); const g = r.restart(0, 0); r.next(); r.next(); r.next();
  assert.deepEqual(r.completed(2, g), []); assert.deepEqual(r.completed(0, g), [0]); assert.deepEqual(r.completed(1, g), [1, 2]);
});

test('a read for an older seek is dropped', () => {      // Review Focus 5
  const r = new ReadAhead({ blocks: 50 }); const g1 = r.restart(0, 0); r.next();
  const g2 = r.restart(30, 30 * 1_024_000 + 17); r.restart(40, 40 * 1_024_000);
  assert.deepEqual(r.completed(0, g1), []); assert.deepEqual(r.completed(30, g2), []);
  assert.equal(r.next().block, 40);
});

test('a failed block stops reading at that block', () => {
  const r = new ReadAhead({ blocks: 10 }); const g = r.restart(0, 0); r.next(); r.next();
  r.failed(1, g); assert.equal(r.stoppedAt, 1); assert.deepEqual(r.completed(0, g), [0]); assert.equal(r.next(), null);
});

test('a dropped connection is retried for as long as it takes, a verification failure never', () => {   // Review Focus 4
  const unreachable = new XrplFileError('NodeUnreachable', 'x');
  assert.deepEqual([0, 1, 2, 3, 4, 5, 6, 50].map((a) => retryDelay(unreachable, a)), [1000, 2000, 4000, 8000, 16000, 30000, 30000, 30000]);
  for (const code of ['BlockMismatch', 'ChunkMissing', 'ListMismatch']) assert.equal(retryDelay(new XrplFileError(code, 'x'), 0), null);
  assert.equal(retryDelay(new TypeError('x'), 0), null);
});

test('before play only the first media block is read; while playing, up to 30 s ahead', () => {
  const base = { streamingAllowed: true, bufferedAhead: 0 };
  assert.equal(shouldRead({ ...base, started: false, playing: false, firstMediaAppended: false }), true);
  assert.equal(shouldRead({ ...base, started: false, playing: false, firstMediaAppended: true }), false);
  assert.equal(shouldRead({ ...base, started: true, playing: true, firstMediaAppended: true, bufferedAhead: 29 }), true);
  assert.equal(shouldRead({ ...base, started: true, playing: true, firstMediaAppended: true, bufferedAhead: 30 }), false);
  assert.equal(shouldRead({ ...base, started: true, playing: false, firstMediaAppended: true }), false);
  assert.equal(shouldRead({ ...base, streamingAllowed: false, started: true, playing: true, firstMediaAppended: true }), false);
});

test('a seek inside the buffer reads nothing; outside, it reads or snaps', () => {
  const buffered = [[0, 40], [300, 330]];
  assert.deepEqual(seekDecision({ time: 310, buffered, seekable: true }), { kind: 'buffered' });
  assert.deepEqual(seekDecision({ time: 200, buffered, seekable: true }), { kind: 'read' });
  assert.deepEqual(seekDecision({ time: 200, buffered, seekable: false }), { kind: 'snap', time: 300 });
  assert.deepEqual(seekDecision({ time: 100, buffered, seekable: false }), { kind: 'snap', time: 40 });
});

test('the overlay shows buffering while waiting and the error only at the gap', () => {
  const quiet = { started: true, waiting: false, readsInFlight: 0, retrying: false, stoppedAt: null, atGap: false };
  assert.equal(overlayState(quiet), 'none');
  assert.equal(overlayState({ ...quiet, waiting: true, readsInFlight: 1 }), 'buffering');
  assert.equal(overlayState({ ...quiet, waiting: true, retrying: true }), 'buffering');
  assert.equal(overlayState({ ...quiet, stoppedAt: 7, atGap: false }), 'none');
  assert.equal(overlayState({ ...quiet, stoppedAt: 7, atGap: true }), 'error');
  assert.equal(overlayState({ ...quiet, started: false }), 'buffering');
});

test('a full buffer gives up what is further than two minutes from the playhead', () => {
  assert.deepEqual(farRanges([[0, 600]], 300), [[0, 180], [420, 600]]);
  assert.deepEqual(farRanges([[250, 350]], 300), []);
});

// Beyond the brief's list: the edges the glue relies on.

test('a window of three keeps a slow block from letting memory run away', () => {
  const r = new ReadAhead({ blocks: 20 }); const g = r.restart(5, 0);
  assert.deepEqual([r.next(), r.next(), r.next()].map((x) => x.block), [5, 6, 7]);
  assert.deepEqual(r.completed(6, g), []);
  assert.deepEqual(r.completed(7, g), []);
  // Block 5 is slow: 6 and 7 are done but held, so nothing past 7 is read yet.
  assert.equal(r.inFlight, 1);
  assert.equal(r.next(), null);
  assert.deepEqual(r.completed(5, g), [5, 6, 7]);
  assert.deepEqual([r.next(), r.next(), r.next(), r.next()].map((x) => x?.block ?? null), [8, 9, 10, null]);
});

test('reading stops at the end of the file, and the last block finishes the stream', () => {
  const r = new ReadAhead({ blocks: 2 }); const g = r.restart(0, 0);
  assert.deepEqual([r.next(), r.next(), r.next()].map((x) => x?.block ?? null), [0, 1, null]);
  assert.equal(r.finished, false);
  r.completed(0, g); r.completed(1, g);
  assert.equal(r.finished, true);
});

test('a restart forgets reads, failures and held blocks of the generation before', () => {
  const r = new ReadAhead({ blocks: 10 }); const g1 = r.restart(0, 0);
  r.next(); r.next(); r.failed(1, g1);
  assert.equal(r.stoppedAt, 1);
  const g2 = r.restart(4, 4_500);
  assert.notEqual(g1, g2);
  assert.equal(r.stoppedAt, null);
  assert.equal(r.inFlight, 0);
  assert.equal(r.fromOffset, 4_500);
  assert.equal(r.next().generation, g2);
  // A late failure of the old generation changes nothing.
  r.failed(1, g1);
  assert.equal(r.stoppedAt, null);
});

test('only the first block of a generation starts inside its block', () => {
  const r = new ReadAhead({ blocks: 10 }); r.restart(3, 3_000_017);
  assert.equal(r.offsetFor(3), 3_000_017);
  assert.equal(r.offsetFor(4), undefined);
});

test('the earliest failure is where reading stops, and blocks before it still arrive', () => {
  const r = new ReadAhead({ blocks: 10 }); const g = r.restart(0, 0);
  r.next(); r.next(); r.next();
  r.failed(2, g); r.failed(1, g);
  assert.equal(r.stoppedAt, 1);
  assert.deepEqual(r.completed(0, g), [0]);
  assert.deepEqual(r.completed(2, g), []);
});

test('a block reported twice is appended once', () => {
  const r = new ReadAhead({ blocks: 4 }); const g = r.restart(0, 0); r.next(); r.next();
  assert.deepEqual(r.completed(0, g), [0]);
  assert.deepEqual(r.completed(0, g), []);
  assert.deepEqual(r.completed(1, g), [1]);
});

test('nothing is handed out before the first restart', () => {
  const r = new ReadAhead({ blocks: 4 });
  assert.equal(r.next(), null);
  assert.deepEqual(r.completed(0, 0), []);
});

test('an unreachable node is retried whatever the attempt number, and nothing else is', () => {
  const unreachable = new XrplFileError('NodeUnreachable', 'x');
  assert.equal(retryDelay(unreachable, 1_000_000), RETRY_CAP_MS);
  assert.equal(retryDelay(new XrplFileError('NodeLacksHistory', 'x'), 0), null);
  assert.equal(retryDelay(new XrplFileError('ManifestNotFound', 'x'), 0), null);
  assert.equal(retryDelay(null, 0), null);
  assert.equal(retryDelay(new DOMException('x', 'AbortError'), 0), null);
});

test('the read-ahead is switched off while the browser says it has enough', () => {
  assert.equal(shouldRead({ streamingAllowed: false, started: false, playing: false, firstMediaAppended: false, bufferedAhead: 0 }), false);
});

test('the nearest point of the buffer is the playhead when it is inside', () => {
  assert.equal(nearestBuffered(310, [[0, 40], [300, 330]]), 310);
  assert.equal(nearestBuffered(1000, [[0, 40], [300, 330]]), 330);
  assert.equal(nearestBuffered(5, []), null);
});

test('a seek with nothing buffered and no index goes back to the start', () => {
  assert.deepEqual(seekDecision({ time: 50, buffered: [], seekable: false }), { kind: 'snap', time: 0 });
  assert.deepEqual(seekDecision({ time: 50, buffered: [], seekable: true }), { kind: 'read' });
});

test('a seek to the very end of a range is not in the buffer: there is nothing to play there', () => {
  assert.deepEqual(seekDecision({ time: 40, buffered: [[0, 40]], seekable: true }), { kind: 'read' });
  assert.deepEqual(seekDecision({ time: 39.95, buffered: [[0, 40]], seekable: true }), { kind: 'read' });
  assert.deepEqual(seekDecision({ time: 39.5, buffered: [[0, 40]], seekable: true }), { kind: 'buffered' });
  assert.deepEqual(seekDecision({ time: 0, buffered: [[0, 40]], seekable: true }), { kind: 'buffered' });
});

test('an error is shown at the gap even before any frame, and wins over buffering', () => {
  const state = { started: false, waiting: true, readsInFlight: 1, retrying: true, stoppedAt: 0, atGap: true };
  assert.equal(overlayState(state), 'error');
});

test('farRanges cuts both sides and drops nothing near the playhead', () => {
  assert.deepEqual(farRanges([[0, 10], [295, 305], [900, 1000]], 300), [[0, 10], [900, 1000]]);
  assert.deepEqual(farRanges([[0, 600]], 300, 60), [[0, 240], [360, 600]]);
  assert.deepEqual(farRanges([], 0), []);
});

test('the range holding a time, with a little slack at both ends', () => {
  const ranges = [[0, 40], [300, 330]];
  assert.deepEqual(rangeAt(ranges, 10), [0, 40]);
  assert.deepEqual(rangeAt(ranges, 40.05), [0, 40]);
  assert.deepEqual(rangeAt(ranges, 299.95), [300, 330]);
  assert.equal(rangeAt(ranges, 100), null);
});

test('the read-ahead follows the playhead when it sits in a range the last read did not write', () => {
  // The visitor watched 0..35, jumped to 300 (reads now write there), then came back to 10.
  const ranges = [[0, 35], [300, 330]];
  assert.deepEqual(continuation({ buffered: ranges, time: 10, anchor: 300 }), { time: 35 });
  // Playing in the range the reads write to: nothing to change.
  assert.equal(continuation({ buffered: ranges, time: 310, anchor: 300 }), null);
  assert.equal(continuation({ buffered: [[0, 35]], time: 10, anchor: 0 }), null);
  // The playhead is in no range: that is a seek's business, not a continuation.
  assert.equal(continuation({ buffered: ranges, time: 100, anchor: 300 }), null);
  assert.equal(continuation({ buffered: [], time: 0, anchor: 0 }), null);
});

test('what two buffers share is what can be played', () => {
  assert.deepEqual(intersectRanges([[0, 40], [60, 90]], [[0.02, 35], [70, 100]]), [[0.02, 35], [70, 90]]);
  assert.deepEqual(intersectRanges([[0, 10]], [[10, 20]]), []);
  assert.deepEqual(intersectRanges([[0, 10]], []), []);
});
