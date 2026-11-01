// The video view: a native <video controls> fed through Media Source Extensions with blocks that
// verified, read ahead while playing, re-aimed by seeks, and covered by a buffering overlay.
//
// This is the glue between a real <video>, the verified blocks of a FileHandle, a feed (what to
// append, where a time lives in the file) and the pure decisions of media/playback.mjs. It adapts the
// shape of the video platform's js/player.mjs (E:\GIT\XRPL\xrpl-video-platform): a generation that a
// seek replaces, and a read-ahead that follows the playhead. It is checked in a browser, and with fakes
// in test/element-video.test.mjs.

import { openFeed } from './media/feeds.mjs';
import { detectContainer, containerAgrees } from './media/container.mjs';
import { loadMp4Box } from './media/mp4box-loader.mjs';
import { Streamer, StreamerError } from './media/mse.mjs';
import {
  ReadAhead, shouldRead, seekDecision, overlayState, retryDelay, continuation, rangeAt,
} from './media/playback.mjs';
import { PRIORITY } from './page.mjs';
import { progressDetail } from './load.mjs';
import { messageFor } from './messages.mjs';
import { downloadName } from './format.mjs';

const HAVE_CURRENT_DATA = 2;
const HAVE_FUTURE_DATA = 3;
// How close to the end of a range the playhead can be and still be playing from it.
const EDGE = 0.05;
// A buffer that holds the timeline in one piece, give or take this much at the ends, may be closed.
const WHOLE_TOLERANCE = 0.5;
const SAFETY_TICK_MS = 1000;

const RETRYING = 'The node is not answering. Retrying…';
const BUFFERING = 'Buffering…';

const isAbort = (error) => error?.name === 'AbortError';

/** The URL mp4box.js is looked up next to: the bundle's own, or on the unbundled dev page the loader module's. */
function mp4boxScriptUrl() {
  const url = new URL(import.meta.url);
  if (url.pathname.endsWith('/src/element/video.mjs')) {
    url.pathname = url.pathname.replace(/video\.mjs$/, 'media/mp4box-loader.mjs');
  }
  return url.href;
}

/**
 * One file's playback inside the element.
 *
 * `start()` resolves with `{ view: 'video' }` once the player is in place and reading, or
 * `{ view: 'download', reason }` when the file cannot be played here (`mismatch`: its bytes are not
 * what its type says; `unplayable`: the browser refuses its codecs), or `{ view: 'closed' }` when
 * `close()` came first. It rejects with the reader's error when the file cannot be read or verified
 * before there is anything to show. Later failures go to `onError`.
 */
export class VideoView {
  #root;
  #handle;
  #record;
  #priority;
  #givenCover;
  #deps;
  #onProgress;
  #onError;
  #onDownload;
  #onLoad;
  #onSize;
  #onStatus;

  #lifetime = new AbortController();
  #reads = new AbortController();
  #closed = false;
  #ready = false;

  #video = null;
  #overlay = null;
  #overlayText = null;
  #streamer = null;
  #feed = null;
  #ra = null;
  #blockBytes = 0;
  #lastBlock = 0;

  // Per generation: what is read and not yet appended, the append order, and what that generation has done.
  #held = new Map();
  #appendChain = Promise.resolve();
  #pendingAppends = 0;
  #appendHalted = false;
  #firstMedia = false;
  #epoch = 0;
  #seekPending = false;
  // The media time the current generation has written up to: where reads must carry on from.
  #writeHead = 0;

  #started = false;
  #hasFrame = false;
  #loaded = false;
  #retrying = 0;
  #failure = null;
  #fatal = null;
  #errorReported = false;
  #gaveUp = false;
  #statusShown = false;

  #verified = new Map();
  #bytesVerified = 0;
  #urls = [];
  #posterUrl = null;
  #tick = 0;
  #listeners = [];

  /**
   * @param {object} options
   * @param {HTMLElement} options.root the view's container; the `<video>` goes in as its first child, and it holds the overlay (`.overlay`, `.overlay-text`)
   * @param {object} options.handle a FileHandle from page.mjs
   * @param {object} options.record the manifest record
   * @param {{ bytes: Uint8Array, type: string } | null} [options.cover] a cover already read; left out, the handle is asked (shared per link)
   * @param {() => number} options.priority a PRIORITY value for the element as it is now: NOW when visible
   * @param {(detail: object, kind: { block: boolean }) => void} options.onProgress every newly verified block of this file
   * @param {(error: Error, state: { playing: boolean }) => void} options.onError a block failed and playback stands at the gap, or the browser's decoder gave up; `playing` is whether a frame was already shown
   * @param {(reason: string) => void} [options.onDownload] the browser rejected the stream before any frame was shown: the file is for download instead (`unplayable`)
   * @param {() => void} options.onLoad the first frame is on screen
   * @param {(kind: 'cover' | 'content', width: number, height: number) => void} [options.onSize] natural proportions, for the host's ratio
   * @param {(text: string | null) => void} [options.onStatus] a line for the loading view while nothing is on screen yet; null restores its own
   * @param {object} [options.deps] test seams: `openFeed`, `Streamer`, `loadMp4Box`
   */
  constructor({ root, handle, record, cover, priority, onProgress, onError, onDownload, onLoad, onSize, onStatus, deps = {} }) {
    this.#root = root;
    this.#handle = handle;
    this.#record = record;
    this.#givenCover = cover;
    this.#priority = priority;
    this.#onProgress = onProgress;
    this.#onError = onError;
    this.#onDownload = onDownload ?? (() => {});
    this.#onLoad = onLoad;
    this.#onSize = onSize ?? (() => {});
    this.#onStatus = onStatus ?? (() => {});
    this.#deps = {
      openFeed: deps.openFeed ?? openFeed,
      Streamer: deps.Streamer ?? Streamer,
      loadMp4Box: deps.loadMp4Box ?? (() => loadMp4Box({ scriptUrl: mp4boxScriptUrl() })),
    };
  }

  async start() {
    try {
      return await this.#open();
    } catch (error) {
      if (this.#closed) return { view: 'closed' };
      throw error;
    }
  }

  async #open() {
    const { manifest } = this.#record;
    const signal = this.#lifetime.signal;
    this.#loadCover();

    this.#blockBytes = await this.#handle.blockBytes();
    this.#lastBlock = manifest.blocks - 1;

    const head = await this.#readBlock(0, () => this.#priority(), signal);
    const kind = detectContainer(head);
    if (!containerAgrees(manifest.type, kind)) return { view: 'download', reason: 'mismatch' };

    let feed;
    try {
      feed = await this.#deps.openFeed({
        kind,
        head,
        blockBytes: this.#blockBytes,
        size: manifest.size,
        readBlock: (index) => this.#readBlock(index, () => this.#priority(), signal),
        loadMp4Box: this.#deps.loadMp4Box,
      });
    } catch (error) {
      // The reader's own errors say what is wrong with the file or the node. Anything else is a container
      // that cannot be read as a video, or the player library that could not be loaded.
      if (isAbort(error) || error?.name === 'XrplFileError') throw error;
      return { view: 'download', reason: 'unplayable' };
    }
    if (this.#closed) { feed.close(); return { view: 'closed' }; }
    this.#feed = feed;

    const video = this.#createVideo();
    try {
      this.#streamer = new this.#deps.Streamer(video, feed.tracks, feed.duration);
      this.#streamer.onStreamingChange(() => this.#pump());
      this.#watch(video);
      await this.#streamer.open();
    } catch (error) {
      if (error instanceof StreamerError && error.code === 'unsupported') return { view: 'download', reason: 'unplayable' };
      throw error;
    }
    if (this.#closed) return { view: 'closed' };

    this.#ra = new ReadAhead({ blocks: manifest.blocks });
    const first = await feed.seekTo(0);
    if (first === null) throw new Error('the feed cannot start at the beginning of the file');
    try {
      for (const segment of await feed.init()) await this.#streamer.append(segment.track, segment.bytes);
    } catch (error) {
      if (this.#closed) return { view: 'closed' };
      // The browser took the tracks (isTypeSupported said so) but not these bytes.
      if (error instanceof StreamerError) return { view: 'download', reason: 'unplayable' };
      throw error;
    }
    if (this.#closed) return { view: 'closed' };
    if (this.#gaveUp) return { view: 'download', reason: 'unplayable' };

    this.#ra.restart(first.block, first.offset);
    this.#writeHead = 0;
    this.#ready = true;
    this.#tick = setInterval(() => this.#pump(), SAFETY_TICK_MS);
    this.#pump();
    return { view: 'video' };
  }

  // Reading

  /** The time a block read may wait for the node: forever, for a node that does not answer, and never for a block that fails verification. */
  async #readBlock(index, priority, signal) {
    for (let attempt = 0; ; attempt++) {
      try {
        const bytes = await this.#handle.block(index, {
          priority: typeof priority === 'function' ? priority(index) : priority,
          signal,
          sequential: false,
        });
        this.#noteVerified(index, bytes.length);
        return bytes;
      } catch (error) {
        if (signal.aborted || this.#closed) throw error;

        const delay = retryDelay(error, attempt);
        if (delay === null) throw error;
        await this.#wait(delay, signal);
      }
    }
  }

  /** Waits out a retry delay with the overlay saying so; ends early, quietly, when the reads are aborted. */
  #wait(ms, signal) {
    if (signal.aborted) return Promise.resolve();
    this.#retrying++;
    if (!this.#ready) { this.#statusShown = true; this.#onStatus(RETRYING); }
    this.#refresh();

    return new Promise((resolve) => {
      const done = () => {
        clearTimeout(timer);
        signal.removeEventListener('abort', done);
        this.#retrying--;
        if (this.#statusShown && this.#retrying === 0) { this.#statusShown = false; this.#onStatus(null); }
        resolve();
      };
      const timer = setTimeout(done, ms);
      signal.addEventListener('abort', done, { once: true });
    }).then(() => {
      // Back in the loop after an abort: the next call of the read throws it.
      this.#refresh();
    });
  }

  #noteVerified(index, length) {
    if (this.#verified.has(index)) return;
    this.#verified.set(index, length);
    this.#bytesVerified += length;
    const { manifest } = this.#record;
    this.#onProgress(progressDetail({
      bytesDone: this.#bytesVerified,
      bytesTotal: manifest.size,
      blocksVerified: this.#verified.size,
      blocksTotal: manifest.blocks,
    }), { block: true });
  }

  /** NOW (as the element is) for the block playback needs next, AHEAD or lower for the rest. */
  #priorityFor(block) {
    const base = this.#priority();
    return block === this.#ra.frontier ? base : Math.max(base, PRIORITY.AHEAD);
  }

  #readState() {
    const video = this.#video;
    return {
      started: this.#started,
      playing: !video.paused || video.seeking,
      streamingAllowed: this.#streamer.streaming,
      bufferedAhead: this.#bufferedAhead(),
      firstMediaAppended: this.#firstMedia,
    };
  }

  #bufferedAhead() {
    const time = this.#video.currentTime;
    const range = this.#streamer.buffered().find(([start, end]) => time >= start - 0.1 && time < end);
    return range ? range[1] - time : 0;
  }

  /** Starts the reads the state asks for, and shows what is going on. */
  #pump() {
    if (this.#closed || !this.#ready || this.#gaveUp) return;
    // While blocks are still going into the buffer the buffer does not say how far ahead it is: the next
    // round (the append calls it when it ends) decides with the true figure and reads no more than 30 s ahead.
    if (this.#seekPending || this.#pendingAppends > 0) { this.#refresh(); return; }

    if (this.#followPlayhead()) { this.#refresh(); return; }

    const state = this.#readState();
    while (shouldRead(state)) {
      // Before the first play one read at a time: only the first media block is wanted, not a window of three.
      if (!this.#started && this.#ra.inFlight > 0) break;

      const next = this.#ra.next();
      if (next === null) break;
      this.#startRead(next);
    }

    this.#refresh();
  }

  #startRead({ block, generation }) {
    const signal = this.#reads.signal;
    this.#readBlock(block, () => this.#priorityFor(block), signal).then(
      (bytes) => {
        if (this.#stale(generation, signal)) return;
        this.#held.set(block, bytes);
        const ready = this.#ra.completed(block, generation);
        this.#enqueueAppend(ready, generation);
        this.#pump();
      },
      (error) => {
        if (this.#stale(generation, signal) || isAbort(error)) return;
        this.#fail(block, generation, error);
      });
  }

  #stale(generation, signal) {
    return this.#closed || signal.aborted || generation !== this.#ra.generation;
  }

  // Appending

  #enqueueAppend(blocks, generation) {
    if (blocks.length === 0) return;
    const epoch = this.#epoch;
    this.#pendingAppends++;
    this.#appendChain = this.#appendChain.then(() => this.#appendBlocks(blocks, generation, epoch));
  }

  async #appendBlocks(blocks, generation, epoch) {
    // Between the check and the feed call nothing awaits: a seek that comes after this check also comes
    // after the call, and the feed is never asked about a block of an older generation.
    const stale = () => this.#closed || epoch !== this.#epoch || generation !== this.#ra.generation;

    try {
      for (const block of blocks) {
        if (stale() || this.#appendHalted) return;
        try {
          const bytes = this.#held.get(block);
          this.#held.delete(block);
          const segments = await this.#feed.media(block, bytes, this.#ra.offsetFor(block));

          for (const segment of segments) {
            if (stale()) return;
            await this.#streamer.append(segment.track, segment.bytes);
          }
          if (stale()) return;

          if (segments.length > 0) this.#firstMedia = true;
          this.#advanceWriteHead();
          if (block === this.#lastBlock) await this.#endIfWhole();
        } catch (error) {
          if (stale()) return;
          // A browser that rejects the stream before showing anything cannot play this file here.
          if (error instanceof StreamerError && !this.#hasFrame) { this.#giveUp(); return; }
          this.#appendHalted = true;
          this.#fail(block, generation, error);
          return;
        }
      }
    } finally {
      this.#pendingAppends--;
      if (!this.#closed) this.#pump();
    }
  }

  /** After an append: the write head is the end of the range it was in, which the append just grew. */
  #advanceWriteHead() {
    const range = rangeAt(this.#streamer.buffered(), this.#writeHead);
    if (range !== null) this.#writeHead = Math.max(this.#writeHead, range[1]);
  }

  /** Only a buffer holding the whole timeline in one piece may end the stream: ending over a hole would cut the duration at it. */
  async #endIfWhole() {
    const ranges = this.#streamer.buffered();
    const duration = this.#feed.duration;
    const whole = ranges.length === 1 && ranges[0][0] <= WHOLE_TOLERANCE
      && (!(duration > 0) || ranges[0][1] >= duration - WHOLE_TOLERANCE);
    if (whole) await this.#streamer.end();
  }

  #fail(block, generation, error) {
    this.#ra.failed(block, generation);
    if (generation !== this.#ra.generation) return;
    if (this.#failure === null || block < this.#failure.block) this.#failure = { error, block };
    this.#refresh();
  }

  // Seeking

  #onSeeking = () => {
    if (!this.#ready) return;

    const time = this.#video.currentTime;
    const decision = seekDecision({ time, buffered: this.#streamer.buffered(), seekable: this.#feed.seekable });
    if (decision.kind === 'snap') {
      // Setting the time again to the value it has seeks again, and the seek would land here once more.
      if (Math.abs(decision.time - time) > 1e-6) this.#video.currentTime = decision.time;
    } else if (decision.kind === 'read') {
      this.#restartAt(time);
    }
    this.#pump();
  };

  /**
   * Ends everything the reads so far started and carries on from `time`: abort what is being read, give
   * the parser a clean start, ask the feed where `time` lives, and read from there. The first segment
   * comes from inside its block, at the offset the feed answered.
   */
  #restartAt(time) {
    const epoch = ++this.#epoch;
    this.#reads.abort();
    this.#reads = new AbortController();
    this.#held.clear();
    this.#firstMedia = false;
    this.#appendHalted = false;
    this.#failure = null;
    this.#errorReported = false;
    this.#seekPending = true;
    this.#writeHead = time;
    this.#refresh();

    this.#seekTo(time, epoch).catch((error) => {
      if (this.#closed || epoch !== this.#epoch) return;
      this.#seekPending = false;
      this.#fatal = error;
      this.#refresh();
    });
  }

  async #seekTo(time, epoch) {
    await this.#streamer.abort();
    if (this.#closed || epoch !== this.#epoch) return;

    const target = await this.#feed.seekTo(time);
    if (this.#closed || epoch !== this.#epoch) return;

    // A feed that is open answers every time its `seekable` allowed a read; null means it was closed under us.
    if (target === null) throw new Error('the file cannot be entered at that time');

    this.#seekPending = false;
    this.#ra.restart(target.block, target.offset);
    this.#pump();
  }

  /**
   * Reads must carry on where the playhead plays, which after a jump and a step back is not where the
   * last seek began. Returns true when it started a new generation, which ends this round of reading.
   */
  #followPlayhead() {
    if (!this.#started || !shouldRead(this.#readState())) return false;

    const next = continuation({ buffered: this.#streamer.buffered(), time: this.#video.currentTime, anchor: this.#writeHead });
    if (next === null) return false;

    this.#restartAt(next.time);
    return true;
  }

  // The player

  #createVideo() {
    const video = this.#root.ownerDocument.createElement('video');
    video.controls = true;
    video.playsInline = true;
    video.preload = 'metadata';
    video.setAttribute('aria-label', downloadName(this.#record.manifest));
    if (this.#posterUrl !== null) video.poster = this.#posterUrl;

    this.#overlay = this.#root.querySelector('.overlay');
    this.#overlayText = this.#root.querySelector('.overlay-text');
    this.#root.prepend(video);
    this.#video = video;
    return video;
  }

  #watch(video) {
    const on = (type, handler) => {
      video.addEventListener(type, handler);
      this.#listeners.push([type, handler]);
    };

    on('loadedmetadata', () => this.#onSize('content', video.videoWidth, video.videoHeight));
    on('play', () => { this.#started = true; this.#pump(); });
    on('seeking', this.#onSeeking);
    on('error', () => {
      if (!this.#hasFrame) { this.#giveUp(); return; }
      this.#fatal = new Error('the browser could not play this video');
      this.#refresh();
    });
    for (const type of ['loadeddata', 'canplay', 'playing', 'seeked', 'timeupdate', 'waiting', 'stalled', 'pause', 'ended']) {
      on(type, () => this.#pump());
    }
  }

  async #loadCover() {
    try {
      const cover = this.#givenCover !== undefined ? this.#givenCover : await this.#handle.cover();
      if (!cover || this.#closed) return;

      const url = URL.createObjectURL(new Blob([cover.bytes], { type: cover.type }));
      this.#urls.push(url);
      this.#posterUrl = url;
      if (this.#video !== null) this.#video.poster = url;

      // The element takes the cover's proportions as soon as the picture has decoded.
      const image = this.#root.ownerDocument.createElement('img');
      image.addEventListener('load', () => {
        if (!this.#closed) this.#onSize('cover', image.naturalWidth, image.naturalHeight);
      });
      image.src = url;
    } catch {
      // A cover that cannot be read or shown never fails the video it decorates.
    }
  }

  /**
   * The browser refused the stream before anything was shown: not a failure of the file, so no error. Before
   * `start` has finished it answers with the download outcome; after, through `onDownload`.
   */
  #giveUp() {
    if (this.#gaveUp || this.#closed) return;
    this.#gaveUp = true;
    if (this.#ready) this.#onDownload('unplayable');
  }

  // The overlay

  #refresh() {
    if (this.#closed || this.#video === null || !this.#ready) return;

    const video = this.#video;
    if (!this.#hasFrame && video.readyState >= HAVE_CURRENT_DATA) {
      this.#hasFrame = true;
      this.#onLoad();
    }
    if (this.#closed) return;

    const time = video.currentTime;
    const playable = this.#streamer.buffered().some(([start, end]) => time >= start - EDGE && time < end - EDGE);
    const state = this.#fatal !== null ? 'error' : overlayState({
      // A browser that does not want data yet (a managed source before play) is not buffering.
      started: this.#hasFrame || !this.#streamer.streaming,
      waiting: video.readyState < HAVE_FUTURE_DATA && (!video.paused || video.seeking),
      readsInFlight: this.#ra.inFlight + (this.#seekPending ? 1 : 0),
      retrying: this.#retrying > 0,
      stoppedAt: this.#ra.stoppedAt,
      atGap: !this.#seekPending && !playable,
    });

    this.#render(state);
  }

  #render(state) {
    const overlay = this.#overlay;
    if (overlay === null) return;

    overlay.hidden = state === 'none';
    overlay.classList.toggle('failed', state === 'error');
    if (state === 'none') return;

    if (state === 'buffering') {
      this.#overlayText.textContent = this.#retrying > 0 ? RETRYING : BUFFERING;
      this.#overlayText.setAttribute('role', 'status');
      return;
    }

    const error = this.#fatal ?? this.#failure?.error ?? null;
    this.#overlayText.textContent = messageFor(error, { playing: this.#hasFrame });
    this.#overlayText.setAttribute('role', 'alert');
    if (!this.#errorReported) {
      this.#errorReported = true;
      this.#onError(error, { playing: this.#hasFrame });
    }
  }

  // Ending

  /** Aborts every read, closes the source, and gives every blob URL back. */
  close() {
    if (this.#closed) return;
    this.#closed = true;

    this.#lifetime.abort();
    this.#reads.abort();
    clearInterval(this.#tick);
    for (const [type, handler] of this.#listeners) this.#video?.removeEventListener(type, handler);
    this.#listeners = [];
    this.#held.clear();

    try { this.#feed?.close(); } catch { /* a feed that is already closed has nothing left to release */ }
    this.#streamer?.close();
    for (const url of this.#urls) URL.revokeObjectURL(url);
    this.#urls = [];
    this.#video?.remove();
    if (this.#overlay !== null) this.#overlay.hidden = true;
  }
}
