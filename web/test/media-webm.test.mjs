import assert from 'node:assert/strict';
import { test } from 'node:test';
import { cueAt, locateCues, parseCues, parseWebmHead, webmMime } from '../src/element/media/webm.mjs';
import { read, reader } from './media-helpers.mjs';

function hex4(bytes, at) {
  return [...bytes.subarray(at, at + 4)].map((b) => b.toString(16).padStart(2, '0')).join('').toUpperCase();
}

function concat(...parts) {
  const out = new Uint8Array(parts.reduce((sum, part) => sum + part.length, 0));
  let at = 0;
  for (const part of parts) {
    out.set(part, at);
    at += part.length;
  }
  return out;
}

/** An EBML size in the shortest form that holds `length`. */
function sizeOf(length) {
  for (let width = 1; width <= 8; width++) {
    if (length < 2 ** (7 * width) - 1) {
      const out = new Uint8Array(width);
      let rest = length;
      for (let i = width - 1; i >= 0; i--) {
        out[i] = rest % 256;
        rest = Math.floor(rest / 256);
      }
      out[0] |= 0x80 >> (width - 1);
      return out;
    }
  }
  throw new Error('too large');
}

function idBytes(id) {
  const out = [];
  for (let rest = id; rest > 0; rest = Math.floor(rest / 256)) out.unshift(rest % 256);
  return new Uint8Array(out);
}

function element(id, ...body) {
  const data = concat(...body);
  return concat(idBytes(id), sizeOf(data.length), data);
}

function uint(id, value) {
  const bytes = [];
  for (let rest = value; rest > 0 || bytes.length === 0; rest = Math.floor(rest / 256)) bytes.unshift(rest % 256);
  return element(id, new Uint8Array(bytes));
}

function text(id, value) {
  return element(id, new TextEncoder().encode(value));
}

const UNKNOWN_SEGMENT = concat(idBytes(0x18538067), new Uint8Array([0x01, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]));
const EBML_HEADER = element(0x1a45dfa3, text(0x4282, 'webm'));
const CLUSTER = element(0x1f43b675, uint(0xe7, 0));

function trackEntry(codec, video) {
  const parts = [text(0x86, codec)];
  if (video) parts.push(element(0xe0, uint(0xb0, video[0]), uint(0xba, video[1])));
  return element(0xae, ...parts);
}

/** A minimal file: header, unknown-size Segment, the given children, then a Cluster. */
function file(...children) {
  return concat(EBML_HEADER, UNKNOWN_SEGMENT, ...children, CLUSTER);
}

const head = parseWebmHead(read('clip.webm'));

test('the header of a real file: init segment ends at a Cluster, codecs, duration, size', () => {
  const bytes = read('clip.webm');
  assert.equal(hex4(bytes, head.initEnd), '1F43B675');
  assert.deepEqual(head.codecs, ['vp9', 'opus']);
  assert.equal(webmMime(head), 'video/webm; codecs="vp9,opus"');
  assert.ok(Math.abs(head.duration - 4) < 0.2);
  assert.equal(head.timecodeScale, 1_000_000);
  assert.equal(head.width, 160);
  assert.equal(head.height, 90);
  assert.ok(head.segmentStart > 0 && head.segmentStart < head.initEnd);
  assert.ok(head.cuesOffset > head.initEnd);
});

test('the first block alone is enough when the init segment fits in it', () => {
  const first = parseWebmHead(read('clip.webm').subarray(0, 4096));
  assert.deepEqual(first, head);
});

test('cues are found through the seek head and point at clusters', async () => {
  const bytes = read('clip.webm');
  const cues = parseCues(await locateCues(reader(bytes, 4096), 4096, head), head.segmentStart, head.timecodeScale);
  assert.ok(cues.length >= 2);
  for (const c of cues) assert.equal(hex4(bytes, c.offset), '1F43B675');
  assert.equal(cues[cueAt(cues, 2.5)].time <= 2.5, true);
  for (let i = 1; i < cues.length; i++) assert.ok(cues[i].time >= cues[i - 1].time);
});

test('the Cues element is found whatever the block size, even when it straddles blocks', async () => {
  const bytes = read('clip.webm');
  const expected = parseCues(await locateCues(reader(bytes, 4096), 4096, head), head.segmentStart, head.timecodeScale);

  for (const blockBytes of [7, 64, 1000, 4096, bytes.length, bytes.length * 2]) {
    const cuesBytes = await locateCues(reader(bytes, blockBytes), blockBytes, head);
    assert.equal(hex4(cuesBytes, 0), '1C53BB6B', `block ${blockBytes}`);
    assert.deepEqual(parseCues(cuesBytes, head.segmentStart, head.timecodeScale), expected, `block ${blockBytes}`);
  }
});

test('only the blocks holding the Cues are read', async () => {
  const bytes = read('clip.webm');
  const read1 = [];
  const inner = reader(bytes, 4096);
  await locateCues(async (index) => (read1.push(index), inner(index)), 4096, head);
  assert.ok(read1.length <= 2, `read ${read1}`);
  assert.ok(read1.every((index) => index >= Math.floor(head.cuesOffset / 4096)));
});

test('cueAt picks the last cue at or before the time, and the first before the start', () => {
  const cues = [{ time: 0 }, { time: 1 }, { time: 2.5 }, { time: 4 }];
  assert.equal(cueAt(cues, -1), 0);
  assert.equal(cueAt(cues, 0), 0);
  assert.equal(cueAt(cues, 0.99), 0);
  assert.equal(cueAt(cues, 1), 1);
  assert.equal(cueAt(cues, 2.5), 2);
  assert.equal(cueAt(cues, 3.9), 2);
  assert.equal(cueAt(cues, 4), 3);
  assert.equal(cueAt(cues, 1e9), 3);
  assert.equal(cueAt([{ time: 5 }], 1), 0);
  assert.equal(cueAt([], 1), 0);
  assert.equal(cueAt(cues, NaN), 0);
});

test('a live-written file has no cues and still has its init segment', () => {
  const live = parseWebmHead(read('clip-live.webm'));
  assert.equal(live.cuesOffset, null);
  assert.equal(hex4(read('clip-live.webm'), live.initEnd), '1F43B675');
  assert.deepEqual(live.codecs, ['vp9', 'opus']);
});

test('no cues offset means no read', async () => {
  const live = parseWebmHead(read('clip-live.webm'));
  let reads = 0;
  assert.equal(await locateCues(async () => (reads++, new Uint8Array(0)), 4096, live), null);
  assert.equal(reads, 0);
});

test('truncated or foreign bytes are refused with an Error, not a hang', () => {
  assert.throws(() => parseWebmHead(read('clip.webm').subarray(0, 40)), Error);
  assert.throws(() => parseWebmHead(read('plain-start.mp4')), Error);
  assert.throws(() => parseWebmHead(new Uint8Array([0x1a, 0x45, 0xdf, 0xa3, 0xff])), Error);
  assert.throws(() => parseWebmHead(new Uint8Array(0)), Error);
});

test('every prefix of a real header is an Error, never a hang or a wrong answer', () => {
  const bytes = read('clip.webm');
  for (let n = 0; n < head.initEnd; n++) {
    assert.throws(() => parseWebmHead(bytes.subarray(0, n)), Error, `prefix ${n}`);
  }
});

test('codec ids: the four known ones map, anything else passes through raw', () => {
  const tracks = element(
    0x1654ae6b,
    trackEntry('V_VP8', [10, 20]),
    trackEntry('A_VORBIS'),
    trackEntry('V_AV1', [99, 98]),
    trackEntry('A_FLAC'),
  );
  const parsed = parseWebmHead(file(tracks));
  assert.deepEqual(parsed.codecs, ['vp8', 'vorbis', 'V_AV1', 'A_FLAC']);
  assert.equal(parsed.width, 10);
  assert.equal(parsed.height, 20);
  assert.equal(webmMime(parsed), 'video/webm; codecs="vp8,vorbis,V_AV1,A_FLAC"');
});

test('a hostile codec id cannot break out of the MIME parameter', () => {
  const parsed = parseWebmHead(file(element(0x1654ae6b, trackEntry('V_X"; codecs="evil'))));
  assert.equal(parsed.codecs.length, 1);
  assert.doesNotMatch(parsed.codecs[0], /["; ,]/);
  assert.equal(webmMime(parsed).split('"').length, 3);
});

test('defaults: no Info means a millisecond scale and no duration; no Tracks means no codecs', () => {
  const parsed = parseWebmHead(file());
  assert.equal(parsed.timecodeScale, 1_000_000);
  assert.ok(Number.isNaN(parsed.duration));
  assert.deepEqual(parsed.codecs, []);
  assert.equal(parsed.width, 0);
  assert.equal(parsed.cuesOffset, null);
  assert.equal(webmMime(parsed), 'video/webm');
});

test('duration is in timecode-scale ticks, as a 4 or an 8 byte float', () => {
  const f32 = new Uint8Array(4);
  new DataView(f32.buffer).setFloat32(0, 5000);
  const f64 = new Uint8Array(8);
  new DataView(f64.buffer).setFloat64(0, 2500);

  const a = parseWebmHead(file(element(0x1549a966, uint(0x2ad7b1, 1_000_000), element(0x4489, f32))));
  const b = parseWebmHead(file(element(0x1549a966, uint(0x2ad7b1, 2_000_000), element(0x4489, f64))));
  assert.equal(a.duration, 5);
  assert.equal(b.duration, 5);
});

test('the seek head offset is relative to the segment data', () => {
  const seek = element(0x4dbb, element(0x53ab, new Uint8Array([0x1c, 0x53, 0xbb, 0x6b])), uint(0x53ac, 1234));
  const other = element(0x4dbb, element(0x53ab, new Uint8Array([0x15, 0x49, 0xa9, 0x66])), uint(0x53ac, 99));
  const bytes = file(element(0x114d9b74, other, seek));
  const parsed = parseWebmHead(bytes);
  assert.equal(parsed.cuesOffset, parsed.segmentStart + 1234);
  assert.equal(parsed.segmentStart, EBML_HEADER.length + UNKNOWN_SEGMENT.length);
});

test('a known-size Segment is accepted too', () => {
  const body = concat(element(0x1654ae6b, trackEntry('V_VP9', [4, 3])), CLUSTER);
  const bytes = concat(EBML_HEADER, idBytes(0x18538067), sizeOf(body.length), body);
  const parsed = parseWebmHead(bytes);
  assert.deepEqual(parsed.codecs, ['vp9']);
  assert.equal(hex4(bytes, parsed.initEnd), '1F43B675');
});

test('hostile sizes are refused', () => {
  const eightBytes = (...tail) => new Uint8Array([0x01, ...tail]);
  const huge = eightBytes(0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xfe);
  const beyondSafe = eightBytes(0x20, 0, 0, 0, 0, 0, 0);

  // An element ahead of the first Cluster with an unknown size.
  assert.throws(() => parseWebmHead(concat(EBML_HEADER, UNKNOWN_SEGMENT, idBytes(0x1654ae6b), eightBytes(0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff), CLUSTER)), Error);
  // One that claims more than the bytes hold.
  assert.throws(() => parseWebmHead(concat(EBML_HEADER, UNKNOWN_SEGMENT, idBytes(0x1654ae6b), huge, CLUSTER)), Error);
  // One whose size is past what a number holds exactly.
  assert.throws(() => parseWebmHead(concat(EBML_HEADER, UNKNOWN_SEGMENT, idBytes(0x1654ae6b), beyondSafe, CLUSTER)), Error);
  // A child bigger than the master holding it.
  const lying = concat(idBytes(0x1549a966), sizeOf(4), uint(0x2ad7b1, 1_000_000), new Uint8Array([0, 0, 0]));
  assert.throws(() => parseWebmHead(file(lying)), Error);
  // A zero timecode scale would divide the world by nothing.
  assert.throws(() => parseWebmHead(file(element(0x1549a966, uint(0x2ad7b1, 0)))), Error);
  // An integer wider than eight bytes.
  assert.throws(() => parseWebmHead(file(element(0x1549a966, element(0x2ad7b1, new Uint8Array(9))))), Error);
  // A float that is neither four nor eight bytes.
  assert.throws(() => parseWebmHead(file(element(0x1549a966, element(0x4489, new Uint8Array(3))))), Error);
  // A zero size byte and an ID that cannot be one.
  assert.throws(() => parseWebmHead(concat(EBML_HEADER, UNKNOWN_SEGMENT, new Uint8Array([0x00, 0x00, 0x00]))), Error);
  // A Segment that ends before any Cluster appears.
  assert.throws(() => parseWebmHead(concat(EBML_HEADER, idBytes(0x18538067), sizeOf(0))), Error);
  // No Cluster in the bytes at all.
  assert.throws(() => parseWebmHead(concat(EBML_HEADER, UNKNOWN_SEGMENT, element(0xec, new Uint8Array(10)))), Error);
});

test('hostile bytes of every shape end in an Error or a result, never a hang', () => {
  const bytes = read('clip.webm').slice(0, 400);
  let seed = 12345;
  const next = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff);

  for (let round = 0; round < 3000; round++) {
    const mutated = bytes.slice();
    for (let k = 0; k < 3; k++) mutated[next() % mutated.length] = next() & 0xff;
    try {
      parseWebmHead(mutated);
    } catch (error) {
      assert.ok(error instanceof Error);
    }
  }
});

test('locateCues refuses an offset that is not Cues, a size that is absurd and a file that ends early', async () => {
  const bytes = read('clip.webm');

  const wrong = { ...head, cuesOffset: head.initEnd };
  await assert.rejects(locateCues(reader(bytes, 4096), 4096, wrong), Error);

  const past = { ...head, cuesOffset: bytes.length + 10 };
  await assert.rejects(locateCues(reader(bytes, 4096), 4096, past), /end inside/);

  const huge = concat(idBytes(0x1c53bb6b), new Uint8Array([0x01, 0x00, 0x00, 0x00, 0x7f, 0xff, 0xff, 0xff]));
  await assert.rejects(locateCues(reader(huge, 16), 16, { ...head, cuesOffset: 0 }), Error);

  const open = concat(idBytes(0x1c53bb6b), new Uint8Array([0xff]), new Uint8Array(20));
  await assert.rejects(locateCues(reader(open, 16), 16, { ...head, cuesOffset: 0 }), Error);

  const cut = bytes.subarray(0, head.cuesOffset + 20);
  await assert.rejects(locateCues(reader(cut, 4096), 4096, head), Error);
});

test('a Cues element that is the last bytes of the file, shorter than a header read, is found', async () => {
  const tiny = element(0x1c53bb6b);
  const bytes = concat(new Uint8Array(10), tiny);
  const found = await locateCues(reader(bytes, 8), 8, { ...head, cuesOffset: 10 });
  assert.deepEqual([...found], [...tiny]);
  assert.deepEqual(parseCues(found, 0, 1_000_000), []);
});

test('parseCues: positions are relative to the segment, times to the scale, the first track position wins', () => {
  const point = (time, ...positions) =>
    element(0xbb, uint(0xb3, time), ...positions.map(([track, cluster]) => element(0xb7, uint(0xf7, track), uint(0xf1, cluster))));
  const cues = element(
    0x1c53bb6b,
    point(2000, [1, 500], [2, 900]),
    point(0, [1, 100]),
    element(0xbb, uint(0xb3, 5)),
    element(0xec, new Uint8Array(3)),
  );
  const parsed = parseCues(cues, 1000, 1_000_000);
  assert.deepEqual(parsed, [
    { time: 0, offset: 1100 },
    { time: 2, offset: 1500 },
  ]);
  assert.deepEqual(parseCues(cues, 0, 500_000).map((c) => c.time), [0, 1]);
});

test('parseCues refuses bytes that are not a complete Cues element', () => {
  assert.throws(() => parseCues(new Uint8Array(0), 0, 1_000_000), Error);
  assert.throws(() => parseCues(element(0x1549a966), 0, 1_000_000), Error);
  const real = read('clip.webm');
  const cuesBytes = real.subarray(head.cuesOffset, head.cuesOffset + 30);
  assert.throws(() => parseCues(cuesBytes, head.segmentStart, 1_000_000), Error);
  assert.throws(() => parseCues(concat(idBytes(0x1c53bb6b), new Uint8Array([0xff])), 0, 1_000_000), Error);
  const lying = concat(idBytes(0x1c53bb6b), sizeOf(8), idBytes(0xbb), sizeOf(100), new Uint8Array(6));
  assert.throws(() => parseCues(lying, 0, 1_000_000), Error);
});
