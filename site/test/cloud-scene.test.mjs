import test from 'node:test';
import assert from 'node:assert/strict';
import {
  MAX_FILE_PADS,
  cloudTimings,
  createTimeline,
  groundPoints,
  frameFor,
  layoutCloud,
  scatterPoints,
} from '../src/js/cloud-scene.mjs';

const REAL = { chunks: 123, ledgers: 8 };

test('every chunk gets one cube, ledger by ledger in chunk order, none on top of another', () => {
  const layout = layoutCloud(REAL);
  assert.equal(layout.cubes.length, 123);
  for (let i = 1; i < layout.cubes.length; i += 1) {
    assert.ok(layout.cubes[i].pad >= layout.cubes[i - 1].pad, `chunk ${i} stands on an earlier ledger than chunk ${i - 1}`);
  }
  const places = new Set(layout.cubes.map((c) => [c.x, c.y, c.z].map((v) => v.toFixed(5)).join(',')));
  assert.equal(places.size, 123);
});

test('the file stands on as many pads as the manifest names ledgers, spread evenly', () => {
  const layout = layoutCloud(REAL);
  assert.equal(layout.filePads, 8);
  const perPad = new Array(8).fill(0);
  for (const cube of layout.cubes) perPad[cube.pad - layout.filePadFirst] += 1;
  assert.equal(perPad.reduce((a, b) => a + b, 0), 123);
  assert.ok(Math.max(...perPad) - Math.min(...perPad) <= 1, `uneven: ${perPad}`);
});

test('the pad count follows the ledger range but never exceeds the chunks or the cap', () => {
  assert.equal(layoutCloud({ chunks: 123, ledgers: 1 }).filePads, 1);
  assert.equal(layoutCloud({ chunks: 123, ledgers: 500 }).filePads, MAX_FILE_PADS);
  assert.equal(layoutCloud({ chunks: 3, ledgers: 9 }).filePads, 3);
  assert.equal(layoutCloud({ chunks: 1, ledgers: 1 }).cubes.length, 1);
});

test('the manifest stands a few ledgers after the file, on its own pad', () => {
  const layout = layoutCloud(REAL);
  const lastFilePad = layout.filePadFirst + layout.filePads - 1;
  const gap = layout.manifestPad - lastFilePad;
  assert.ok(gap >= 2 && gap <= 4, `gap ${gap}`);
  assert.ok(layout.manifestPad < layout.padCount);
  assert.ok(layout.manifest.x > layout.cubes.at(-1).x);
});

test('the file span covers every cube and the callout anchor stands above the tallest stack', () => {
  const layout = layoutCloud(REAL);
  for (const cube of layout.cubes) assert.ok(cube.x >= layout.fileSpan[0] && cube.x <= layout.fileSpan[1]);
  const tallest = Math.max(...layout.cubes.map((c) => c.y + layout.cube));
  assert.ok(layout.anchors.file[1] >= tallest);
  assert.ok(layout.anchors.manifest[1] > layout.manifest.y);
});

test('a file read from few ledgers stays inside the frame: towers get deeper layers, not more height', () => {
  const layout = layoutCloud({ chunks: 123, ledgers: 2 });
  assert.equal(layout.filePads, 2);
  for (const pad of new Set(layout.cubes.map((c) => c.pad))) {
    const layers = new Set(layout.cubes.filter((c) => c.pad === pad).map((c) => c.y.toFixed(5)));
    assert.ok(layers.size <= 12, `pad ${pad}: ${layers.size} layers`);
  }
  assert.ok(layout.top < 0.5, `top ${layout.top}`);
  const places = new Set(layout.cubes.map((c) => [c.x, c.y, c.z].map((v) => v.toFixed(5)).join(',')));
  assert.equal(places.size, 123, 'no two cubes share a place');
  for (const cube of layout.cubes) assert.ok(cube.x >= layout.fileSpan[0] && cube.x + layout.cube <= layout.fileSpan[1] + 1e-9);
});

test('the ground and the camera frame do not depend on how many ledgers the file spans', () => {
  assert.equal(layoutCloud({ chunks: 123, ledgers: 2 }).padCount, layoutCloud({ chunks: 123, ledgers: 8 }).padCount);
});

test('the camera frame holds the file and the manifest, and is never tighter than the minimum', () => {
  for (const ledgers of [1, 2, 8, 10]) {
    const layout = layoutCloud({ chunks: 123, ledgers });
    const [left, right] = frameFor(layout);
    assert.ok(left <= layout.fileSpan[0] && right >= layout.manifest.x + layout.cube, `ledgers ${ledgers}`);
    assert.ok(right - left >= 1.1 - 1e-9);
  }
  const few = frameFor(layoutCloud({ chunks: 123, ledgers: 2 }));
  const many = frameFor(layoutCloud({ chunks: 123, ledgers: 10 }));
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
  tl.chunkArrived(0, chunkBytes(0), 1000);
  assert.equal(tl.visual(1, 5000).shown, false, 'another chunk is still not there');
  const v = tl.visual(0, 1000 + TIMINGS.lit / 2);
  assert.equal(v.shown, true);
  assert.ok(v.lit > 0 && v.lit < 1, 'it fades in');
  assert.equal(tl.visual(0, 1000 + TIMINGS.lit).lit, 1);
  assert.ok(tl.inside(0).length > 0);
});

test('an arrived chunk is never lime until its block is verified', () => {
  const tl = timeline();
  for (let i = 0; i < 123; i += 1) tl.chunkArrived(i, chunkBytes(i), 1000 + i);
  for (let i = 0; i < 123; i += 1) assert.equal(tl.visual(i, 1e9).lime, 0, `chunk ${i}`);
  assert.equal(tl.sweep(1e9), null, 'no sweep without a verification');
});

test('a verified block limes its own chunks only, left to right as the sweep passes', () => {
  const tl = timeline();
  for (let i = 0; i < 123; i += 1) tl.chunkArrived(i, chunkBytes(i), 1000);
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
  for (let i = 0; i < 123; i += 1) tl.chunkArrived(i, chunkBytes(i), 1000);
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
  tl.chunkArrived(0, chunkBytes(0), 1000);
  tl.blockVerified({ firstChunk: 0, lastChunk: 3 }, 2000);
  assert.equal(tl.visual(1, 1e9).shown, false);
  assert.equal(tl.visual(1, 1e9).lime, 0);
  assert.equal(tl.visual(0, 1e9).lime, 1);
});

test('with reduced motion everything is final at once and nothing sweeps', () => {
  const tl = timeline({ timings: cloudTimings({ fast: false, reducedMotion: true }) });
  tl.chunkArrived(0, chunkBytes(0), 1000);
  assert.equal(tl.visual(0, 1000).lit, 1);
  assert.equal(tl.visual(0, 1000).lime, 0, 'still not verified');
  tl.blockVerified({ firstChunk: 0, lastChunk: 0 }, 2000);
  assert.equal(tl.visual(0, 2000).lime, 1);
  assert.equal(tl.sweep(2000), null);
  assert.equal(tl.settleDelay(2000), 0);
});

test('a chunk reported twice keeps its first arrival time', () => {
  const tl = timeline();
  tl.chunkArrived(0, chunkBytes(0), 1000);
  tl.chunkArrived(0, chunkBytes(0), 9000);
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
  for (let i = 0; i < 123; i += 1) tl.chunkArrived(i, chunkBytes(i), 1000);
  tl.blockVerified({ firstChunk: 0, lastChunk: 122 }, 5000);
  const wait = tl.settleDelay(5000);
  assert.ok(wait >= TIMINGS.sweep + TIMINGS.lime);
  assert.equal(tl.settleDelay(1e9), 0);
});
