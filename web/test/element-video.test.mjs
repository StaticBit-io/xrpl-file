// VideoView against fakes: a scripted <video>, a streamer that records what it was given, a feed
// where every block holds ten seconds, and a handle whose block reads the test settles by hand. The
// glue is checked in a real browser too; this pins the decisions it makes with what the browser tells it.
import { test, mock } from 'node:test';
import assert from 'node:assert/strict';
import { VideoView } from '../src/element/video.mjs';
import { StreamerError } from '../src/element/media/mse.mjs';
import { XrplFileError } from '../src/link.mjs';
import { messageFor, detailFor } from '../src/element/messages.mjs';
import { STRINGS } from '../src/element/strings.mjs';
import { PRIORITY } from '../src/element/page.mjs';

const BLOCK = 1000;
const BLOCKS = 12;
const SECONDS_PER_BLOCK = 10;
const INIT = 255;

const flush = () => new Promise((resolve) => setImmediate(resolve));

async function until(condition, what) {
  for (let i = 0; i < 200; i++) {
    if (condition()) return;
    await flush();
  }
  assert.fail(`timed out waiting for ${what}`);
}

function blockBytes(index) {
  const bytes = new Uint8Array(BLOCK);
  // ftyp: 24 bytes, enough for detectContainer to call it an ISO file.
  if (index === 0) bytes.set([0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70]);
  return bytes;
}

class FakeVideo extends EventTarget {
  currentTime = 0;
  paused = true;
  seeking = false;
  readyState = 0;
  videoWidth = 640;
  videoHeight = 360;
  attributes = {};
  poster = '';
  removed = false;
  setAttribute(name, value) { this.attributes[name] = value; }
  removeAttribute(name) { delete this.attributes[name]; }
  load() {}
  remove() { this.removed = true; }
  fire(type) { this.dispatchEvent(new Event(type)); }
  play() { this.paused = false; this.fire('play'); }
  seek(time) { this.currentTime = time; this.seeking = true; this.fire('seeking'); }
}

class FakeImage extends EventTarget {
  naturalWidth = 960;
  naturalHeight = 540;
  set src(value) { this.url = value; }
}

class FakeElement {
  hidden = false;
  textContent = '';
  attributes = {};
  flags = {};
  classList = { toggle: (name, on) => { this.flags[name] = on; } };
  setAttribute(name, value) { this.attributes[name] = value; }
}

function fakeRoot() {
  const overlay = new FakeElement();
  const overlayText = new FakeElement();
  const root = {
    overlay,
    overlayText,
    video: null,
    images: [],
    ownerDocument: {
      createElement(name) {
        if (name === 'video') return root.video = new FakeVideo();
        const image = new FakeImage();
        root.images.push(image);
        return image;
      },
    },
    prepend() {},
    querySelector: (selector) => (selector === '.overlay' ? overlay : overlayText),
  };
  return root;
}

class FakeHandle {
  calls = [];
  coverValue = null;

  blockBytes() { return Promise.resolve(BLOCK); }

  cover() { return Promise.resolve(this.coverValue); }

  block(index, { priority, signal, onChunk }) {
    const call = { index, priority, signal, onChunk, settled: false };
    this.calls.push(call);
    return new Promise((resolve, reject) => {
      call.resolve = () => { call.settled = true; resolve(blockBytes(index)); };
      call.reject = (error) => { call.settled = true; reject(error); };
      signal?.addEventListener('abort', () => { call.settled = true; reject(new DOMException('aborted', 'AbortError')); }, { once: true });
    });
  }

  pending(index) { return this.calls.filter((call) => call.index === index && !call.settled); }

  indexes() { return this.calls.map((call) => call.index); }
}

class FakeFeed {
  kind = 'fmp4';
  tracks = [{ id: 0, mime: 'video/mp4; codecs="avc1.4D401F"' }];
  duration = BLOCKS * SECONDS_PER_BLOCK;
  seekable = true;
  seeks = [];
  mediaCalls = [];
  closed = false;

  async init() { return [{ track: 0, bytes: new Uint8Array([INIT, INIT]) }]; }

  async seekTo(time) {
    this.seeks.push(time);
    const block = Math.min(BLOCKS - 1, Math.floor(time / SECONDS_PER_BLOCK));
    return { block, offset: block * BLOCK + (block === 0 ? 0 : 7), time: block * SECONDS_PER_BLOCK };
  }

  async media(block, bytes, from) {
    this.mediaCalls.push({ block, from, length: bytes.length });
    return [{ track: 0, bytes: new Uint8Array([block * SECONDS_PER_BLOCK, (block + 1) * SECONDS_PER_BLOCK]) }];
  }

  close() { this.closed = true; }
}

class FakeStreamer {
  static last = null;
  appended = [];
  aborts = 0;
  ends = 0;
  // MSE: endOfStream() makes the source 'ended', and the next append re-opens it.
  readyState = 'open';
  reopens = 0;
  appendsAfterEnd = [];
  closed = false;
  static initialStreaming = true;
  static initialFailure = null;
  streaming = FakeStreamer.initialStreaming;
  failure = FakeStreamer.initialFailure;
  ranges = [];
  #callbacks = [];

  constructor(video, tracks, duration) {
    this.video = video;
    this.tracks = tracks;
    this.duration = duration;
    FakeStreamer.last = this;
  }

  async open() {}

  onStreamingChange(callback) { this.#callbacks.push(callback); }

  setStreaming(value) { this.streaming = value; for (const callback of this.#callbacks) callback(value); }

  async append(track, bytes) {
    if (this.failure !== null && (bytes[0] !== INIT || this.failure.initToo)) throw this.failure.error;
    if (this.readyState === 'ended') {
      this.readyState = 'open';
      this.reopens++;
      this.appendsAfterEnd.push([...bytes]);
    }
    this.appended.push([...bytes]);
    if (bytes[0] === INIT) return;
    this.ranges.push([bytes[0], bytes[1]]);
    this.ranges.sort((a, b) => a[0] - b[0]);
    const merged = [];
    for (const range of this.ranges) {
      const last = merged.at(-1);
      if (last && range[0] <= last[1]) last[1] = Math.max(last[1], range[1]);
      else merged.push([...range]);
    }
    this.ranges = merged;
  }

  async abort() { this.aborts++; }

  buffered() { return this.ranges.map((range) => [...range]); }

  async end() {
    if (this.readyState !== 'open') return;
    this.readyState = 'ended';
    this.ends++;
  }

  close() { this.closed = true; }
}

function setup({ cover = null, openFeed, lang, onRead } = {}) {
  const root = fakeRoot();
  const handle = new FakeHandle();
  handle.coverValue = cover;
  const feed = new FakeFeed();
  const log = { progress: [], errors: [], loads: 0, sizes: [], statuses: [], downloads: [] };
  const view = new VideoView({
    root,
    handle,
    onDownload: (reason) => log.downloads.push(reason),
    record: { manifest: { type: 'video/mp4', size: BLOCK * BLOCKS - 100, blocks: BLOCKS, chunk: 100, name: 'clip.mp4' } },
    priority: () => PRIORITY.NOW,
    onProgress: (detail, kind) => log.progress.push({ detail, kind }),
    onRead,
    onError: (error, state) => log.errors.push({ error, state }),
    onLoad: () => { log.loads++; },
    onSize: (kind, width, height) => log.sizes.push([kind, width, height]),
    onStatus: (text) => log.statuses.push(text),
    lang,
    deps: { openFeed: openFeed ?? (async () => feed), Streamer: FakeStreamer },
  });
  return { view, root, handle, feed, log, video: () => root.video, streamer: () => FakeStreamer.last };
}

/** Starts the view and settles the head read; the view is then reading its first media block. */
async function started(t) {
  const start = t.view.start();
  await until(() => t.handle.pending(0).length === 1, 'the head read');
  t.handle.pending(0)[0].resolve();
  const outcome = await start;
  assert.deepEqual(outcome, { view: 'video' });
  return outcome;
}

async function settle(t, index) {
  await until(() => t.handle.pending(index).length > 0, `a read of block ${index}`);
  t.handle.pending(index)[0].resolve();
  await flush();
  await flush();
}

test('before the first play only the first media block is read', async () => {
  const t = setup();
  try {
    await started(t);
    assert.deepEqual(t.handle.indexes(), [0, 0], 'the head, then the first media block');
    await settle(t, 0);
    assert.deepEqual(t.feed.mediaCalls, [{ block: 0, from: 0, length: BLOCK }]);
    assert.deepEqual(t.streamer().appended[0], [INIT, INIT], 'the init segment goes in first');
    for (let i = 0; i < 5; i++) await flush();
    assert.deepEqual(t.handle.indexes(), [0, 0], 'nothing further is read while the video has not been played');
    assert.equal(t.streamer().ends, 0);
  } finally { t.view.close(); }
});

test('once playing it reads three blocks ahead, then stops at 30 seconds buffered', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().play();
    await until(() => t.handle.indexes().length === 5, 'three reads ahead');
    assert.deepEqual(t.handle.indexes().slice(2), [1, 2, 3]);
    assert.equal(t.handle.calls.at(-3).priority, PRIORITY.NOW, 'the block playback needs next is NOW');
    assert.equal(t.handle.calls.at(-2).priority, PRIORITY.AHEAD);
    assert.equal(t.handle.calls.at(-1).priority, PRIORITY.AHEAD);

    // Out of order: appended in order.
    await settle(t, 3);
    await settle(t, 2);
    assert.deepEqual(t.feed.mediaCalls.map((c) => c.block), [0]);
    await settle(t, 1);
    assert.deepEqual(t.feed.mediaCalls.map((c) => c.block), [0, 1, 2, 3]);
    assert.deepEqual(t.streamer().buffered(), [[0, 40]]);
    for (let i = 0; i < 5; i++) await flush();
    assert.equal(t.handle.indexes().length, 5, '40 s are buffered ahead: nothing more is read');

    // Playing eats the buffer; at 25 s there are 15 s left and reading resumes from the frontier.
    t.video().currentTime = 25;
    t.video().fire('timeupdate');
    await until(() => t.handle.indexes().length === 8, 'reads resume');
    assert.deepEqual(t.handle.indexes().slice(5), [4, 5, 6]);
  } finally { t.view.close(); }
});

test('a pause stops the reading again', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().play();
    await until(() => t.handle.indexes().length === 5, 'reads ahead');
    t.video().paused = true;
    t.video().fire('pause');
    for (const index of [1, 2, 3]) await settle(t, index);
    t.video().currentTime = 25;
    t.video().fire('timeupdate');
    for (let i = 0; i < 5; i++) await flush();
    assert.equal(t.handle.indexes().length, 5);
  } finally { t.view.close(); }
});

test('a seek outside the buffer drops the old reads and starts from inside the new block', async () => {   // Review Focus 5
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().play();
    await until(() => t.handle.indexes().length === 5, 'reads ahead');
    const stale = [1, 2, 3].map((i) => t.handle.pending(i)[0]);

    t.video().seek(85);
    await until(() => t.handle.pending(8).length === 1, 'the read at the target');
    assert.ok(stale.every((call) => call.signal.aborted), 'the reads of the old generation were aborted');
    assert.equal(t.streamer().aborts, 1);
    assert.deepEqual(t.feed.seeks, [0, 85]);
    assert.equal(t.handle.pending(8)[0].priority, PRIORITY.NOW);
    assert.equal(t.video().currentTime, 85, 'the playhead stays where the visitor put it');

    // A late answer of the old generation changes nothing.
    stale[0].resolve();
    await flush();
    await settle(t, 8);
    assert.deepEqual(t.feed.mediaCalls.map((c) => [c.block, c.from]), [[0, 0], [8, 8 * BLOCK + 7]]);
    assert.deepEqual(t.streamer().buffered(), [[0, 10], [80, 90]]);
  } finally { t.view.close(); }
});

test('a seek inside the buffer reads nothing new', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().play();
    await until(() => t.handle.indexes().length === 5, 'reads ahead');
    for (const index of [1, 2, 3]) await settle(t, index);
    const reads = t.handle.indexes().length;
    t.video().seek(33);
    for (let i = 0; i < 5; i++) await flush();
    // No generation of its own: the parser is not reset, the feed is not asked where 33 s lives, and the
    // reads that follow are the read-ahead's own, from the frontier.
    assert.equal(t.streamer().aborts, 0);
    assert.deepEqual(t.feed.seeks, [0]);
    assert.ok(t.handle.indexes().slice(reads).every((index) => index >= 4));
  } finally { t.view.close(); }
});

test('before play, a seek reads only its first media block as well', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().seek(55);
    await settle(t, 5);
    for (let i = 0; i < 5; i++) await flush();
    assert.deepEqual(t.handle.indexes().slice(2), [5]);
    assert.deepEqual(t.feed.mediaCalls.at(-1), { block: 5, from: 5 * BLOCK + 7, length: BLOCK });
  } finally { t.view.close(); }
});

test('after a jump forward and a step back, reads carry on where the playhead plays', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().play();
    for (const index of [1, 2, 3]) await settle(t, index);   // 0..40 buffered, reads at block 4 next
    t.video().seek(85);
    await settle(t, 8);                                      // 80..90
    for (const index of [9, 10]) await settle(t, index);     // 80..110 now buffered
    const before = t.handle.indexes().length;

    t.video().seeking = false;
    t.video().currentTime = 15;                              // back in the first range; 25 s ahead of it
    t.video().fire('seeking');
    t.video().fire('timeupdate');
    await until(() => t.handle.indexes().length > before, 'reads from the end of the first range');
    assert.equal(t.handle.calls.at(before).index, 4, 'block 4 holds the end of 0..40, not block 11');
    assert.deepEqual(t.feed.seeks.at(-1), 40);
  } finally { t.view.close(); }
});

test('a node that does not answer is retried 1, 2, 4 seconds on, under the buffering overlay', async () => {   // Review Focus 4
  const t = setup();
  mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
  try {
    await started(t);
    await settle(t, 0);
    t.video().play();
    t.video().readyState = 4;
    t.video().fire('loadeddata');
    await until(() => t.handle.pending(1).length === 1, 'the read of block 1');
    // Playback has eaten what was buffered and waits for the next block.
    t.video().readyState = 1;
    t.video().fire('waiting');

    t.handle.pending(1)[0].reject(new XrplFileError('NodeUnreachable', 'down'));
    await flush(); await flush();
    assert.equal(t.handle.calls.filter((c) => c.index === 1).length, 1);
    assert.equal(t.root.overlay.hidden, false);
    assert.equal(t.root.overlayText.textContent, 'The node is not answering. Retrying…');

    mock.timers.tick(999); await flush();
    assert.equal(t.handle.calls.filter((c) => c.index === 1).length, 1);
    mock.timers.tick(1); await flush(); await flush();
    assert.equal(t.handle.calls.filter((c) => c.index === 1).length, 2, 'the same block, again');

    t.handle.pending(1)[0].reject(new XrplFileError('NodeUnreachable', 'down'));
    await flush(); await flush();
    mock.timers.tick(1999); await flush();
    assert.equal(t.handle.calls.filter((c) => c.index === 1).length, 2);
    mock.timers.tick(1); await flush(); await flush();
    assert.equal(t.handle.calls.filter((c) => c.index === 1).length, 3);

    t.handle.pending(1)[0].resolve();
    await flush(); await flush();
    assert.equal(t.log.errors.length, 0, 'a dropped connection is never reported as an error');
  } finally {
    t.view.close();
    mock.timers.reset();
  }
});

test('a block that fails verification is not retried and shows its error where playback reaches the gap', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().play();
    t.video().readyState = 4;
    t.video().fire('loadeddata');
    await until(() => t.handle.indexes().length === 5, 'reads ahead');

    const mismatch = new XrplFileError('BlockMismatch', 'x', { block: 2 });
    await settle(t, 1);
    t.handle.pending(2)[0].reject(mismatch);
    await settle(t, 3);
    await flush();
    assert.equal(t.handle.calls.filter((c) => c.index === 2).length, 1, 'no retry');
    assert.deepEqual(t.feed.mediaCalls.map((c) => c.block), [0, 1], 'nothing past the failed block is appended');
    assert.deepEqual(t.streamer().buffered(), [[0, 20]]);

    // Earlier video plays on without a word.
    t.video().currentTime = 8;
    t.video().fire('timeupdate');
    assert.equal(t.log.errors.length, 0);
    assert.equal(t.root.overlay.hidden, true);

    // At the gap the error shows, once.
    t.video().currentTime = 20;
    t.video().readyState = 1;
    t.video().fire('waiting');
    t.video().fire('timeupdate');
    assert.equal(t.root.overlay.hidden, false);
    assert.equal(t.root.overlay.flags.failed, true);
    assert.equal(t.root.overlayText.textContent, 'Playback stopped at block 2: it did not pass verification.');
    assert.equal(t.root.overlayText.textContent, messageFor(mismatch, { playing: true }));
    assert.equal(t.log.errors.length, 1);
    assert.equal(t.log.errors[0].error, mismatch);
    assert.deepEqual(t.log.errors[0].state, { playing: true });
  } finally { t.view.close(); }
});

test('a failure before any frame is shown is reported as not playing', async () => {
  const t = setup();
  try {
    const start = t.view.start();
    await until(() => t.handle.pending(0).length === 1, 'the head read');
    t.handle.pending(0)[0].resolve();
    await start;
    await until(() => t.handle.pending(0).length === 1, 'the first media block');
    t.handle.pending(0)[0].reject(new XrplFileError('BlockMismatch', 'x', { block: 0 }));
    await flush(); await flush();
    assert.equal(t.log.errors.length, 1);
    assert.deepEqual(t.log.errors[0].state, { playing: false });
  } finally { t.view.close(); }
});

test('a head that fails verification rejects start, with nothing built', async () => {
  const t = setup();
  const start = t.view.start();
  await until(() => t.handle.pending(0).length === 1, 'the head read');
  const mismatch = new XrplFileError('BlockMismatch', 'x', { block: 0 });
  t.handle.pending(0)[0].reject(mismatch);
  await assert.rejects(start, mismatch);
  assert.equal(t.video(), null);
  t.view.close();
});

test('a file whose bytes are not its type is offered as a download', async () => {
  const handle = new FakeHandle();
  // The head is an ISO file; a WebM type does not agree with it.
  const view = new VideoView({
    root: fakeRoot(), handle, priority: () => PRIORITY.NOW, onProgress() {}, onError() {}, onLoad() {},
    record: { manifest: { type: 'video/webm', size: BLOCK * BLOCKS, blocks: BLOCKS, chunk: 100 } },
    deps: { openFeed: async () => assert.fail('no feed for a mismatch'), Streamer: FakeStreamer },
  });
  const start = view.start();
  await until(() => handle.pending(0).length === 1, 'the head read');
  handle.pending(0)[0].resolve();
  assert.deepEqual(await start, { view: 'download', reason: 'mismatch' });
  view.close();
});

test('a codec the browser refuses is a download with its own reason', async () => {
  class Refusing extends FakeStreamer {
    constructor() { super(...arguments); throw new StreamerError('no hvc1', { code: 'unsupported' }); }
  }
  const root = fakeRoot();
  const handle = new FakeHandle();
  const view = new VideoView({
    root, handle, priority: () => PRIORITY.NOW, onProgress() {}, onError() {}, onLoad() {},
    record: { manifest: { type: 'video/mp4', size: BLOCK * BLOCKS, blocks: BLOCKS, chunk: 100 } },
    deps: { openFeed: async () => new FakeFeed(), Streamer: Refusing },
  });
  const start = view.start();
  await until(() => handle.pending(0).length === 1, 'the head read');
  handle.pending(0)[0].resolve();
  assert.deepEqual(await start, { view: 'download', reason: 'unplayable' });
  view.close();
});

test('a feed that cannot read the container is a download, a node error is not swallowed', async () => {
  const run = async (failure) => {
    const handle = new FakeHandle();
    const view = new VideoView({
      root: fakeRoot(), handle, priority: () => PRIORITY.NOW, onProgress() {}, onError() {}, onLoad() {},
      record: { manifest: { type: 'video/mp4', size: BLOCK * BLOCKS, blocks: BLOCKS, chunk: 100 } },
      deps: { openFeed: async () => { throw failure; }, Streamer: FakeStreamer },
    });
    const start = view.start();
    await until(() => handle.pending(0).length === 1, 'the head read');
    handle.pending(0)[0].resolve();
    try { return await start; } catch (error) { return error; } finally { view.close(); }
  };
  assert.deepEqual(await run(new RangeError('damaged moov')), { view: 'download', reason: 'unplayable' });
  const missing = new XrplFileError('ChunkMissing', 'x', { block: 3 });
  assert.equal(await run(missing), missing);
});

test('a player library that cannot be loaded gets its own download reason and says nothing to the console', async () => {
  const handle = new FakeHandle();
  const noise = [];
  mock.method(console, 'error', (...args) => noise.push(args));
  mock.method(console, 'warn', (...args) => noise.push(args));
  const view = new VideoView({
    root: fakeRoot(), handle, priority: () => PRIORITY.NOW, onProgress() {}, onError() {}, onLoad() {},
    record: { manifest: { type: 'video/mp4', size: BLOCK * BLOCKS, blocks: BLOCKS, chunk: 100 } },
    deps: {
      openFeed: async ({ loadMp4Box }) => { await loadMp4Box(); throw new Error('unreachable'); },
      loadMp4Box: async () => { throw new Error('the mp4box script could not be loaded from https://x.invalid/mp4box.min.js'); },
      Streamer: FakeStreamer,
    },
  });
  try {
    const start = view.start();
    await until(() => handle.pending(0).length === 1, 'the head read');
    handle.pending(0)[0].resolve();
    assert.deepEqual(await start, { view: 'download', reason: 'player' });
    assert.deepEqual(noise, []);
  } finally { view.close(); mock.restoreAll(); }
});

test('a player that loads but then fails to parse the file stays "unplayable"', async () => {
  const handle = new FakeHandle();
  const view = new VideoView({
    root: fakeRoot(), handle, priority: () => PRIORITY.NOW, onProgress() {}, onError() {}, onLoad() {},
    record: { manifest: { type: 'video/mp4', size: BLOCK * BLOCKS, blocks: BLOCKS, chunk: 100 } },
    deps: {
      openFeed: async ({ loadMp4Box }) => { await loadMp4Box(); throw new RangeError('damaged moov'); },
      loadMp4Box: async () => ({}),
      Streamer: FakeStreamer,
    },
  });
  try {
    const start = view.start();
    await until(() => handle.pending(0).length === 1, 'the head read');
    handle.pending(0)[0].resolve();
    assert.deepEqual(await start, { view: 'download', reason: 'unplayable' });
  } finally { view.close(); }
});

test('progress counts each verified block once, whatever re-reads follow', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    assert.equal(t.log.progress.length, 1, 'block 0 was read twice and counted once');
    assert.deepEqual(t.log.progress[0].detail, { bytesDone: BLOCK, bytesTotal: BLOCK * BLOCKS - 100, blocksVerified: 1, blocksTotal: BLOCKS });
    assert.deepEqual(t.log.progress[0].kind, { block: true });
    t.video().play();
    await settle(t, 1);
    assert.equal(t.log.progress.at(-1).detail.blocksVerified, 2);
    assert.equal(t.log.progress.at(-1).detail.bytesDone, 2 * BLOCK);
  } finally { t.view.close(); }
});

test('the read report gets every chunk a block read takes, and the block once it verified', async () => {
  const seen = [];
  const t = setup({ onRead: { chunk: (info) => seen.push(['chunk', info]), block: (index) => seen.push(['block', index]) } });
  try {
    const start = t.view.start();
    await until(() => t.handle.pending(0).length === 1, 'the head read');
    t.handle.pending(0)[0].onChunk({ index: 0, ledger: 700 });
    t.handle.pending(0)[0].onChunk({ index: 1, ledger: 701 });
    assert.deepEqual(seen, [['chunk', { index: 0, ledger: 700 }], ['chunk', { index: 1, ledger: 701 }]], 'a chunk is reported as it arrives, before its block');
    t.handle.pending(0)[0].resolve();
    await start;
    assert.deepEqual(seen.at(-1), ['block', 0]);
  } finally { t.view.close(); }
});

test('after a forward seek the last block still ends the stream, hole or not', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().play();
    t.video().seek(115);
    await settle(t, 11);
    await until(() => t.streamer().ends === 1, 'the end of the stream');
    assert.deepEqual(t.streamer().buffered(), [[0, 10], [110, 120]], 'the hole before the last block is still there');
    assert.equal(t.streamer().readyState, 'ended');
  } finally { t.view.close(); }
});

test('a buffer whose start the browser evicted still ends at the last block', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().play();
    t.video().seek(115);
    // Chromium drops the start of a long file by itself; nothing before the playhead is left.
    t.streamer().ranges = [];
    await settle(t, 11);
    await until(() => t.streamer().ends === 1, 'the end of the stream');
  } finally { t.view.close(); }
});

test('a last range that stops short of the duration does not end the stream', async () => {
  const t = setup();
  try {
    t.feed.duration = 125;
    await started(t);
    await settle(t, 0);
    t.video().play();
    t.video().seek(115);
    await settle(t, 11);
    await flush(); await flush();
    assert.equal(t.streamer().ends, 0, 'the file says it is longer than what arrived');
  } finally { t.view.close(); }
});

test('seeking back into a hole after the stream ended reads and appends again, re-opening the source', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().play();
    t.video().seek(115);
    await settle(t, 11);
    await until(() => t.streamer().ends === 1, 'the end of the stream');
    const readsBefore = t.handle.calls.length;
    const abortsBefore = t.streamer().aborts;

    t.video().seeking = false;
    t.video().seek(55);
    await settle(t, 5);

    assert.ok(t.handle.calls.length > readsBefore, 'the hole is read again');
    assert.ok(t.streamer().aborts > abortsBefore, 'the seek asked the streamer to reset');
    assert.equal(t.streamer().reopens, 1, 'the append re-opened the ended source');
    assert.deepEqual(t.streamer().appendsAfterEnd[0], [50, 60]);
    assert.ok(t.streamer().buffered().some(([from, to]) => from <= 50 && to >= 60));
    assert.equal(t.streamer().readyState, 'open');
  } finally { t.view.close(); }
});

test('the last block of a file played from the start ends the stream', async () => {
  const root = fakeRoot();
  const handle = new FakeHandle();
  const feed = new FakeFeed();
  feed.duration = 20;
  const view = new VideoView({
    root, handle, priority: () => PRIORITY.NOW, onProgress() {}, onError() {}, onLoad() {},
    record: { manifest: { type: 'video/mp4', size: BLOCK * 2, blocks: 2, chunk: 100 } },
    deps: { openFeed: async () => feed, Streamer: FakeStreamer },
  });
  try {
    const start = view.start();
    await until(() => handle.pending(0).length === 1, 'the head read');
    handle.pending(0)[0].resolve();
    await start;
    await until(() => handle.pending(0).length === 1, 'the first media block');
    handle.pending(0)[0].resolve();
    await flush(); await flush();
    root.video.play();
    await until(() => handle.pending(1).length === 1, 'the last block');
    handle.pending(1)[0].resolve();
    await until(() => FakeStreamer.last.ends === 1, 'the end of the stream');
  } finally { view.close(); }
});

test('the cover is the poster and the host takes its proportions; close gives the URL back', async () => {
  const revoked = [];
  mock.method(URL, 'revokeObjectURL', (url) => revoked.push(url));
  const t = setup({ cover: { bytes: new Uint8Array([0xff, 0xd8, 0xff]), type: 'image/jpeg' } });
  try {
    await started(t);
    await until(() => t.root.video.poster !== '', 'the poster');
    assert.match(t.root.video.poster, /^blob:/);
    assert.equal(t.root.images.length, 1);
    t.root.images[0].dispatchEvent(new Event('load'));
    assert.deepEqual(t.log.sizes, [['cover', 960, 540]]);

    t.video().fire('loadedmetadata');
    assert.deepEqual(t.log.sizes.at(-1), ['content', 640, 360]);
    assert.equal(t.video().attributes['aria-label'], 'clip.mp4');
  } finally {
    t.view.close();
    mock.restoreAll();
  }
  assert.equal(revoked.length, 1);
});

test('a missing or failing cover is silently ignored', async () => {
  const t = setup();
  t.handle.cover = () => Promise.reject(new XrplFileError('ManifestNotFound', 'x'));
  try {
    await started(t);
    await flush();
    assert.equal(t.root.video.poster, '');
    assert.equal(t.log.errors.length, 0);
  } finally { t.view.close(); }
});

test('the first frame fires load once', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    assert.equal(t.log.loads, 0);
    t.video().readyState = 2;
    t.video().fire('loadeddata');
    t.video().fire('canplay');
    assert.equal(t.log.loads, 1);
  } finally { t.view.close(); }
});

test('before the first frame the overlay says buffering, and it goes once there is one', async () => {
  const t = setup();
  try {
    await started(t);
    assert.equal(t.root.overlay.hidden, false);
    assert.equal(t.root.overlayText.textContent, 'Buffering…');
    await settle(t, 0);
    t.video().readyState = 4;
    t.video().fire('loadeddata');
    assert.equal(t.root.overlay.hidden, true);
  } finally { t.view.close(); }
});

test('a browser that does not want data yet is not shown as buffering, and reading starts when it does', async () => {
  FakeStreamer.initialStreaming = false;
  const t = setup();
  try {
    const start = t.view.start();
    await until(() => t.handle.pending(0).length === 1, 'the head read');
    t.handle.pending(0)[0].resolve();
    await start;
    FakeStreamer.initialStreaming = true;
    for (let i = 0; i < 3; i++) await flush();
    assert.deepEqual(t.handle.indexes(), [0], 'no media block is read while the browser says it has enough');
    assert.equal(t.root.overlay.hidden, true);

    t.streamer().setStreaming(true);
    await until(() => t.handle.pending(0).length === 1, 'the first media block');
  } finally { FakeStreamer.initialStreaming = true; t.view.close(); }
});

test('close aborts every read, closes the source and the feed, and a late answer is ignored', async () => {
  const t = setup();
  await started(t);
  await settle(t, 0);
  t.video().play();
  await until(() => t.handle.indexes().length === 5, 'reads ahead');
  const open = [1, 2, 3].map((i) => t.handle.pending(i)[0]);

  t.view.close();
  assert.ok(open.every((call) => call.signal.aborted));
  assert.equal(t.streamer().closed, true);
  assert.equal(t.feed.closed, true);
  assert.equal(t.root.video.removed, true);
  const appended = t.streamer().appended.length;
  open[0].resolve();
  await flush();
  assert.equal(t.streamer().appended.length, appended);
  t.view.close();
});

test('closing while the head is still being read ends start quietly', async () => {
  const t = setup();
  const start = t.view.start();
  await until(() => t.handle.pending(0).length === 1, 'the head read');
  t.view.close();
  assert.deepEqual(await start, { view: 'closed' });
});

test('a head that cannot be had is retried too, with a line for the loading view', async () => {
  const t = setup();
  mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
  try {
    const start = t.view.start();
    await until(() => t.handle.pending(0).length === 1, 'the head read');
    t.handle.pending(0)[0].reject(new XrplFileError('NodeUnreachable', 'down'));
    await flush(); await flush();
    assert.deepEqual(t.log.statuses, ['The node is not answering. Retrying…']);
    mock.timers.tick(1000); await flush(); await flush();
    await until(() => t.handle.pending(0).length === 1, 'the second try');
    t.handle.pending(0)[0].resolve();
    await start;
    assert.deepEqual(t.log.statuses, ['The node is not answering. Retrying…', null]);
  } finally {
    t.view.close();
    mock.timers.reset();
  }
});

test('the first media block is read from inside the block the feed answered, which need not be block 0', async () => {
  const t = setup();
  t.feed.seekTo = async (time) => {
    t.feed.seeks.push(time);
    return { block: 1, offset: BLOCK + 4096 % BLOCK, time: 0 };
  };
  try {
    await started(t);
    await until(() => t.handle.pending(1).length === 1, 'a read of block 1');
    assert.deepEqual(t.handle.indexes(), [0, 1], 'block 0 is not read for media');
    await settle(t, 1);
    assert.deepEqual(t.feed.mediaCalls, [{ block: 1, from: BLOCK + 96, length: BLOCK }]);
  } finally { t.view.close(); }
});

test('blocks queued for the buffer when a seek comes are not handed to the feed afterwards', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().play();
    await until(() => t.handle.indexes().length === 5, 'reads ahead');

    let releaseMedia;
    const asked = [];
    const media = t.feed.media.bind(t.feed);
    t.feed.media = async (block, bytes, from) => {
      asked.push(block);
      if (block === 1) await new Promise((resolve) => { releaseMedia = resolve; });
      return media(block, bytes, from);
    };
    let releaseSeek;
    const seekTo = t.feed.seekTo.bind(t.feed);
    t.feed.seekTo = async (time) => {
      await new Promise((resolve) => { releaseSeek = resolve; });
      return seekTo(time);
    };

    await settle(t, 3);
    await settle(t, 2);
    await settle(t, 1);
    await until(() => releaseMedia !== undefined, 'the feed to be busy with block 1');

    // The seek has begun (and waits for the feed to answer); the batch still holds blocks 2 and 3.
    t.video().seek(85);
    await until(() => releaseSeek !== undefined, 'the seek to reach the feed');
    releaseMedia();
    for (let i = 0; i < 5; i++) await flush();
    assert.deepEqual(asked, [1], 'only the call that was already under way; blocks 2 and 3 never reach the feed');
    releaseSeek();
  } finally { t.view.close(); }
});

const rejected = () => new StreamerError('the decoder rejected these bytes: the segment is damaged or out of order');

test('a stream the browser rejects before any frame is shown becomes the download view, with no error', async () => {
  const t = setup();
  try {
    await started(t);
    t.streamer().failure = { error: rejected() };
    await settle(t, 0);
    await flush();
    assert.deepEqual(t.log.downloads, ['unplayable']);
    assert.equal(t.log.errors.length, 0);
    assert.equal(t.root.overlayText.textContent !== 'Playback stopped at block 0: it did not pass verification.', true);
    // Nothing more is read for a file that cannot be played.
    const reads = t.handle.indexes().length;
    t.video().play();
    for (let i = 0; i < 5; i++) await flush();
    assert.equal(t.handle.indexes().length, reads);
  } finally { t.view.close(); }
});

test('the video element failing before any frame is the download view too', async () => {
  const t = setup();
  try {
    await started(t);
    t.video().fire('error');
    assert.deepEqual(t.log.downloads, ['unplayable']);
    t.video().fire('error');
    assert.deepEqual(t.log.downloads, ['unplayable'], 'said once');
    assert.equal(t.log.errors.length, 0);
  } finally { t.view.close(); }
});

test('an init segment the browser rejects ends start with the download outcome', async () => {
  FakeStreamer.initialFailure = { error: rejected(), initToo: true };
  const t = setup();
  try {
    const start = t.view.start();
    await until(() => t.handle.pending(0).length === 1, 'the head read');
    t.handle.pending(0)[0].resolve();
    assert.deepEqual(await start, { view: 'download', reason: 'unplayable' });
  } finally { FakeStreamer.initialFailure = null; t.view.close(); }
});

test('after a frame has been shown a rejected append is the error path, not a download', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().readyState = 4;
    t.video().fire('loadeddata');
    t.video().play();
    await until(() => t.handle.indexes().length === 5, 'reads ahead');
    t.streamer().failure = { error: rejected() };
    await settle(t, 1);
    await flush();
    assert.deepEqual(t.log.downloads, []);

    // The playhead reaches the gap the failed block left.
    t.video().currentTime = 10;
    t.video().readyState = 1;
    t.video().fire('waiting');
    assert.equal(t.log.errors.length, 1);
    assert.deepEqual(t.log.errors[0].state, { playing: true });
    assert.equal(t.root.overlay.flags.failed, true);
  } finally { t.view.close(); }
});

test('after a frame has been shown the video element failing is the error path', async () => {
  const t = setup();
  try {
    await started(t);
    await settle(t, 0);
    t.video().readyState = 4;
    t.video().fire('loadeddata');
    t.video().fire('error');
    assert.deepEqual(t.log.downloads, []);
    assert.equal(t.log.errors.length, 1);
    assert.deepEqual(t.log.errors[0].state, { playing: true });
    assert.equal(t.root.overlayText.textContent, 'This browser could not decode this video.');
    assert.deepEqual(detailFor(t.log.errors[0].error, { playing: true }), { name: null, message: 'This browser could not decode this video.' });
  } finally { t.view.close(); }
});

test('the overlay is worded in the language it is asked for, and again after relocalize()', async () => {
  let lang = 'ru';
  const t = setup({ lang: () => lang });
  try {
    await started(t);
    assert.equal(t.root.overlayText.textContent, STRINGS.ru.buffering);
    lang = 'de';
    t.view.relocalize();
    assert.equal(t.root.overlayText.textContent, STRINGS.de.buffering);
    lang = 'en';
    t.view.relocalize();
    assert.equal(t.root.overlayText.textContent, 'Buffering…');
  } finally { t.view.close(); }
});

test('the retry line of the loading view is in the language, and is sent again when it changes', async () => {
  let lang = 'ru';
  const t = setup({ lang: () => lang });
  mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
  try {
    const start = t.view.start();
    await until(() => t.handle.pending(0).length === 1, 'the head read');
    t.handle.pending(0)[0].reject(new XrplFileError('NodeUnreachable', 'down'));
    await flush(); await flush();
    assert.deepEqual(t.log.statuses, [STRINGS.ru.retrying]);
    lang = 'zh';
    t.view.relocalize();
    assert.deepEqual(t.log.statuses, [STRINGS.ru.retrying, STRINGS.zh.retrying]);
    mock.timers.tick(1000); await flush(); await flush();
    await until(() => t.handle.pending(0).length === 1, 'the second try');
    t.handle.pending(0)[0].resolve();
    await start;
    assert.deepEqual(t.log.statuses.at(-1), null);
    t.view.relocalize();
    assert.equal(t.log.statuses.at(-1), null, 'nothing is sent while there is no retry line');
  } finally {
    t.view.close();
    mock.timers.reset();
  }
});

test('a view asked for no language speaks English, and relocalize() on a closed view does nothing', async () => {
  const t = setup();
  await started(t);
  assert.equal(t.root.overlayText.textContent, 'Buffering…');
  t.view.close();
  t.view.relocalize();
});
