// Streamer against a fake MediaSource: which class it picks, how it sets the source up, how it queues
// appends and what it does when the browser says its buffer is full. Real decoding is for the browser.
import { test, afterEach, mock } from 'node:test';
import assert from 'node:assert/strict';
import { Streamer, StreamerError } from '../src/element/media/mse.mjs';

const sources = new Map();
let counter = 0;

class FakeSourceBuffer extends EventTarget {
  mode = 'sequence';
  updating = false;
  ranges = [];
  appends = [];
  removes = [];
  aborts = 0;
  quotaFailures = 0;
  concurrent = 0;
  maxConcurrent = 0;
  decodeFailures = 0;

  get buffered() {
    return { length: this.ranges.length, start: (i) => this.ranges[i][0], end: (i) => this.ranges[i][1] };
  }

  // MSE: appending to (or removing from) a buffer of an ended source re-opens it first.
  reopenParent() {
    if (this.parent?.readyState !== 'ended') return;
    this.parent.readyState = 'open';
    this.parent.reopened++;
    queueMicrotask(() => this.parent.dispatchEvent(new Event('sourceopen')));
  }

  appendBuffer(bytes) {
    if (this.updating) throw new DOMException('still updating', 'InvalidStateError');
    this.reopenParent();
    if (this.quotaFailures > 0) { this.quotaFailures--; throw new DOMException('full', 'QuotaExceededError'); }
    const failing = this.decodeFailures > 0;
    if (failing) this.decodeFailures--;
    this.appends.push([...bytes]);
    this.updating = true;
    this.maxConcurrent = Math.max(this.maxConcurrent, ++this.concurrent);
    setImmediate(() => {
      this.concurrent--;
      this.updating = false;
      if (failing) { this.dispatchEvent(new Event('error')); return; }
      if (bytes.length >= 2) this.ranges.push([bytes[0], bytes[1]]);
      this.dispatchEvent(new Event('updateend'));
    });
  }

  remove(from, to) {
    this.reopenParent();
    this.removes.push([from, to]);
    const kept = [];
    for (const [start, end] of this.ranges) {
      if (to <= start || from >= end) { kept.push([start, end]); continue; }
      if (start < from) kept.push([start, from]);
      if (end > to) kept.push([to, end]);
    }
    this.ranges = kept;
    setImmediate(() => this.dispatchEvent(new Event('updateend')));
  }

  // MSE: abort() throws unless the parent source is open.
  abort() {
    if (this.parent && this.parent.readyState !== 'open') throw new DOMException('not open', 'InvalidStateError');
    this.aborts++;
  }
}

class FakeMediaSource extends EventTarget {
  static supported = new Set(['video/mp4; codecs="avc1.4D401F"', 'audio/mp4; codecs="mp4a.40.2"']);
  static asked = [];
  static isTypeSupported(mime) { FakeMediaSource.asked.push(mime); return FakeMediaSource.supported.has(mime); }

  readyState = 'closed';
  buffers = [];
  log = [];
  ended = 0;
  reopened = 0;
  #duration = NaN;

  get duration() { return this.#duration; }
  set duration(value) { this.#duration = value; this.log.push(['duration', value]); }

  addSourceBuffer(mime) {
    const buffer = new FakeSourceBuffer();
    buffer.mime = mime;
    buffer.parent = this;
    this.buffers.push(buffer);
    this.log.push(['buffer', mime]);
    return buffer;
  }

  endOfStream() { this.ended++; this.readyState = 'ended'; }
  open() { this.readyState = 'open'; this.dispatchEvent(new Event('sourceopen')); }
}

class FakeManagedMediaSource extends FakeMediaSource {
  static asked = [];
  static isTypeSupported(mime) { FakeManagedMediaSource.asked.push(mime); return FakeMediaSource.supported.has(mime); }
  streaming = false;
}

function fakeVideo({ opens = true } = {}) {
  const video = new EventTarget();
  video.currentTime = 0;
  video.attributes = {};
  video.loads = 0;
  video.removeAttribute = (name) => { delete video.attributes[name]; };
  video.load = () => { video.loads++; };
  Object.defineProperty(video, 'src', {
    set(url) {
      video.attributes.src = url;
      const source = sources.get(url);
      if (source && opens) queueMicrotask(() => source.open());
    },
  });
  return video;
}

const revoked = [];

function install({ managed = false } = {}) {
  FakeMediaSource.asked = [];
  FakeManagedMediaSource.asked = [];
  globalThis.MediaSource = FakeMediaSource;
  if (managed) globalThis.ManagedMediaSource = FakeManagedMediaSource;
  mock.method(URL, 'createObjectURL', (source) => {
    const url = `blob:fake/${++counter}`;
    sources.set(url, source);
    return url;
  });
  mock.method(URL, 'revokeObjectURL', (url) => revoked.push(url));
}

afterEach(() => {
  delete globalThis.MediaSource;
  delete globalThis.ManagedMediaSource;
  mock.restoreAll();
  sources.clear();
  revoked.length = 0;
});

const VIDEO = { id: 1, mime: 'video/mp4; codecs="avc1.4D401F"' };
const AUDIO = { id: 2, mime: 'audio/mp4; codecs="mp4a.40.2"' };

test('ManagedMediaSource is used where it exists, and asked about the type, not MediaSource', async () => {
  install({ managed: true });
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO], 60);
  assert.deepEqual(FakeManagedMediaSource.asked, [VIDEO.mime]);
  assert.deepEqual(FakeMediaSource.asked, []);
  await streamer.open();
  assert.equal(video.disableRemotePlayback, true, 'a managed source loads only when the element may not cast');
  assert.ok(sources.get(video.attributes.src) instanceof FakeManagedMediaSource);
});

test('plain MediaSource is used otherwise, always allows streaming and leaves casting alone', async () => {
  install();
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO], 60);
  assert.deepEqual(FakeMediaSource.asked, [VIDEO.mime]);
  assert.equal(streamer.streaming, true);
  await streamer.open();
  assert.equal(video.disableRemotePlayback, undefined);
});

test('a managed source says when it wants data and when it has enough', async () => {
  install({ managed: true });
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO], 60);
  const seen = [];
  streamer.onStreamingChange((value) => seen.push(value));
  await streamer.open();
  const source = sources.get(video.attributes.src);
  assert.equal(streamer.streaming, false);
  source.streaming = true;
  source.dispatchEvent(new Event('startstreaming'));
  assert.equal(streamer.streaming, true);
  source.streaming = false;
  source.dispatchEvent(new Event('endstreaming'));
  assert.deepEqual(seen, [true, false]);
});

test('a type the browser refuses, or no Media Source Extensions, is reported as unsupported', () => {
  install();
  const refused = { id: 1, mime: 'video/mp4; codecs="hvc1.1.6.L93.B0"' };
  assert.throws(() => new Streamer(fakeVideo(), [VIDEO, refused], 0), (e) => e instanceof StreamerError && e.code === 'unsupported');
  delete globalThis.MediaSource;
  assert.throws(() => new Streamer(fakeVideo(), [VIDEO], 0), (e) => e instanceof StreamerError && e.code === 'unsupported');
});

test('one buffer per track in segments mode, and the duration is set before the first append', async () => {
  install();
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO, AUDIO], 90);
  await streamer.open();
  const source = sources.get(video.attributes.src);
  assert.deepEqual(source.log, [['buffer', VIDEO.mime], ['buffer', AUDIO.mime], ['duration', 90]]);
  assert.deepEqual(source.buffers.map((b) => b.mode), ['segments', 'segments']);
  await streamer.append(1, new Uint8Array([0, 10]));
  assert.deepEqual(source.buffers[0].appends, [[0, 10]]);
  assert.deepEqual(source.buffers[1].appends, []);
});

test('an unknown duration is not set to zero', async () => {
  install();
  const video = fakeVideo();
  await new Streamer(video, [VIDEO], 0).open();
  assert.deepEqual(sources.get(video.attributes.src).log, [['buffer', VIDEO.mime]]);
});

test('appends of a buffer go one at a time, in the order they were asked', async () => {
  install();
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO], 90);
  await streamer.open();
  const buffer = sources.get(video.attributes.src).buffers[0];
  await Promise.all([1, 2, 3, 4].map((n) => streamer.append(1, new Uint8Array([n * 10, n * 10 + 10]))));
  assert.equal(buffer.maxConcurrent, 1);
  assert.deepEqual(buffer.appends.map((a) => a[0]), [10, 20, 30, 40]);
});

test('a failed append does not poison the ones behind it', async () => {
  install();
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO], 90);
  await streamer.open();
  const buffer = sources.get(video.attributes.src).buffers[0];
  buffer.decodeFailures = 1;
  const failed = streamer.append(1, new Uint8Array([0, 10]));
  const fine = streamer.append(1, new Uint8Array([10, 20]));
  await assert.rejects(failed, StreamerError);
  await fine;
  assert.deepEqual(buffer.ranges, [[10, 20]]);
  await assert.rejects(streamer.append(9, new Uint8Array([0, 1])), /no track 9/);
});

test('a full buffer gives up what is more than two minutes from the playhead and tries once more', async () => {
  install();
  const video = fakeVideo();
  video.currentTime = 125;
  const streamer = new Streamer(video, [VIDEO], 900);
  await streamer.open();
  const buffer = sources.get(video.attributes.src).buffers[0];
  buffer.ranges = [[0, 250]];
  buffer.quotaFailures = 1;

  await streamer.append(1, new Uint8Array([250, 255]));
  assert.deepEqual(buffer.removes, [[0, 5], [245, 250]]);
  assert.deepEqual(buffer.appends, [[250, 255]]);
});

test('a buffer that is still full after the drop, or has nothing far to drop, is a failure', async () => {
  install();
  const video = fakeVideo();
  video.currentTime = 125;
  const streamer = new Streamer(video, [VIDEO], 900);
  await streamer.open();
  const buffer = sources.get(video.attributes.src).buffers[0];

  buffer.ranges = [[100, 150]];
  buffer.quotaFailures = 1;
  await assert.rejects(streamer.append(1, new Uint8Array([150, 160])), StreamerError);
  assert.deepEqual(buffer.removes, []);

  buffer.ranges = [[0, 250]];
  buffer.quotaFailures = 2;
  await assert.rejects(streamer.append(1, new Uint8Array([250, 255])), StreamerError);
  assert.equal(buffer.removes.length, 2, 'it dropped once, not twice');
});

test('abort waits for the appends before it and resets every parser', async () => {
  install();
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO, AUDIO], 90);
  await streamer.open();
  const source = sources.get(video.attributes.src);
  const [videoBuffer, audioBuffer] = source.buffers;
  const pending = streamer.append(1, new Uint8Array([0, 10]));
  const aborted = streamer.abort();
  assert.equal(videoBuffer.aborts, 0, 'the append in flight is not interrupted');
  await Promise.all([pending, aborted]);
  assert.equal(videoBuffer.aborts, 1);
  assert.equal(audioBuffer.aborts, 1);

  source.readyState = 'ended';
  await streamer.abort();
  assert.equal(videoBuffer.aborts, 1, 'not allowed once the stream has ended');
});

test('a segment appended after the stream ended re-opens the source, and a later end closes it again', async () => {
  install();
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO], 120);
  await streamer.open();
  const source = sources.get(video.attributes.src);
  const [buffer] = source.buffers;

  await streamer.append(1, new Uint8Array([110, 120]));
  await streamer.end();
  assert.equal(source.readyState, 'ended');

  // A seek back into the hole: the parser is clean after a finished last segment, so abort() is skipped
  // rather than thrown, and the append itself is what re-opens the source.
  await streamer.abort();
  assert.equal(buffer.aborts, 0);
  await streamer.append(1, new Uint8Array([50, 60]));
  assert.equal(source.reopened, 1);
  assert.equal(source.readyState, 'open');
  assert.deepEqual(streamer.buffered().sort((a, b) => a[0] - b[0]), [[50, 60], [110, 120]]);

  await streamer.end();
  assert.equal(source.ended, 2);
  assert.equal(source.readyState, 'ended');
});

test('the quota drop on an ended stream re-opens it too', async () => {
  install();
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO], 400);
  await streamer.open();
  const source = sources.get(video.attributes.src);
  const [buffer] = source.buffers;
  buffer.ranges = [[0, 10], [390, 400]];
  video.currentTime = 395;
  await streamer.end();
  buffer.quotaFailures = 1;
  await streamer.append(1, new Uint8Array([380, 390]));
  assert.equal(source.reopened, 1);
  assert.deepEqual(buffer.removes, [[0, 10]]);
});

test('what can be played is what every track holds', async () => {
  install();
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO, AUDIO], 90);
  await streamer.open();
  const [videoBuffer, audioBuffer] = sources.get(video.attributes.src).buffers;
  videoBuffer.ranges = [[0, 40], [60, 90]];
  audioBuffer.ranges = [[0.02, 35], [70, 100]];
  assert.deepEqual(streamer.buffered(), [[0.02, 35], [70, 90]]);
});

test('the stream ends once, when it is open', async () => {
  install();
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO], 90);
  await streamer.open();
  const source = sources.get(video.attributes.src);
  await streamer.append(1, new Uint8Array([0, 90]));
  await streamer.end();
  await streamer.end();
  assert.equal(source.ended, 1);
});

test('close gives the URL back, detaches the video and ends a pending open', async () => {
  install();
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO], 90);
  await streamer.open();
  const url = video.attributes.src;
  streamer.close();
  assert.deepEqual(revoked, [url]);
  assert.equal(video.attributes.src, undefined);
  assert.equal(video.loads, 1);
  streamer.close();
  assert.equal(revoked.length, 1);
  await assert.rejects(streamer.open(), StreamerError);

  const pendingVideo = fakeVideo({ opens: false });
  const waiting = new Streamer(pendingVideo, [VIDEO], 90);
  const opening = waiting.open();
  waiting.close();
  await assert.rejects(opening, StreamerError);
});

test('appends after close do nothing', async () => {
  install();
  const video = fakeVideo();
  const streamer = new Streamer(video, [VIDEO], 90);
  await streamer.open();
  const buffer = sources.get(video.attributes.src).buffers[0];
  streamer.close();
  await streamer.append(1, new Uint8Array([0, 10]));
  assert.deepEqual(buffer.appends, []);
});
