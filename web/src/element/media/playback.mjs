// What decides when video blocks are read, retried and appended, apart from the browser: the
// read-ahead window, the retry delays, the answers to a seek, the overlay and the quota trim.
// Pure functions and one small state machine, so node --test pins them; video.mjs is the glue that
// feeds them the clock, the buffer and the events of a real <video>.
//
// Adapted from the video platform's js/player.mjs (E:\GIT\XRPL\xrpl-video-platform): the 30 s read-ahead,
// the generation counter that makes a seek drop what an earlier one started, and the 120 s trim.

/** Seconds of video kept buffered ahead of the playhead while playing. */
export const AHEAD_SECONDS = 30;
/** Block reads in flight at once (design spec section 3). */
export const READ_AHEAD = 3;
/** On a full buffer, what is further than this from the playhead is given up. */
export const KEEP_SECONDS = 120;
/** The longest wait between two tries of a block the node did not answer. */
export const RETRY_CAP_MS = 30_000;

// A range that ends where the visitor aimed holds nothing to play: the last tenth of a second does not count.
const END_SLACK = 0.1;

/**
 * How long to wait before reading a block again, or null when waiting cannot help. Only a node that
 * did not answer is worth another try, with no limit on tries: the element retries for as long as
 * it is connected (Spec question 6). A block that failed verification is read the same way every time.
 */
export function retryDelay(error, attempt) {
  if (error?.code !== 'NodeUnreachable') return null;
  return Math.min(1000 * 2 ** attempt, RETRY_CAP_MS);
}

/**
 * The window of blocks being read, and where the next append goes.
 *
 * It holds the blocks [appended, appended + limit): a read that finishes early waits for the ones
 * before it, and the wait counts against the window, so a slow block holds back the reads behind it
 * instead of letting finished ones pile up in memory.
 *
 * Every `restart` begins a new generation. A read or a failure that reports under an older number is
 * dropped, which is how a seek ends what the seek before it started.
 */
export class ReadAhead {
  #blocks;
  #limit;
  #generation = 0;
  #active = false;
  #fromBlock = 0;
  #fromOffset = 0;
  #issued = 0;
  #appended = 0;
  #reading = new Set();
  #done = new Set();
  #stoppedAt = null;

  constructor({ blocks, limit = READ_AHEAD }) {
    if (!Number.isInteger(blocks) || blocks < 0) throw new RangeError('a file has a whole number of blocks');
    if (!Number.isInteger(limit) || limit < 1) throw new RangeError('at least one read runs at a time');
    this.#blocks = blocks;
    this.#limit = limit;
  }

  /**
   * Starts a generation at `fromBlock`; `fromOffset` is the file offset the first block's bytes are
   * wanted from, as the feed's seek answered.
   * @returns the generation number reads and failures report under
   */
  restart(fromBlock, fromOffset) {
    if (!Number.isInteger(fromBlock) || fromBlock < 0 || fromBlock > this.#blocks) {
      throw new RangeError(`block ${fromBlock} is outside a file of ${this.#blocks} blocks`);
    }

    this.#generation++;
    this.#active = true;
    this.#fromBlock = fromBlock;
    this.#fromOffset = fromOffset;
    this.#issued = fromBlock;
    this.#appended = fromBlock;
    this.#reading.clear();
    this.#done.clear();
    this.#stoppedAt = null;
    return this.#generation;
  }

  /** The lowest block not yet handed out, or null: window full, end of file, or a failure at or before it. */
  next() {
    if (!this.#active) return null;

    const block = this.#issued;
    if (block >= this.#blocks) return null;
    if (this.#stoppedAt !== null && block >= this.#stoppedAt) return null;
    if (block >= this.#appended + this.#limit) return null;

    this.#issued++;
    this.#reading.add(block);
    return { block, generation: this.#generation };
  }

  /** The blocks that can now be appended, in file order; empty for an old generation or a block not being read. */
  completed(block, generation) {
    if (generation !== this.#generation || !this.#reading.delete(block)) return [];

    this.#done.add(block);
    const ready = [];
    while (this.#done.delete(this.#appended)) ready.push(this.#appended++);
    return ready;
  }

  /** Nothing at or past `block` is handed out from now on. Also the way to report a block that could not be appended. */
  failed(block, generation) {
    if (generation !== this.#generation) return;

    this.#reading.delete(block);
    this.#stoppedAt = this.#stoppedAt === null ? block : Math.min(this.#stoppedAt, block);
  }

  /** The file offset to append `block` from: inside the block for the first one of a generation, else undefined (its start). */
  offsetFor(block) {
    return block === this.#fromBlock ? this.#fromOffset : undefined;
  }

  get generation() { return this.#generation; }

  /** The first block of the generation that is not yet appended: the one playback needs next. */
  get frontier() { return this.#appended; }

  /** The lowest block that failed, or null. */
  get stoppedAt() { return this.#stoppedAt; }

  get inFlight() { return this.#reading.size; }

  get fromOffset() { return this.#fromOffset; }

  /** Every block through the file's last has been appended in this generation. */
  get finished() { return this.#active && this.#appended >= this.#blocks; }
}

/**
 * Whether to start another block read. Before the first `play` only what the first frame needs is
 * read (Spec question 11), so a page of videos costs a block each; afterwards reading follows the
 * playhead, up to `AHEAD_SECONDS` ahead and only while playing.
 */
export function shouldRead({ started, playing, streamingAllowed, bufferedAhead, firstMediaAppended }) {
  if (!streamingAllowed) return false;
  if (!started) return !firstMediaAppended;
  return playing && bufferedAhead < AHEAD_SECONDS;
}

/** The range holding `time`, with a little slack at both ends, or null. */
export function rangeAt(buffered, time, slack = END_SLACK) {
  return buffered.find(([start, end]) => time >= start - slack && time <= end + slack) ?? null;
}

/** The point of the buffer closest to `time`: `time` itself when it is inside, else the end of a range nearest to it; null when nothing is buffered. */
export function nearestBuffered(time, buffered) {
  let best = null;
  for (const [start, end] of buffered) {
    if (time >= start && time <= end) return time;
    for (const edge of [start, end]) {
      if (best === null || Math.abs(edge - time) < Math.abs(best - time)) best = edge;
    }
  }
  return best;
}

/**
 * What a seek needs. `buffered`: the playhead is somewhere playable and nothing is read. Otherwise
 * `read` when the file has an index to find the block in, and `snap` (to the nearest point of the
 * buffer, or the start when there is none) when it has not: such a file plays on from where it is.
 */
export function seekDecision({ time, buffered, seekable }) {
  if (buffered.some(([start, end]) => time >= start && time < end - END_SLACK)) return { kind: 'buffered' };
  if (seekable) return { kind: 'read' };
  return { kind: 'snap', time: nearestBuffered(time, buffered) ?? 0 };
}

/**
 * Where reads should carry on when the playhead plays in a range that the current reads do not
 * write to: after a jump forward and a step back, the reads write at the jump while the playhead
 * runs into the end of the older range. `anchor` is the time the current reads began at. null when
 * the playhead is in no range (a seek decides that) or the reads already write where it plays.
 */
export function continuation({ buffered, time, anchor }) {
  const range = rangeAt(buffered, time);
  if (range === null) return null;

  const [start, end] = range;
  if (anchor >= start - END_SLACK && anchor <= end + END_SLACK) return null;
  return { time: end };
}

/**
 * What the buffering overlay shows. `started` is whether the first frame is on screen. The error
 * shows only where playback stands at the gap a failed block left, never while earlier video plays.
 * @returns 'none' | 'buffering' | 'error'
 */
export function overlayState({ started, waiting, readsInFlight, retrying, stoppedAt, atGap }) {
  if (stoppedAt !== null && atGap) return 'error';
  if (!started) return 'buffering';
  if (waiting && (readsInFlight > 0 || retrying)) return 'buffering';
  return 'none';
}

/** The parts of `buffered` further than `keep` seconds from `time`: what a full buffer gives up. */
export function farRanges(buffered, time, keep = KEEP_SECONDS) {
  const far = [];
  for (const [start, end] of buffered) {
    const before = Math.min(end, time - keep);
    if (before > start) far.push([start, before]);

    const after = Math.max(start, time + keep);
    if (end > after) far.push([after, end]);
  }
  return far;
}

/** The stretches that two sorted lists of ranges share: what a video and an audio buffer both hold. */
export function intersectRanges(a, b) {
  const shared = [];
  let i = 0;
  let j = 0;
  while (i < a.length && j < b.length) {
    const start = Math.max(a[i][0], b[j][0]);
    const end = Math.min(a[i][1], b[j][1]);
    if (end > start) shared.push([start, end]);
    if (a[i][1] < b[j][1]) i++;
    else j++;
  }
  return shared;
}
