import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mimeFor, parseMovie, seekPoint } from '../src/element/media/movie.mjs';
import { ascii, expected, isSampleStart, moovOf, read, syncSamples } from './media-helpers.mjs';

for (const f of ['plain-start.mp4', 'plain-end.mp4']) {
  test(`${f}: a seek lands on the keyframe at or before the target`, () => {
    const movie = parseMovie(moovOf(f));
    const video = movie.tracks.find((t) => t.kind === 'video');
    for (const target of [0, 1.3, 2.5, 3.9]) {
      const { time, offset } = seekPoint(movie, target);
      const sync = syncSamples(video); // [{ cts, offset }] from the parsed table
      // The first sample is always a candidate, as in mp4box: its composition time can be past 0 when B-frames delay it.
      const chosen = sync.filter((s) => s.cts <= target).at(-1) ?? sync[0];
      assert.ok(time <= chosen.cts && time <= target);
      assert.ok(expected[f].videoKeyframeOffsets.includes(chosen.offset));
      assert.ok(offset <= chosen.offset && isSampleStart(movie, offset));
      const next = sync.find((s) => s.cts > chosen.cts);
      if (next) assert.ok(next.cts > target);
    }
  });

  test(`${f}: the video keyframes of the table are the ones ffprobe lists`, () => {
    const video = parseMovie(moovOf(f)).tracks.find((t) => t.kind === 'video');
    assert.deepEqual(syncSamples(video).map((s) => s.offset), expected[f].videoKeyframeOffsets);
  });
}

test('the same file reads the same wherever its moov sits', () => {
  const a = parseMovie(moovOf('plain-start.mp4'));
  const b = parseMovie(moovOf('plain-end.mp4'));
  assert.deepEqual(a.tracks.map((t) => [t.id, t.kind, t.codec, t.timescale, t.table.count]),
    b.tracks.map((t) => [t.id, t.kind, t.codec, t.timescale, t.table.count]));
  // Only the sample offsets move with the moov.
  assert.notDeepEqual(a.tracks[0].table.offsets, b.tracks[0].table.offsets);
});

test('codec strings are read from the sample descriptions', () => {
  const movie = parseMovie(moovOf('plain-start.mp4'));
  assert.match(movie.tracks.find((t) => t.kind === 'video').codec, /^avc1\.[0-9A-F]{6}$/);
  assert.equal(movie.tracks.find((t) => t.kind === 'audio').codec, 'mp4a.40.2');
  // The libx265 that made hevc.mp4 writes progressive + frame-only (0x90) in the constraint flags; other builds
  // also set non-packed (0xB0). The string carries whatever byte the file holds, so the exact strings are checked below.
  assert.match(parseMovie(moovOf('hevc.mp4')).tracks.find((t) => t.kind === 'video').codec, /^hvc1\.1\.6\.L\d+\.[0-9A-F]{2}$/);
});

function hevcCodecWith(patch) {
  const moov = moovOf('hevc.mp4').slice();
  const at = moov.findIndex((_, i) => String.fromCharCode(...moov.subarray(i, i + 4)) === 'hvcC') + 4;
  patch(moov, at);
  return parseMovie(moov).tracks.find((t) => t.kind === 'video').codec;
}

test('hvcC becomes the Annex E string: profile, reversed compatibility flags, tier and level, constraint bytes', () => {
  assert.equal(hevcCodecWith(() => {}), 'hvc1.1.6.L30.90');
  assert.equal(hevcCodecWith((moov, at) => { moov[at + 6] = 0xb0; }), 'hvc1.1.6.L30.B0');
  assert.equal(hevcCodecWith((moov, at) => { moov[at + 6] = 0x90; moov[at + 8] = 0x01; }), 'hvc1.1.6.L30.90.00.01');
  assert.equal(hevcCodecWith((moov, at) => { moov.fill(0, at + 6, at + 12); }), 'hvc1.1.6.L30');
  assert.equal(hevcCodecWith((moov, at) => { moov[at + 1] = 0x22; moov[at + 12] = 120; }), 'hvc1.2.6.H120.90');
  assert.equal(hevcCodecWith((moov, at) => { moov[at + 1] = 0x41; }), 'hvc1.A1.6.L30.90');
  // The flags are stored with profile 0 as the most significant bit, so profile 1 and 2 (0x60000000) read 6 reversed.
  assert.equal(hevcCodecWith((moov, at) => { moov.set([0x80, 0, 0, 0], at + 2); }), 'hvc1.1.1.L30.90');
  assert.equal(hevcCodecWith((moov, at) => { moov.set([0, 0, 0, 1], at + 2); }), 'hvc1.1.80000000.L30.90');
});

test('the picture size comes from the video track', () => {
  const video = parseMovie(moovOf('plain-start.mp4')).tracks.find((t) => t.kind === 'video');
  assert.equal(video.width, 160);
  assert.equal(video.height, 90);
});

test('the MIME type names the video and the audio codec', () => {
  const movie = parseMovie(moovOf('plain-start.mp4'));
  const video = movie.tracks.find((t) => t.kind === 'video').codec;
  assert.equal(mimeFor(movie), `video/mp4; codecs="${video},mp4a.40.2"`);
});

test('the duration is the movie header duration', () => {
  assert.ok(Math.abs(parseMovie(moovOf('plain-start.mp4')).duration - 4) < 0.2);
});

test('a fragmented file has no sample table and says it is fragmented', () => {
  const movie = parseMovie(moovOf('fragmented.mp4'));
  assert.equal(movie.fragmented, true);
  assert.ok(movie.tracks.every((t) => t.table === null));
  assert.match(movie.tracks.find((t) => t.kind === 'video').codec, /^avc1\./);
  assert.equal(seekPoint(movie, 1), null);
  assert.equal(parseMovie(moovOf('plain-start.mp4')).fragmented, false);
});

test('a seek past the end of every track has no point', () => {
  const movie = parseMovie(moovOf('plain-start.mp4'));
  assert.equal(seekPoint(movie, 60), null);
});

// A table in decode order with B-frame composition times, the case mp4box's scan is specific about.
function syntheticTrack({ cts, sync, sizes, offsets, endTicks, timescale = 10 }) {
  const count = cts.length;
  return {
    id: 1,
    kind: 'video',
    timescale,
    codec: 'avc1.64001F',
    width: 16,
    height: 16,
    table: {
      count,
      cts: Float64Array.from(cts),
      offsets: Float64Array.from(offsets ?? cts.map((_, i) => 1000 + i * 100)),
      sizes: Uint32Array.from(sizes ?? cts.map(() => 100)),
      sync: sync === null ? null : Uint8Array.from(sync),
      endTicks,
    },
  };
}

test('seekPoint follows mp4box: scan in decode order to the first composition time past the target', () => {
  // Decode order with composition times 2 0 1 | 5 3 4 (ticks of 0.1 s) and sync samples at 0 and 3.
  const track = syntheticTrack({ cts: [2, 0, 1, 5, 3, 4], sync: [1, 0, 0, 1, 0, 0], endTicks: 60 });
  const movie = { timescale: 1000, duration: 6, fragmented: false, tracks: [track] };

  // 0.25 s: sample 3 (cts 5) is past 2.5 ticks, so the scan stops there and the last sync before it is sample 0.
  assert.deepEqual(seekPoint(movie, 0.25), { time: 0.2, offset: 1000 });
  // 0.45 s: sample 3 (cts 5) is past 4.5; same stop.
  assert.deepEqual(seekPoint(movie, 0.45), { time: 0.2, offset: 1000 });
  // 0.55 s: nothing is past 5.5, the whole table is scanned and the last sync sample is sample 3.
  assert.deepEqual(seekPoint(movie, 0.55), { time: 0.5, offset: 1300 });
  // Before everything: the first sample is never tested, so it is still chosen.
  assert.deepEqual(seekPoint(movie, 0), { time: 0.2, offset: 1000 });
  assert.deepEqual(seekPoint(movie, -1), { time: 0.2, offset: 1000 });
});

test('seekPoint takes the least offset and the least time over the tracks that reach the target', () => {
  const video = syntheticTrack({ cts: [0, 10, 20], sync: [1, 0, 1], endTicks: 30, offsets: [5000, 5100, 5200] });
  const audio = { ...syntheticTrack({ cts: [0, 5, 10, 15, 20, 25], sync: null, endTicks: 30, offsets: [100, 200, 300, 400, 500, 600] }), kind: 'audio' };
  const movie = { timescale: 1000, duration: 3, fragmented: false, tracks: [video, audio] };

  // At 1.2 s the video's last sync sample before the stop is sample 0 (cts 0, offset 5000); the audio has every
  // sample as sync and takes the last one at or before 12 ticks (cts 10, offset 300): least time, least offset.
  assert.deepEqual(seekPoint(movie, 1.2), { time: 0, offset: 300 });
  // An audio track that ends at 1.2 s does not reach 2.5 s, so only the video counts.
  const shortAudio = { ...audio, table: { ...audio.table, endTicks: 12 } };
  assert.deepEqual(seekPoint({ ...movie, tracks: [video, shortAudio] }, 2.5), { time: 2, offset: 5200 });
});

test('seekPoint steps over a zero-sized sample for the offset only, as mp4box does', () => {
  const track = syntheticTrack({ cts: [0, 10, 20], sync: null, endTicks: 30, sizes: [100, 0, 100], offsets: [1000, 1100, 1100] });
  const movie = { timescale: 1000, duration: 3, fragmented: false, tracks: [track] };
  // The chosen sample is the one at cts 10 (index 1); it is empty, so the offset is the next sample's.
  assert.deepEqual(seekPoint(movie, 1.5), { time: 1, offset: 1100 });
});

test('a moov of the wrong shape is refused', () => {
  assert.throws(() => parseMovie(new Uint8Array(8)), Error);
  assert.throws(() => parseMovie(moovOf('plain-start.mp4').subarray(0, 40)), Error);
});

// A hand-built moov: a few hundred bytes of boxes per track, however many samples they declare.
function box(type, ...parts) {
  const body = parts.map((p) => (p instanceof Uint8Array ? p : Uint8Array.from(p)));
  const bytes = new Uint8Array(8 + body.reduce((n, p) => n + p.length, 0));
  const view = new DataView(bytes.buffer);
  view.setUint32(0, bytes.length);
  bytes.set(ascii(type), 4);
  let at = 8;
  for (const part of body) { bytes.set(part, at); at += part.length; }
  return bytes;
}

function words(...values) {
  const bytes = new Uint8Array(values.length * 4);
  values.forEach((value, i) => new DataView(bytes.buffer).setUint32(i * 4, value));
  return bytes;
}

function hostileTrack({ id = 1, count, size, chunkOffset = 0, perChunk = count }) {
  const zeros = (n) => new Uint8Array(n);
  const stbl = box('stbl',
    box('stsd', words(0, 1), box('avc1', zeros(78))),
    box('stsz', words(0, size, count)),
    box('stts', words(0, 1, count, 1)),
    box('stsc', words(0, 1, 1, perChunk, 1)),
    box('stco', words(0, 1, chunkOffset)));
  const mdhd = words(0, 0, 0, 1000, 1000, 0);
  const hdlr = new Uint8Array(24); hdlr.set(ascii('vide'), 8);
  const tkhd = new Uint8Array(84); new DataView(tkhd.buffer).setUint32(12, id);
  return box('trak', box('tkhd', tkhd), box('mdia', box('mdhd', mdhd), box('hdlr', hdlr), box('minf', stbl)));
}

function hostileMoov(...tracks) {
  const mvhd = new Uint8Array(100); new DataView(mvhd.buffer).setUint32(12, 1000);
  return box('moov', box('mvhd', mvhd), ...tracks);
}

function measure(action) {
  const before = process.memoryUsage().arrayBuffers;
  const started = performance.now();
  try { action(); } catch (error) { return { error, ms: performance.now() - started, bytes: process.memoryUsage().arrayBuffers - before }; }
  return { error: null, ms: performance.now() - started, bytes: process.memoryUsage().arrayBuffers - before };
}

test('the hand-built moov reads when its counts are honest', () => {
  const movie = parseMovie(hostileMoov(hostileTrack({ count: 1000, size: 5 })), { fileSize: 10_000 });
  assert.equal(movie.tracks[0].table.count, 1000);
  assert.equal(movie.tracks[0].table.offsets[999], 4995);
});

test('a track declaring ten million constant-size samples is refused before anything is allocated', () => {
  const moov = hostileMoov(hostileTrack({ count: 10_000_000, size: 5 }));
  assert.ok(moov.length < 1000);
  for (const options of [undefined, { fileSize: 600 }, { fileSize: 2 ** 40 }]) {
    const { error, ms, bytes } = measure(() => parseMovie(moov, options));
    assert.ok(error instanceof Error, JSON.stringify(options));
    assert.ok(ms < 200 && bytes < 5_000_000, `${ms} ms, ${bytes} bytes`);
  }
});

test('a constant-size count that the file cannot hold is refused', () => {
  const moov = hostileMoov(hostileTrack({ count: 1_500_000, size: 1000 }));
  const { error, ms, bytes } = measure(() => parseMovie(moov, { fileSize: 700 }));
  assert.ok(error instanceof RangeError);
  assert.ok(ms < 200 && bytes < 5_000_000, `${ms} ms, ${bytes} bytes`);
});

test('many tracks that are each under the cap are refused by the total', () => {
  const tracks = [1, 2, 3, 4, 5, 6, 7, 8].map((id) => hostileTrack({ id, count: 1_500_000, size: 1 }));
  const { error, ms, bytes } = measure(() => parseMovie(hostileMoov(...tracks), { fileSize: 2 ** 40 }));
  assert.ok(error instanceof RangeError);
  assert.ok(ms < 200 && bytes < 5_000_000, `${ms} ms, ${bytes} bytes`);
});

test('a sample placed past the end of the file is refused', () => {
  assert.throws(() => parseMovie(hostileMoov(hostileTrack({ count: 10, size: 5, chunkOffset: 90 })), { fileSize: 100 }), RangeError);
  assert.doesNotThrow(() => parseMovie(hostileMoov(hostileTrack({ count: 10, size: 5, chunkOffset: 50 })), { fileSize: 100 }));
});

for (const f of ['plain-start.mp4', 'plain-end.mp4', 'hevc.mp4']) {
  test(`${f}: the real tables fit their own file, and not half of it`, () => {
    const size = read(f).length;
    assert.doesNotThrow(() => parseMovie(moovOf(f), { fileSize: size }));
    assert.throws(() => parseMovie(moovOf(f), { fileSize: size / 2 }), RangeError);
  });
}
