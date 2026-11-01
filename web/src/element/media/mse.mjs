// Feeds segments into a <video> through Media Source Extensions.
//
// Adapted from the video platform's js/streamer.mjs (E:\GIT\XRPL\xrpl-video-platform): the choice of
// ManagedMediaSource over MediaSource, `segments` mode, the serialised appends and the quota drop. What
// is new here is one SourceBuffer per track (a plain MP4 is cut into separate video and audio
// segments), the managed source's streaming switch, and the quota drop built on farRanges.
//
// A SourceBuffer takes one append or remove at a time and throws when asked for another while
// `updating`, so every operation of a buffer goes through that buffer's queue.

import { farRanges, intersectRanges } from './playback.mjs';

/**
 * A failure of the source: `code` is `unsupported` when the browser cannot play the tracks at all
 * (the download view says so) and `failed` for anything that went wrong while streaming.
 */
export class StreamerError extends Error {
  constructor(message, { code = 'failed', cause = null } = {}) {
    super(message, cause ? { cause } : undefined);
    this.name = 'StreamerError';
    this.code = code;
  }
}

function isQuota(error) {
  return error?.name === 'QuotaExceededError' || error?.cause?.name === 'QuotaExceededError';
}

/** A TimeRanges as [start, end] pairs. */
function rangesOf(sourceBuffer) {
  const ranges = [];
  let timeRanges;
  try {
    timeRanges = sourceBuffer.buffered;
  } catch {
    // A buffer that was removed from its source throws; it holds nothing.
    return ranges;
  }
  for (let i = 0; i < timeRanges.length; i++) ranges.push([timeRanges.start(i), timeRanges.end(i)]);
  return ranges;
}

export class Streamer {
  #video;
  #tracks;
  #duration;
  #managed;
  #mediaSource;
  #buffers = new Map();
  #url = null;
  #closed = false;
  #streamingCallbacks = [];
  #abortOpen = null;

  /**
   * @param {HTMLVideoElement} video
   * @param {{ id: number, mime: string }[]} tracks one SourceBuffer each
   * @param {number} duration seconds, or 0 when the file does not say
   * @throws {StreamerError} with code `unsupported` when the browser has no Media Source Extensions or refuses a track
   */
  constructor(video, tracks, duration) {
    // Safari on iPhone ships ManagedMediaSource rather than the plain one; checking only for MediaSource
    // would turn every iPhone away.
    const Source = globalThis.ManagedMediaSource ?? globalThis.MediaSource;
    if (!Source) throw new StreamerError('this browser has no Media Source Extensions', { code: 'unsupported' });

    for (const track of tracks) {
      // Asked of the class that will be used: the two can disagree about the same type.
      if (!Source.isTypeSupported(track.mime)) {
        throw new StreamerError(`this browser cannot play ${track.mime}`, { code: 'unsupported' });
      }
    }

    this.#video = video;
    this.#tracks = tracks;
    this.#duration = duration;
    this.#managed = Source === globalThis.ManagedMediaSource;
    this.#mediaSource = new Source();
  }

  /** Whether the browser wants data now. A plain MediaSource always does; a managed one says when it has enough. */
  get streaming() {
    return this.#managed ? this.#mediaSource.streaming === true : true;
  }

  /** Called with true on `startstreaming` and false on `endstreaming`; a plain MediaSource never calls it. */
  onStreamingChange(callback) {
    this.#streamingCallbacks.push(callback);
  }

  /** Attaches the source to the video and waits for it to open, then makes one buffer per track. */
  async open() {
    if (this.#closed) throw new StreamerError('the stream was closed');

    const opened = new Promise((resolve, reject) => {
      this.#abortOpen = () => reject(new StreamerError('the stream was closed'));
      this.#mediaSource.addEventListener('sourceopen', resolve, { once: true });
      this.#mediaSource.addEventListener('error', () => reject(new StreamerError('the media source failed to open')), { once: true });
    });

    if (this.#managed) {
      // A managed source starts loading only once the element may not cast: without this it sits idle on iOS.
      this.#video.disableRemotePlayback = true;
      this.#mediaSource.addEventListener('startstreaming', this.#onStartStreaming);
      this.#mediaSource.addEventListener('endstreaming', this.#onEndStreaming);
    }

    this.#url = URL.createObjectURL(this.#mediaSource);
    this.#video.src = this.#url;
    await opened;
    this.#abortOpen = null;
    if (this.#closed) throw new StreamerError('the stream was closed');

    for (const track of this.#tracks) {
      let sourceBuffer;
      try {
        sourceBuffer = this.#mediaSource.addSourceBuffer(track.mime);
      } catch (error) {
        throw new StreamerError(`this browser cannot play ${track.mime}`, { code: 'unsupported', cause: error });
      }

      // `segments` honours each fragment's own timestamps, so a fragment read after a seek lands where it
      // belongs. `sequence` would stack it behind whatever came before.
      sourceBuffer.mode = 'segments';
      this.#buffers.set(track.id, { sourceBuffer, queue: Promise.resolve() });
    }

    // Before the first append: it makes the whole timeline seekable at once. Without it `seekable` is only
    // what is buffered, and a jump past the loaded part snaps back instead of asking for the fragment.
    if (Number.isFinite(this.#duration) && this.#duration > 0) this.#mediaSource.duration = this.#duration;
  }

  #onStartStreaming = () => { for (const callback of this.#streamingCallbacks) callback(true); };

  #onEndStreaming = () => { for (const callback of this.#streamingCallbacks) callback(false); };

  /**
   * Appends one segment to its track's buffer. Appends of a buffer are serialised, and the promise
   * settles when the buffer has taken the bytes. A full buffer is not a broken file: it gives up what is
   * far from the playhead and tries once more.
   */
  append(track, bytes) {
    const buffer = this.#buffers.get(track);
    if (buffer === undefined) return Promise.reject(new StreamerError(`this stream has no track ${track}`));

    const run = buffer.queue.then(() => this.#appendNow(buffer.sourceBuffer, bytes));
    // One failed append must not fail the ones queued behind it: the caller of each hears its own result.
    buffer.queue = run.catch(() => {});
    return run;
  }

  async #appendNow(sourceBuffer, bytes) {
    if (this.#closed) return;

    try {
      await this.#push(sourceBuffer, bytes);
    } catch (error) {
      if (!isQuota(error)) throw this.#failure(error);

      const far = farRanges(rangesOf(sourceBuffer), this.#video.currentTime);
      if (far.length === 0) throw this.#failure(error);

      for (const [from, to] of far) await this.#remove(sourceBuffer, from, to);
      try {
        await this.#push(sourceBuffer, bytes);
      } catch (again) {
        throw this.#failure(again);
      }
    }
  }

  #failure(error) {
    if (error instanceof StreamerError) return error;
    return new StreamerError(`the browser refused the data: ${error?.message ?? error}`, { cause: error });
  }

  #push(sourceBuffer, bytes) {
    return new Promise((resolve, reject) => {
      const cleanup = () => {
        sourceBuffer.removeEventListener('updateend', onDone);
        sourceBuffer.removeEventListener('error', onFail);
      };
      const onDone = () => { cleanup(); resolve(); };
      const onFail = () => { cleanup(); reject(new StreamerError('the decoder rejected these bytes: the segment is damaged or out of order')); };

      sourceBuffer.addEventListener('updateend', onDone);
      sourceBuffer.addEventListener('error', onFail);

      try {
        sourceBuffer.appendBuffer(bytes);
      } catch (error) {
        cleanup();
        reject(error);
      }
    });
  }

  #remove(sourceBuffer, from, to) {
    return new Promise((resolve) => {
      const done = () => { sourceBuffer.removeEventListener('updateend', done); resolve(); };
      sourceBuffer.addEventListener('updateend', done);

      try {
        sourceBuffer.remove(from, to);
      } catch {
        sourceBuffer.removeEventListener('updateend', done);
        resolve();
      }
    });
  }

  /**
   * Throws away each parser's half-read box, so the next append may start anywhere. A seek hands the
   * decoder bytes from the middle of the file; without this the first box of the new place lands inside
   * whatever box the parser was still waiting for. Queued behind the appends before it, because
   * aborting between an `appendBuffer` and its `updateend` is an error.
   */
  abort() {
    const runs = [];
    for (const buffer of this.#buffers.values()) {
      const run = buffer.queue.then(() => {
        // Not allowed once the stream has ended; the parser is clean then, the last block being the file's end.
        if (this.#closed || this.#mediaSource.readyState !== 'open') return;
        try {
          buffer.sourceBuffer.abort();
        } catch {
          // Nothing was being parsed, which is what abort was asked for.
        }
      });
      buffer.queue = run;
      runs.push(run);
    }
    return Promise.all(runs).then(() => undefined);
  }

  /** What every track holds at once, in seconds: the stretches that can be played with sound and picture. */
  buffered() {
    const lists = [...this.#buffers.values()].map((buffer) => rangesOf(buffer.sourceBuffer));
    if (lists.length === 0) return [];
    return lists.reduce((all, one) => intersectRanges(all, one));
  }

  /** Tells the decoder no more segments are coming, so the duration settles and playback can end. */
  async end() {
    await Promise.all([...this.#buffers.values()].map((buffer) => buffer.queue));
    if (this.#closed || this.#mediaSource.readyState !== 'open') return;

    try {
      this.#mediaSource.endOfStream();
    } catch {
      // A buffer is still updating or the source closed under us; the next append reopens a stream that ended.
    }
  }

  /** Detaches everything, so another view does not stack a MediaSource on this one. */
  close() {
    if (this.#closed) return;
    this.#closed = true;

    this.#abortOpen?.();
    this.#mediaSource.removeEventListener('startstreaming', this.#onStartStreaming);
    this.#mediaSource.removeEventListener('endstreaming', this.#onEndStreaming);
    this.#streamingCallbacks = [];

    if (this.#url !== null) {
      URL.revokeObjectURL(this.#url);
      this.#url = null;
    }

    this.#video.removeAttribute('src');
    this.#video.load();
  }
}
