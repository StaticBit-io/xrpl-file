import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { children, find, topLevelBoxes } from '../src/element/media/boxes.mjs';
import { MP4BOX_INTEGRITY, loadMp4Box as loadRealMp4Box, mp4boxUrl } from '../src/element/media/mp4box-loader.mjs';
import { openFeed } from '../src/element/media/feeds.mjs';
import { parseMovie, seekPoint } from '../src/element/media/movie.mjs';
import { parseWebmHead } from '../src/element/media/webm.mjs';
import { ascii, moovOf, read } from './media-helpers.mjs';
import { loadMp4Box, mp4box } from './mp4box.mjs';

const BLOCK = 4096;

/** A file as blocks, with the list of blocks asked for, in order. */
function blocksOf(bytes, blockBytes = BLOCK) {
  const reads = [];
  return {
    bytes,
    blockBytes,
    size: bytes.length,
    blocks: Math.ceil(bytes.length / blockBytes),
    reads,
    head: bytes.slice(0, blockBytes),
    block: (index) => bytes.slice(index * blockBytes, (index + 1) * blockBytes),
    readBlock: async (index) => {
      reads.push(index);
      return bytes.slice(index * blockBytes, (index + 1) * blockBytes);
    },
  };
}

function open(kind, source, extra = {}) {
  return openFeed({
    kind,
    head: source.head,
    readBlock: source.readBlock,
    blockBytes: source.blockBytes,
    size: source.size,
    loadMp4Box,
    ...extra,
  });
}

function view(bytes) {
  return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
}

/** The `moof` boxes of a segment, each with its `traf` children. */
function fragments(segment) {
  return topLevelBoxes(segment.bytes)
    .filter((box) => box.type === 'moof')
    .map((moof) => {
      const traf = find(segment.bytes, moof, 'traf');
      const parts = children(segment.bytes, traf);
      return { trun: parts.find((box) => box.type === 'trun'), tfdt: parts.find((box) => box.type === 'tfdt') };
    });
}

/** Samples in a segment: the sum of the `trun` sample counts of its fragments. */
function countSamples(segments) {
  let samples = 0;
  for (const segment of segments) {
    for (const { trun } of fragments(segment)) samples += view(segment.bytes).getUint32(trun.start + 12);
  }
  return samples;
}

/** Decode time of the segment's first fragment, in seconds. */
function tfdtSeconds(segment, timescale) {
  const { tfdt } = fragments(segment)[0];
  const data = view(segment.bytes);
  const ticks = segment.bytes[tfdt.start + 8] === 1 ? Number(data.getBigUint64(tfdt.start + 12)) : data.getUint32(tfdt.start + 12);
  return ticks / timescale;
}

function boxAt(bytes, offset) {
  return ascii(bytes.subarray(offset + 4, offset + 8));
}

/** Samples of the tracks a player segments: a timecode or metadata track is not part of the count. */
function samplesInTables(movie) {
  return movie.tracks.filter((track) => track.kind !== 'other').reduce((sum, track) => sum + track.table.count, 0);
}

/** Every block from the seek answer on, through `media`, as the player does. */
async function playFrom(feed, source, target, { blocks = source.blocks } = {}) {
  const segments = [];
  for (let block = target.block; block < blocks; block++) {
    segments.push(...await feed.media(block, source.block(block), block === target.block ? target.offset : undefined));
  }
  return segments;
}

for (const name of ['plain-start.mp4', 'plain-end.mp4']) {
  test(`${name}: playing from seekTo(0) cuts every sample into segments`, async () => {
    const source = blocksOf(read(name));
    const movie = parseMovie(moovOf(name), { fileSize: source.size });
    const feed = await open('iso', source);

    assert.equal(feed.kind, 'mp4');
    assert.equal(feed.seekable, true);
    assert.equal(feed.duration, 4);
    assert.deepEqual(feed.tracks.map((t) => t.mime.split(';')[0]).sort(), ['audio/mp4', 'video/mp4']);
    assert.deepEqual(feed.tracks.map((t) => t.mime).sort(), ['audio/mp4; codecs="mp4a.40.2"', 'video/mp4; codecs="avc1.64000a"']);

    const init = await feed.init();
    assert.equal(init.length, 2);
    assert.deepEqual(init.map((s) => s.track).sort(), feed.tracks.map((t) => t.id).sort());
    for (const segment of init) assert.deepEqual([boxAt(segment.bytes, 0), boxAt(segment.bytes, boxLength(segment.bytes))], ['ftyp', 'moov']);

    const start = await feed.seekTo(0);
    const segments = await playFrom(feed, source, start);

    assert.equal(countSamples(segments), samplesInTables(movie));
    for (const track of feed.tracks) assert.ok(segments.some((s) => s.track === track.id));
  });
}

function boxLength(bytes) {
  return view(bytes).getUint32(0);
}

test('plain MP4 with moov at the end is opened from the blocks that hold moov, block 0 coming from head', async () => {
  const source = blocksOf(read('plain-end.mp4'));
  const moovStart = topLevelBoxes(source.bytes).find((box) => box.type === 'moov');
  const moovBlocks = [];
  for (let b = Math.floor(moovStart.start / BLOCK); b <= Math.floor((moovStart.end - 1) / BLOCK); b++) moovBlocks.push(b);
  assert.ok(moovBlocks.length > 0 && !moovBlocks.includes(0));

  const feed = await open('iso', source);
  assert.equal(feed.kind, 'mp4');
  await feed.init();
  await feed.seekTo(0);

  assert.deepEqual(source.reads, moovBlocks);
});

test('a head that is not all of block 0 is read again rather than trusted', async () => {
  const source = blocksOf(read('plain-end.mp4'));
  const feed = await open('iso', source, { head: source.head.subarray(0, 100) });

  assert.equal(feed.kind, 'mp4');
  assert.equal(source.reads[0], 0);
});

for (const name of ['plain-start.mp4', 'plain-end.mp4']) {
  test(`${name}: a seek feeds both tracks from the keyframe at or before the target`, async () => {
    const source = blocksOf(read(name));
    const feed = await open('iso', source);
    const video = feed.tracks.find((t) => t.mime.startsWith('video'));
    const audio = feed.tracks.find((t) => t.mime.startsWith('audio'));

    const target = await feed.seekTo(2.5);
    assert.ok(target.time <= 2.5);
    assert.ok(target.time > 1.5, `a keyframe a second away is not "at or before 2.5": ${target.time}`);
    assert.equal(target.block, Math.floor(target.offset / BLOCK));

    const segments = [];
    for (let b = target.block; b < source.blocks && segments.length < 4; b++) {
      segments.push(...await feed.media(b, source.block(b), b === target.block ? target.offset : undefined));
    }

    const firstVideo = segments.find((s) => s.track === video.id);
    const firstAudio = segments.find((s) => s.track === audio.id);
    assert.ok(firstVideo && firstAudio);
    assert.ok(tfdtSeconds(firstVideo, 10240) <= 2.5);
    assert.ok(tfdtSeconds(firstAudio, 48000) <= 2.5);
    assert.ok(tfdtSeconds(firstVideo, 10240) > 1.5);
  });
}

/** An mp4box `ISOFile` that holds the movie header of a file and none of its media data. */
function mp4boxOver(bytes, moovEnd) {
  const file = mp4box().createFile();
  let tracks = null;
  file.onReady = (info) => { tracks = info.tracks; };
  const copy = bytes.slice(0, moovEnd).buffer;
  copy.fileStart = 0;
  file.appendBuffer(copy);
  assert.ok(tracks, 'mp4box read the movie header');

  return { file, tracks };
}

/**
 * The rule the player needs, written against mp4box's own `seekTrack`: the least offset and time
 * over the video and audio tracks that reach `t`. (`ISOFile.seek` does the same over every track.)
 */
function playedTracksSeek({ file, tracks }, t) {
  let offset = Infinity;
  let time = Infinity;
  for (const info of tracks.filter((track) => track.type === 'video' || track.type === 'audio')) {
    const trak = file.getTrackById(info.id);
    if (t > file.getTrackDuration(trak)) continue;
    const found = file.seekTrack(t, true, trak);
    offset = Math.min(offset, found.offset);
    time = Math.min(time, found.time);
  }
  return { offset, time };
}

for (const name of ['plain-start.mp4', 'plain-end.mp4', 'timecode.mov']) {
  test(`${name}: seekPoint agrees with mp4box's seekTrack over the video and audio tracks`, () => {
    // Only the movie header is given to mp4box, and seekTrack does not look at buffers at all.
    const bytes = read(name);
    const moov = topLevelBoxes(bytes).find((box) => box.type === 'moov');
    const movie = parseMovie(bytes.subarray(moov.start, moov.end), { fileSize: bytes.length });
    const over = mp4boxOver(bytes, moov.end);

    for (const t of [0, 0.4, 1.3, 2.5, 2.95, 3.5, 3.9, 4]) {
      assert.deepEqual(seekPoint(movie, t), playedTracksSeek(over, t), `at ${t} s`);
    }
  });
}

test('mp4box own seek counts a timecode track, which is why the feed does not use it', () => {
  const bytes = read('timecode.mov');
  const moov = topLevelBoxes(bytes).find((box) => box.type === 'moov');
  const over = mp4boxOver(bytes, moov.end);

  assert.ok(over.tracks.some((track) => track.type !== 'video' && track.type !== 'audio'));

  // What seek() does before it looks at buffers: the least offset over every track.
  const every = over.tracks.map((info) => over.file.seekTrack(3.5, true, over.file.getTrackById(info.id)).offset);
  assert.ok(Math.min(...every) < 100);
  assert.ok(playedTracksSeek(over, 3.5).offset > 40000);
});

test('a timecode track does not pull a seek back to the start of the file', async () => {
  const source = blocksOf(read('timecode.mov'));
  const movie = parseMovie(moovOf('timecode.mov'), { fileSize: source.size });
  const feed = await open('iso', source);

  assert.equal(feed.tracks.length, 2);
  assert.deepEqual(feed.tracks.map((t) => t.mime).sort(), ['audio/mp4; codecs="mp4a.40.2"', 'video/mp4; codecs="avc1.64000a"']);

  const point = seekPoint(movie, 3.5);
  assert.ok(point.offset > source.size * 0.6, `seekPoint reads from ${point.offset} of ${source.size}`);
  assert.ok(point.time > 3 && point.time <= 3.5);

  const target = await feed.seekTo(3.5);
  assert.ok(target.block >= 9 && target.block > 1, `seekTo starts at block ${target.block}`);
  assert.equal(target.offset, point.offset);
  assert.equal(target.time, point.time);

  const segments = await playFrom(feed, source, target);
  // A decode time: the keyframe's composition time (3.2) less the two frames of B-frame delay.
  assert.ok(tfdtSeconds(segments.find((s) => s.track === 1), 10240) > 2.5);
  assert.equal(await (async () => countSamples(await playFrom(feed, source, await feed.seekTo(0))))(), samplesInTables(movie));
});

test('after a seek into the middle, every track gets exactly the samples from its starting sample on', async () => {
  const source = blocksOf(read('plain-start.mp4'));
  const movie = parseMovie(moovOf('plain-start.mp4'), { fileSize: source.size });
  const feed = await open('iso', source);

  const t = 2.5;
  const target = await feed.seekTo(t);
  const segments = await playFrom(feed, source, target);

  for (const track of movie.tracks) {
    const { table } = track;
    // The scan seekPoint documents: in decode order up to the first sample past t, the last sync sample before it.
    let from = 0;
    for (let i = 0; i < table.count; i++) {
      if (i > 0 && table.cts[i] > t * track.timescale) break;
      if (table.sync === null || table.sync[i] === 1) from = i;
    }

    const own = segments.filter((segment) => segment.track === track.id);
    assert.equal(countSamples(own), table.count - from, `track ${track.id}`);
    assert.ok(from > 0);
  }
});

test('the feed answers with mp4box\'s position, which skips data mp4box already holds', async () => {
  const source = blocksOf(read('plain-end.mp4'));
  const movie = parseMovie(moovOf('plain-end.mp4'), { fileSize: source.size });
  const feed = await open('iso', source);

  // The first sample is at byte 48, inside block 0, which the feed has handed mp4box: reading resumes after it.
  assert.equal(seekPoint(movie, 0).offset, 48);
  assert.deepEqual(await feed.seekTo(0), { block: 1, offset: BLOCK, time: 0 });
});

test('a second seek starts afresh: nothing of the first one is left over', async () => {
  const source = blocksOf(read('plain-start.mp4'));
  const movie = parseMovie(moovOf('plain-start.mp4'), { fileSize: source.size });
  const feed = await open('iso', source);

  const first = await feed.seekTo(2.5);
  await feed.media(first.block, source.block(first.block), first.offset);
  await feed.media(first.block + 1, source.block(first.block + 1));

  const start = await feed.seekTo(0);
  const segments = await playFrom(feed, source, start);

  assert.equal(countSamples(segments), samplesInTables(movie));
  for (const segment of segments.filter((s) => s.track === feed.tracks[0].id).slice(0, 1)) {
    assert.equal(tfdtSeconds(segment, 10240), 0);
  }
});

test('seeking forward and then back reads the same samples as seeking once', async () => {
  const source = blocksOf(read('plain-end.mp4'));
  const feed = await open('iso', source);

  const forward = await feed.seekTo(3.9);
  assert.ok(forward.time <= 3.9 && forward.time > 3);
  const later = await playFrom(feed, source, forward);

  const back = await feed.seekTo(1.3);
  const earlier = await playFrom(feed, source, back);

  assert.ok(countSamples(earlier) > countSamples(later));
  assert.ok(back.time <= 1.3);
});

test('a seek past the end is clamped to the last position the file has', async () => {
  const source = blocksOf(read('plain-start.mp4'));
  const feed = await open('iso', source);

  const target = await feed.seekTo(1e6);
  assert.ok(target.time > 3 && target.time <= 4);
  assert.ok(target.offset < source.size);
  assert.deepEqual(await feed.seekTo(-5), await feed.seekTo(0));
  assert.deepEqual(await feed.seekTo(Number.NaN), await feed.seekTo(0));
});

test('a file smaller than one block still plays: the answer stays inside the file and the last block flushes', async () => {
  const source = blocksOf(read('plain-start.mp4'), 1 << 20);
  const movie = parseMovie(moovOf('plain-start.mp4'), { fileSize: source.size });
  const feed = await open('iso', source);

  const start = await feed.seekTo(0);
  assert.equal(start.block, 0);
  assert.ok(start.offset <= source.size);

  const segments = await playFrom(feed, source, start);
  assert.equal(countSamples(segments), samplesInTables(movie));
});

test('a box between the media data and moov does not stop mp4box reading the moov', async () => {
  const original = read('plain-end.mp4');
  const moov = topLevelBoxes(original).find((box) => box.type === 'moov');
  const padding = 1000;

  // plain-end.mp4 with a free box of 1000 bytes in front of the moov: no sample offset changes.
  const bytes = new Uint8Array(original.length + padding);
  bytes.set(original.subarray(0, moov.start));
  view(bytes).setUint32(moov.start, padding);
  bytes.set(ascii('free'), moov.start + 4);
  bytes.set(original.subarray(moov.start), moov.start + padding);

  const source = blocksOf(bytes);
  const movie = parseMovie(moovOf('plain-end.mp4'), { fileSize: source.size });
  const feed = await open('iso', source);

  assert.equal(feed.kind, 'mp4');
  const segments = await playFrom(feed, source, await feed.seekTo(0));
  assert.equal(countSamples(segments), samplesInTables(movie));

  const target = await feed.seekTo(2.5);
  assert.deepEqual(target, { block: 6, offset: 27356, time: 2.2 });
});

test('a sidx that claims more bytes than the file has still answers inside the file', async () => {
  const bytes = read('fragmented-sidx.mp4');
  const sidx = topLevelBoxes(bytes).find((box) => box.type === 'sidx');
  const first = sidx.start + (bytes[sidx.start + 8] === 1 ? 40 : 32);
  view(bytes).setUint32(first, 0x7fffffff);

  const source = blocksOf(bytes);
  const feed = await open('iso', source);
  const target = await feed.seekTo(3.9);

  assert.equal(target.offset, source.size);
  assert.equal(target.block, source.blocks - 1);
  assert.deepEqual(await feed.media(target.block, source.block(target.block), target.offset), []);
});

test('HEVC in a plain MP4 is reported with its codec, for the caller to ask isTypeSupported about', async () => {
  const feed = await open('iso', blocksOf(read('hevc.mp4')));

  assert.equal(feed.kind, 'mp4');
  assert.ok(feed.tracks.some((t) => t.mime.startsWith('video/mp4; codecs="hvc1.')));
});

test('mp4box is loaded once per feed and the feed works after close() only as an empty one', async () => {
  const before = loadMp4Box.calls;
  const source = blocksOf(read('plain-start.mp4'));
  const feed = await open('iso', source);
  const target = await feed.seekTo(1.3);
  await feed.seekTo(2.5);
  assert.equal(loadMp4Box.calls, before + 1);

  feed.close();
  assert.deepEqual(await feed.media(target.block, source.block(target.block), target.offset), []);
  assert.equal(await feed.seekTo(0), null);
});

test('a plain MP4 without loadMp4Box is a programming error, not a silent failure', async () => {
  const source = blocksOf(read('plain-start.mp4'));
  await assert.rejects(() => open('iso', source, { loadMp4Box: undefined }), TypeError);
});

test('fragmented MP4 with sidx: init ends where the first moof starts, seeks land on a moof', async () => {
  const source = blocksOf(read('fragmented-sidx.mp4'));
  const calls = loadMp4Box.calls;
  const feed = await open('iso', source);

  assert.equal(feed.kind, 'fmp4');
  assert.equal(feed.seekable, true);
  assert.ok(feed.duration > 4);
  assert.equal(feed.tracks.length, 1);
  assert.equal(feed.tracks[0].id, 0);

  const [init] = await feed.init();
  assert.equal(init.track, 0);
  assert.equal(boxAt(source.bytes, init.bytes.length), 'moof');
  assert.equal(boxAt(init.bytes, 0), 'ftyp');
  assert.deepEqual(init.bytes, source.bytes.subarray(0, init.bytes.length));

  const target = await feed.seekTo(2.5);
  assert.equal(boxAt(source.bytes, target.offset), 'moof');
  assert.ok(target.time <= 2.5);
  assert.ok(target.time > 1.5);
  assert.equal(target.block, Math.floor(target.offset / BLOCK));

  const start = await feed.seekTo(0);
  assert.equal(start.offset, init.bytes.length);
  assert.equal(loadMp4Box.calls, calls);
});

test('fragmented MP4: media hands over the block from the offset on, as one segment', async () => {
  const source = blocksOf(read('fragmented-sidx.mp4'));
  const feed = await open('iso', source);
  const target = await feed.seekTo(2.5);

  const [first] = await feed.media(target.block, source.block(target.block), target.offset);
  assert.equal(first.track, 0);
  assert.deepEqual(first.bytes, source.bytes.subarray(target.offset, (target.block + 1) * BLOCK));

  const [next] = await feed.media(target.block + 1, source.block(target.block + 1));
  assert.deepEqual(next.bytes, source.block(target.block + 1));

  assert.deepEqual(await feed.media(target.block, source.block(target.block), (target.block + 1) * BLOCK), []);
});

test('fragmented MP4 without sidx is not seekable outside the buffer', async () => {
  const source = blocksOf(read('fragmented.mp4'));
  const feed = await open('iso', source);

  assert.equal(feed.kind, 'fmp4');
  assert.equal(feed.seekable, false);
  assert.equal(await feed.seekTo(2), null);

  const [init] = await feed.init();
  assert.equal(boxAt(source.bytes, init.bytes.length), 'moof');
  assert.deepEqual(await feed.seekTo(0), { block: Math.floor(init.bytes.length / BLOCK), offset: init.bytes.length, time: 0 });
});

test('WebM with Cues seeks to a cluster; without Cues it does not', async () => {
  const source = blocksOf(read('clip.webm'));
  const withCues = await open('webm', source);

  assert.equal(withCues.kind, 'webm');
  assert.equal(withCues.seekable, true);
  assert.equal(withCues.duration, 4.015);
  assert.deepEqual(withCues.tracks, [{ id: 0, mime: 'video/webm; codecs="vp9,opus"' }]);

  const target = await withCues.seekTo(2.5);
  assert.deepEqual([...source.bytes.subarray(target.offset, target.offset + 4)], [0x1f, 0x43, 0xb6, 0x75]);
  assert.ok(target.time <= 2.5);
  assert.ok(target.time > 1.5);
  assert.equal(target.block, Math.floor(target.offset / BLOCK));

  const head = parseWebmHead(source.head);
  const [init] = await withCues.init();
  assert.equal(init.bytes.length, head.initEnd);
  assert.deepEqual(await withCues.seekTo(0), { block: Math.floor(head.initEnd / BLOCK), offset: head.initEnd, time: 0 });

  const liveSource = blocksOf(read('clip-live.webm'));
  const live = await open('webm', liveSource);
  assert.equal(live.seekable, false);
  assert.equal(await live.seekTo(2.5), null);
  assert.equal((await live.seekTo(0)).offset, parseWebmHead(liveSource.head).initEnd);
  assert.deepEqual(liveSource.reads, []);
});

test('WebM: only the block that holds the Cues is read to open', async () => {
  const source = blocksOf(read('clip.webm'));
  const head = parseWebmHead(source.head);
  await open('webm', source);

  assert.deepEqual(source.reads, [Math.floor(head.cuesOffset / BLOCK)]);
});

/** `clip.webm` with its Cues replaced by an empty Cues element that is the last five bytes of a whole number of blocks. */
function webmEndingInTinyCues() {
  const original = read('clip.webm');
  const head = parseWebmHead(original);
  const emptyCues = [0x1c, 0x53, 0xbb, 0x6b, 0x80];

  const room = BLOCK - ((head.cuesOffset + emptyCues.length) % BLOCK);
  const padding = room === BLOCK ? 0 : room;
  // A Void element fills the gap: ID EC, then a two-byte size.
  const voidBytes = padding === 0 ? [] : padding < 3 ? null : [0xec, 0x40 | ((padding - 3) >> 8), (padding - 3) & 0xff, ...new Uint8Array(padding - 3)];
  assert.ok(voidBytes !== null || padding === 0);

  const bytes = new Uint8Array(head.cuesOffset + padding + emptyCues.length);
  bytes.set(original.subarray(0, head.cuesOffset));
  if (voidBytes) bytes.set(voidBytes, head.cuesOffset);
  bytes.set(emptyCues, head.cuesOffset + padding);

  // The SeekHead's position for Cues counts from the Segment's data.
  const stored = head.cuesOffset - head.segmentStart;
  const wanted = head.cuesOffset + padding - head.segmentStart;
  const seekHead = bytes.subarray(0, head.initEnd);
  let patched = false;
  for (let i = 0; i + 3 < seekHead.length && !patched; i++) {
    if (seekHead[i] !== 0x53 || seekHead[i + 1] !== 0xac) continue;
    const length = seekHead[i + 2] & 0x7f;
    let value = 0;
    for (let k = 0; k < length; k++) value = value * 256 + seekHead[i + 3 + k];
    if (value !== stored) continue;
    for (let k = length - 1, rest = wanted; k >= 0; k--, rest = Math.floor(rest / 256)) seekHead[i + 3 + k] = rest % 256;
    patched = true;
  }
  assert.ok(patched, 'the SeekHead entry for Cues was found');

  return bytes;
}

test('WebM whose Cues end exactly at a block boundary: no block past the last one is read', async () => {
  const bytes = webmEndingInTinyCues();
  assert.equal(bytes.length % BLOCK, 0);

  const source = blocksOf(bytes);
  const beyond = [];
  const feed = await openFeed({
    kind: 'webm',
    head: source.head,
    blockBytes: BLOCK,
    size: source.size,
    readBlock: async (index) => {
      if (index >= source.blocks) beyond.push(index);
      return source.readBlock(index);
    },
  });

  // The empty Cues element is well-formed and has no cue points: the file is playable, not seekable.
  assert.deepEqual(beyond, []);
  assert.equal(feed.kind, 'webm');
  assert.equal(feed.seekable, false);
  assert.deepEqual(source.reads, [source.blocks - 1]);
});

test('a block that cannot be read is an error of the open, not a file without Cues', async () => {
  const source = blocksOf(read('clip.webm'));
  const failure = new RangeError('the node is gone');

  await assert.rejects(
    () => openFeed({ kind: 'webm', head: source.head, blockBytes: BLOCK, size: source.size, readBlock: async () => { throw failure; } }),
    (error) => error === failure,
  );
});

test('openFeed refuses what it cannot work with', async () => {
  const source = blocksOf(read('plain-start.mp4'));

  await assert.rejects(() => open('unknown', source), TypeError);
  await assert.rejects(() => open('iso', source, { blockBytes: 0 }), TypeError);
  await assert.rejects(() => open('iso', source, { size: 0 }), TypeError);
});

test('mp4boxUrl', () => {
  assert.equal(mp4boxUrl('https://cdn.example/1.0.0/xrpl-file-element.js'), 'https://cdn.example/1.0.0/mp4box.min.js');
  assert.equal(mp4boxUrl('https://cdn.example/1.0.0/xrpl-file-element.js?v=3#x'), 'https://cdn.example/1.0.0/mp4box.min.js');
  assert.equal(mp4boxUrl('http://127.0.0.1:8790/src/element/media/mp4box-loader.mjs'), 'http://127.0.0.1:8790/vendor/mp4box-0.5.4.min.js');
  assert.throws(() => mp4boxUrl('blob:https://example.org/3f2a'), TypeError);
  assert.throws(() => mp4boxUrl('not a url'), TypeError);
});

/** A page, as far as the loader looks at one. */
function fakePage() {
  const scripts = [];
  const document = {
    head: { append: (script) => scripts.push(script) },
    createElement: (tag) => ({ tag, remove() { this.removed = true; } }),
  };
  return { scripts, document };
}

async function withPage(run) {
  const saved = { document: globalThis.document, own: Object.hasOwn(globalThis, 'MP4Box'), mp4box: globalThis.MP4Box };
  const page = fakePage();
  globalThis.document = page.document;
  try {
    await run(page);
  } finally {
    if (saved.document === undefined) delete globalThis.document;
    else globalThis.document = saved.document;
    if (saved.own) globalThis.MP4Box = saved.mp4box;
    else delete globalThis.MP4Box;
  }
}

test('loadMp4Box: one script with crossorigin and integrity, the host page\'s own MP4Box put back', async () => {
  await withPage(async ({ scripts }) => {
    const hosts = { createFile: () => 'host copy' };
    const ours = { createFile: () => 'our copy' };
    globalThis.MP4Box = hosts;

    const url = 'https://cdn.example/one/element.js';
    const first = loadRealMp4Box({ scriptUrl: url, integrity: 'sha384-abc' });
    const second = loadRealMp4Box({ scriptUrl: url, integrity: 'sha384-abc' });

    assert.equal(scripts.length, 1);
    assert.equal(scripts[0].src, 'https://cdn.example/one/mp4box.min.js');
    assert.equal(scripts[0].crossOrigin, 'anonymous');
    assert.equal(scripts[0].integrity, 'sha384-abc');

    globalThis.MP4Box = ours;
    scripts[0].onload();

    assert.equal(await first, ours);
    assert.equal(await second, ours);
    assert.equal(globalThis.MP4Box, hosts);

    assert.equal(await loadRealMp4Box({ scriptUrl: url, integrity: 'sha384-abc' }), ours);
    assert.equal(scripts.length, 1);
  });
});

test('loadMp4Box: the build placeholder is not sent as an integrity value, and a page without MP4Box is left without it', async () => {
  await withPage(async ({ scripts }) => {
    assert.equal(MP4BOX_INTEGRITY, '__MP4BOX_INTEGRITY__');
    delete globalThis.MP4Box;

    const loaded = loadRealMp4Box({ scriptUrl: 'https://cdn.example/two/element.js' });
    assert.equal('integrity' in scripts[0], false);

    const ours = { createFile: () => 'ours' };
    globalThis.MP4Box = ours;
    scripts[0].onload();

    assert.equal(await loaded, ours);
    assert.equal(Object.hasOwn(globalThis, 'MP4Box'), false);
  });
});

test('the build placeholder occurs once in the loader, so replacing it cannot turn the check off', async () => {
  const source = readFileSync(new URL('../src/element/media/mp4box-loader.mjs', import.meta.url), 'utf8');
  assert.equal(source.split(MP4BOX_INTEGRITY).length - 1, 1);

  // Once the build has written a real hash into the export, it is still passed on.
  await withPage(async ({ scripts }) => {
    const hash = 'sha384-' + 'A'.repeat(64);
    const loaded = loadRealMp4Box({ scriptUrl: 'https://cdn.example/six/element.js', integrity: hash });
    assert.equal(scripts[0].integrity, hash);

    globalThis.MP4Box = { createFile: () => 'ours' };
    scripts[0].onload();
    await loaded;
  });
});

test('loadMp4Box silences the parser error log, which only complains about QuickTime layouts', async () => {
  await withPage(async ({ scripts }) => {
    const logged = [];
    const warned = [];
    const savedLog = globalThis.Log;
    // What the script leaves behind: a global Log next to MP4Box.
    globalThis.Log = { getDurationString: () => '', error: (...args) => logged.push(args), warn: (...args) => warned.push(args) };

    try {
      const loaded = loadRealMp4Box({ scriptUrl: 'https://cdn.example/seven/element.js' });
      globalThis.MP4Box = { createFile: () => 'ours' };
      scripts[0].onload();
      await loaded;

      globalThis.Log.error('BoxParser', 'a box could not be read');
      globalThis.Log.warn('ISOFile', 'something else');
      assert.deepEqual(logged, []);
      assert.equal(warned.length, 1, 'only the error log is silenced');
    } finally {
      if (savedLog === undefined) delete globalThis.Log;
      else globalThis.Log = savedLog;
    }
  });
});

test('loadMp4Box: a failed load rejects, is not remembered, and takes its script away', async () => {
  await withPage(async ({ scripts }) => {
    const url = 'https://cdn.example/three/element.js';
    const failed = loadRealMp4Box({ scriptUrl: url });
    scripts[0].onerror();

    await assert.rejects(failed, /could not be loaded/);
    assert.equal(scripts[0].removed, true);

    const ours = { createFile: () => 'ours' };
    const again = loadRealMp4Box({ scriptUrl: url });
    assert.equal(scripts.length, 2);
    globalThis.MP4Box = ours;
    scripts[1].onload();
    assert.equal(await again, ours);
  });
});

test('loadMp4Box: a script that defines no MP4Box is an error', async () => {
  await withPage(async ({ scripts }) => {
    const loaded = loadRealMp4Box({ scriptUrl: 'https://cdn.example/four/element.js' });
    delete globalThis.MP4Box;
    scripts[0].onload();

    await assert.rejects(loaded, /did not define MP4Box/);
  });
});

test('loadMp4Box outside a page rejects instead of throwing', async () => {
  const saved = globalThis.document;
  delete globalThis.document;
  try {
    await assert.rejects(loadRealMp4Box({ scriptUrl: 'https://cdn.example/five/element.js' }), /into a page/);
  } finally {
    if (saved !== undefined) globalThis.document = saved;
  }
});

const loaderUrl = new URL('../src/element/media/mp4box-loader.mjs', import.meta.url).href;

/**
 * Runs a page script in a fresh node process and returns what it printed as JSON. A classic script's
 * top-level `var MP4Box` makes a global property that can neither be deleted nor be left behind by a
 * test, so these cases each get their own global object.
 */
function inFreshGlobal(body) {
  const code = `
    import { loadMp4Box } from ${JSON.stringify(loaderUrl)};
    const scripts = [];
    globalThis.document = { head: { append: (script) => scripts.push(script) }, createElement: () => ({ remove() {} }) };
    const ours = { createFile: () => 'ours' };
    // What a classic script's top-level var does to the global object.
    const declare = (name, value) => Object.defineProperty(globalThis, name, { value, writable: true, configurable: false, enumerable: true });
    ${body}
  `;
  const out = execFileSync(process.execPath, ['--input-type=module', '-e', code], { encoding: 'utf8' });
  return JSON.parse(out);
}

test('loadMp4Box resolves although the script\'s global MP4Box cannot be deleted', () => {
  const result = inFreshGlobal(`
    const loading = loadMp4Box({ scriptUrl: 'https://cdn.example/a/element.js' });
    declare('MP4Box', ours);
    declare('Log', { getDurationString: () => '', error: () => { throw new Error('logged'); } });
    scripts[0].onload();
    const got = await loading;
    globalThis.Log.error();
    console.log(JSON.stringify({ resolved: got === ours, silenced: true }));
  `);

  assert.deepEqual(result, { resolved: true, silenced: true });
});

test('loadMp4Box puts a host page\'s MP4Box back when that global cannot be deleted either', () => {
  const result = inFreshGlobal(`
    const host = { createFile: () => 'host' };
    declare('MP4Box', host);
    const loading = loadMp4Box({ scriptUrl: 'https://cdn.example/b/element.js' });
    globalThis.MP4Box = ours;
    scripts[0].onload();
    const got = await loading;
    console.log(JSON.stringify({ resolved: got === ours, hostBack: globalThis.MP4Box === host }));
  `);

  assert.deepEqual(result, { resolved: true, hostBack: true });
});

test('loadMp4Box still rejects when the load fails and the host\'s MP4Box is read-only', () => {
  const result = inFreshGlobal(`
    Object.defineProperty(globalThis, 'MP4Box', { value: { createFile: () => 'host' }, writable: false, configurable: false });
    const loading = loadMp4Box({ scriptUrl: 'https://cdn.example/c/element.js' });
    scripts[0].onerror();
    const message = await loading.then(() => 'resolved', (error) => error.message);
    console.log(JSON.stringify({ message }));
  `);

  assert.match(result.message, /could not be loaded/);
});
