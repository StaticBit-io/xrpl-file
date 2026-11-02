import test from 'node:test';
import assert from 'node:assert/strict';
import {
  MAX_FILE_PADS,
  cloudTimings,
  createTimeline,
  groundPoints,
  frameFor,
  layoutCloud,
  padOfLedger,
  scatterPoints,
} from '../src/js/cloud-scene.mjs';

const FIRST = 100;
// 123 chunks over 8 ledgers, like the hero file: 16 chunks on each of the first seven ledgers, 11 on the last.
const REAL = { chunks: 123, ledgers: 8, firstLedger: FIRST };
const ledgerOf = (index) => FIRST + Math.min(7, Math.floor(index / 16));

function arrive(tl, indices, time = 1000) {
  for (const i of indices) tl.chunkArrived(i, chunkBytes(i), time, ledgerOf(i));
}
const all = Array.from({ length: 123 }, (_, i) => i);
const placeKey = (c) => [c.x, c.y, c.z].map((v) => v.toFixed(5)).join(',');

test('a cube stands on the pad of the ledger its chunk was written in, never on another', () => {
  const layout = layoutCloud(REAL);
  const tl = createTimeline({ layout, timings: TIMINGS });
  arrive(tl, all);
  for (const i of all) {
    assert.equal(tl.cube(i).pad, layout.filePadFirst + (ledgerOf(i) - FIRST), `chunk ${i}`);
  }
});

test('a chunk that arrives out of order still stands on its own ledger', () => {
  const layout = layoutCloud(REAL);
  const tl = createTimeline({ layout, timings: TIMINGS });
  arrive(tl, [100, 3, 60]);
  assert.equal(tl.cube(100).pad, layout.filePadFirst + 6);
  assert.equal(tl.cube(3).pad, layout.filePadFirst);
  assert.equal(tl.cube(60).pad, layout.filePadFirst + 3);
});

test('no two cubes share a place, and none leaves the file span', () => {
  const layout = layoutCloud(REAL);
  const tl = createTimeline({ layout, timings: TIMINGS });
  arrive(tl, all);
  assert.equal(new Set(all.map((i) => placeKey(tl.cube(i)))).size, 123);
  for (const i of all) {
    const cube = tl.cube(i);
    assert.ok(cube.x >= layout.fileSpan[0] && cube.x + layout.cube <= layout.fileSpan[1] + 1e-9);
  }
});

test('a cube has no place until its chunk has arrived', () => {
  const tl = createTimeline({ layout: layoutCloud(REAL), timings: TIMINGS });
  assert.equal(tl.cube(0), null);
  arrive(tl, [0]);
  assert.notEqual(tl.cube(0), null);
  assert.equal(tl.cube(1), null);
});

test('the file stands on as many pads as the manifest names ledgers, up to the cap', () => {
  assert.equal(layoutCloud(REAL).filePads, 8);
  assert.equal(layoutCloud({ chunks: 123, ledgers: 1, firstLedger: FIRST }).filePads, 1);
  assert.equal(layoutCloud({ chunks: 123, ledgers: 500, firstLedger: FIRST }).filePads, MAX_FILE_PADS);
  assert.equal(layoutCloud({ chunks: 3, ledgers: 9, firstLedger: FIRST }).filePads, 9, 'a pad is a ledger, whether or not it holds a chunk');
});

test('the layout says whether pads are ledgers or groups of ledgers', () => {
  const exact = layoutCloud(REAL);
  assert.equal(exact.capped, false);
  assert.equal(exact.ledgersPerPad, 1);
  assert.equal(exact.ledgers, 8);
  const capped = layoutCloud({ chunks: 500, ledgers: 340, firstLedger: FIRST });
  assert.equal(capped.capped, true);
  assert.equal(capped.filePads, MAX_FILE_PADS);
  assert.equal(capped.ledgersPerPad, 34);
  assert.equal(capped.ledgers, 340);
});

test('when pads are capped, neighbouring ledgers share a pad in order, and every ledger has one', () => {
  const layout = layoutCloud({ chunks: 500, ledgers: 340, firstLedger: FIRST });
  let previous = 0;
  const seen = new Set();
  for (let ledger = FIRST; ledger < FIRST + 340; ledger += 1) {
    const pad = padOfLedger(layout, ledger);
    assert.ok(pad >= previous && pad < layout.filePads, `ledger ${ledger}`);
    previous = pad;
    seen.add(pad);
  }
  assert.equal(seen.size, MAX_FILE_PADS);
  assert.equal(padOfLedger(layout, FIRST), 0);
  assert.equal(padOfLedger(layout, FIRST + 339), MAX_FILE_PADS - 1);
});

test('a ledger outside the manifest range lands on the nearest end pad', () => {
  const layout = layoutCloud(REAL);
  assert.equal(padOfLedger(layout, FIRST - 5), 0);
  assert.equal(padOfLedger(layout, FIRST + 99), 7);
});

test('the manifest stands a few ledgers after the file, on its own pad', () => {
  const layout = layoutCloud(REAL);
  const lastFilePad = layout.filePadFirst + layout.filePads - 1;
  const gap = layout.manifestPad - lastFilePad;
  assert.ok(gap >= 2 && gap <= 4, `gap ${gap}`);
  assert.ok(layout.manifestPad < layout.padCount);
  assert.ok(layout.manifest.x > layout.fileSpan[1] - 1e-9);
});

test('the file callout stands above the tallest stack so far, and over the file before any cube', () => {
  const tl = createTimeline({ layout: layoutCloud(REAL), timings: TIMINGS });
  const empty = tl.fileAnchor();
  assert.ok(empty[0] > layoutCloud(REAL).fileSpan[0] && empty[0] < layoutCloud(REAL).fileSpan[1]);
  arrive(tl, all);
  const anchor = tl.fileAnchor();
  const tallest = Math.max(...all.map((i) => tl.cube(i).y + layoutCloud(REAL).cube));
  assert.ok(anchor[1] >= tallest);
});

test('a file read from few ledgers stays inside the frame: towers get deeper layers, not more height', () => {
  const layout = layoutCloud({ chunks: 123, ledgers: 2, firstLedger: FIRST });
  const tl = createTimeline({ layout, timings: TIMINGS });
  all.forEach((i) => tl.chunkArrived(i, chunkBytes(i), 1000, FIRST + (i < 62 ? 0 : 1)));
  for (const pad of new Set(all.map((i) => tl.cube(i).pad))) {
    const layers = new Set(all.filter((i) => tl.cube(i).pad === pad).map((i) => tl.cube(i).y.toFixed(5)));
    assert.ok(layers.size <= 12, `pad ${pad}: ${layers.size} layers`);
  }
  assert.equal(new Set(all.map((i) => placeKey(tl.cube(i)))).size, 123, 'no two cubes share a place');
});

test('the ground and the camera frame do not depend on how many ledgers the file spans', () => {
  assert.equal(layoutCloud({ chunks: 123, ledgers: 2, firstLedger: FIRST }).padCount, layoutCloud({ chunks: 123, ledgers: 8, firstLedger: FIRST }).padCount);
});

test('the camera frame holds the file and the manifest, and is never tighter than the minimum', () => {
  for (const ledgers of [1, 2, 8, 10]) {
    const layout = layoutCloud({ chunks: 123, ledgers, firstLedger: FIRST });
    const [left, right] = frameFor(layout);
    assert.ok(left <= layout.fileSpan[0] && right >= layout.manifest.x + layout.cube, `ledgers ${ledgers}`);
    assert.ok(right - left >= 1.1 - 1e-9);
  }
  const few = frameFor(layoutCloud({ chunks: 123, ledgers: 2, firstLedger: FIRST }));
  const many = frameFor(layoutCloud({ chunks: 123, ledgers: 10, firstLedger: FIRST }));
  assert.ok(few[1] - few[0] < many[1] - many[0], 'a file on two ledgers is framed closer');
});

test('a layout is the same every time for the same input', () => {
  assert.deepEqual(layoutCloud(REAL), layoutCloud(REAL));
});

test('a layout needs a whole number of chunks', () => {
  assert.throws(() => layoutCloud({ chunks: 0, ledgers: 1 }), RangeError);
  assert.throws(() => layoutCloud({ chunks: 1.5, ledgers: 1 }), RangeError);
});

test('the inside of a cube is scattered by the chunk bytes, the same way every time', () => {
  const bytes = Uint8Array.from({ length: 1000 }, (_, i) => (i * 37 + 11) % 256);
  const a = scatterPoints(bytes);
  const b = scatterPoints(bytes.slice());
  assert.deepEqual(a, b);
  assert.ok(a.length > 0 && a.length % 3 === 0 && a.length <= 3 * 40);
  assert.ok(a.every((v) => v >= 0 && v <= 1));
  assert.equal(a[0], Math.fround(bytes[0] / 255), 'the first point is read straight from the first three bytes');
  assert.equal(a[2], Math.fround(bytes[2] / 255));
  const other = scatterPoints(bytes.map((v) => 255 - v));
  assert.notDeepEqual(a, other, 'other bytes give another scatter');
});

test('a short or empty chunk scatters what it has and never reads past its end', () => {
  assert.equal(scatterPoints(new Uint8Array(0)).length, 0);
  assert.equal(scatterPoints(new Uint8Array(2)).length, 0);
  assert.equal(scatterPoints(new Uint8Array([0, 255, 255])).length, 3);
  const short = scatterPoints(Uint8Array.from({ length: 84 }, (_, i) => i));
  assert.ok(short.length <= 84);
  assert.ok(short.every((v) => Number.isFinite(v)));
});

test('the ground is the same every time and lies on the pads', () => {
  const a = groundPoints(20);
  assert.deepEqual(a, groundPoints(20));
  assert.ok(a.length > 4 * 1000 && a.length % 4 === 0);
});

test('timings: reduced motion has none, a replay finishes its animation within 300 ms of the last chunk', () => {
  const reduced = cloudTimings({ fast: false, reducedMotion: true });
  assert.deepEqual(Object.values(reduced).filter((v) => typeof v === 'number' && v !== 0), []);
  const fast = cloudTimings({ fast: true, reducedMotion: false });
  assert.ok(fast.sweep + fast.lime + fast.settle <= 300, 'a fast replay is quick');
  assert.equal(cloudTimings({ fast: true, reducedMotion: true }).sweep, 0, 'reduced motion wins over fast');
  const full = cloudTimings({ fast: false, reducedMotion: false });
  assert.ok(full.sweep > fast.sweep);
});

const TIMINGS = cloudTimings({ fast: false, reducedMotion: false });

function timeline(options = {}) {
  const layout = layoutCloud(REAL);
  return createTimeline({ layout, timings: options.timings ?? TIMINGS });
}

const chunkBytes = (index) => Uint8Array.from({ length: 1000 }, (_, i) => (i + index) % 256);

test('a cube is not there, and has no inside, until its chunk has arrived', () => {
  const tl = timeline();
  assert.equal(tl.visual(0, 5000).shown, false);
  assert.equal(tl.inside(0), null);
  tl.chunkArrived(0, chunkBytes(0), 1000, ledgerOf(0));
  assert.equal(tl.visual(1, 5000).shown, false, 'another chunk is still not there');
  const v = tl.visual(0, 1000 + TIMINGS.lit / 2);
  assert.equal(v.shown, true);
  assert.ok(v.lit > 0 && v.lit < 1, 'it fades in');
  assert.equal(tl.visual(0, 1000 + TIMINGS.lit).lit, 1);
  assert.ok(tl.inside(0).length > 0);
});

test('an arrived chunk is never lime until its block is verified', () => {
  const tl = timeline();
  for (let i = 0; i < 123; i += 1) tl.chunkArrived(i, chunkBytes(i), 1000 + i, ledgerOf(i));
  for (let i = 0; i < 123; i += 1) assert.equal(tl.visual(i, 1e9).lime, 0, `chunk ${i}`);
  assert.equal(tl.sweep(1e9), null, 'no sweep without a verification');
});

test('a verified block limes its own chunks only, left to right as the sweep passes', () => {
  const tl = timeline();
  for (let i = 0; i < 123; i += 1) tl.chunkArrived(i, chunkBytes(i), 1000, ledgerOf(i));
  tl.blockVerified({ firstChunk: 0, lastChunk: 99 }, 5000);

  assert.equal(tl.visual(0, 5000).lime, 0, 'the sweep starts at the left end');
  const early = tl.visual(10, 5000 + TIMINGS.sweep * 0.2).lime;
  const late = tl.visual(90, 5000 + TIMINGS.sweep * 0.2).lime;
  assert.ok(early > late, 'the sweep reaches the left of the block first');
  assert.equal(tl.visual(99, 5000 + TIMINGS.sweep + TIMINGS.lime).lime, 1);
  assert.equal(tl.visual(100, 1e9).lime, 0, 'a chunk outside the verified block stays unverified');
  assert.equal(tl.visual(122, 1e9).lime, 0);
});

test('the sweep runs across the block while it takes, then is gone', () => {
  const tl = timeline();
  for (let i = 0; i < 123; i += 1) tl.chunkArrived(i, chunkBytes(i), 1000, ledgerOf(i));
  tl.blockVerified({ firstChunk: 0, lastChunk: 122 }, 5000);
  const layout = layoutCloud(REAL);
  assert.equal(tl.sweep(4999), null, 'not before the verification');
  const start = tl.sweep(5000);
  const middle = tl.sweep(5000 + TIMINGS.sweep / 2);
  const end = tl.sweep(5000 + TIMINGS.sweep - 1);
  assert.ok(start.x <= middle.x && middle.x <= end.x);
  assert.ok(start.x <= layout.fileSpan[0] + 1e-6);
  assert.equal(tl.sweep(5000 + TIMINGS.sweep + 1), null);
});

test('a block verified before its chunk is drawn does not light that chunk', () => {
  const tl = timeline();
  tl.chunkArrived(0, chunkBytes(0), 1000, ledgerOf(0));
  tl.blockVerified({ firstChunk: 0, lastChunk: 3 }, 2000);
  assert.equal(tl.visual(1, 1e9).shown, false);
  assert.equal(tl.visual(1, 1e9).lime, 0);
  assert.equal(tl.visual(0, 1e9).lime, 1);
});

test('with reduced motion everything is final at once and nothing sweeps', () => {
  const tl = timeline({ timings: cloudTimings({ fast: false, reducedMotion: true }) });
  tl.chunkArrived(0, chunkBytes(0), 1000, ledgerOf(0));
  assert.equal(tl.visual(0, 1000).lit, 1);
  assert.equal(tl.visual(0, 1000).lime, 0, 'still not verified');
  tl.blockVerified({ firstChunk: 0, lastChunk: 0 }, 2000);
  assert.equal(tl.visual(0, 2000).lime, 1);
  assert.equal(tl.sweep(2000), null);
  assert.equal(tl.settleDelay(2000), 0);
});

test('a chunk reported twice keeps its first arrival time', () => {
  const tl = timeline();
  tl.chunkArrived(0, chunkBytes(0), 1000, ledgerOf(0));
  tl.chunkArrived(0, chunkBytes(0), 9000, ledgerOf(0));
  assert.equal(tl.visual(0, 1000 + TIMINGS.lit).lit, 1);
});

test('the manifest cube appears with the manifest and turns lime only when everything is verified', () => {
  const tl = timeline();
  assert.equal(tl.manifestVisual(5000).shown, false);
  tl.showManifest(1000);
  assert.equal(tl.manifestVisual(1000 + TIMINGS.lit).shown, true);
  assert.equal(tl.manifestVisual(1e9).lime, 0);
  tl.manifestVerified(7000);
  assert.equal(tl.manifestVisual(6999).lime, 0);
  assert.equal(tl.manifestVisual(7000 + TIMINGS.lime).lime, 1);
});

test('the settling time covers the last sweep and its fade, and is never negative', () => {
  const tl = timeline();
  for (let i = 0; i < 123; i += 1) tl.chunkArrived(i, chunkBytes(i), 1000, ledgerOf(i));
  tl.blockVerified({ firstChunk: 0, lastChunk: 122 }, 5000);
  const wait = tl.settleDelay(5000);
  assert.ok(wait >= TIMINGS.sweep + TIMINGS.lime);
  assert.equal(tl.settleDelay(1e9), 0);
});

test('a chunk reported without a ledger is not drawn: its place would be a guess', () => {
  const tl = timeline();
  tl.chunkArrived(0, chunkBytes(0), 1000, undefined);
  tl.chunkArrived(1, chunkBytes(1), 1000, 'x');
  assert.equal(tl.cube(0), null);
  assert.equal(tl.cube(1), null);
  assert.equal(tl.visual(0, 1e9).shown, false);
});

test('a ledger that holds most of the chunks does not leave the frame: its tower stays within twelve layers', () => {
  const layout = layoutCloud({ chunks: 123, ledgers: 2, firstLedger: FIRST });
  const tl = createTimeline({ layout, timings: TIMINGS });
  all.forEach((i) => tl.chunkArrived(i, chunkBytes(i), 1000, FIRST + (i < 108 ? 0 : 1)));
  const layers = new Set(all.filter((i) => tl.cube(i).pad === layout.filePadFirst).map((i) => tl.cube(i).y.toFixed(5)));
  assert.ok(layers.size <= 12, `${layers.size} layers`);
  assert.equal(new Set(all.map((i) => placeKey(tl.cube(i)))).size, 123);
});
