// The hero's read of its file, without any drawing: the reader wired to the pure state in
// mosaic-state.mjs, the stall clock, the failure path and the release of the connection. A renderer
// (cloud.mjs) listens to what the read really did and draws only that.
//
// Order of events, each reported once and never ahead of the reader: onManifest, then onChunk per
// accepted chunk, onBlockVerified when a block's hash matched, onDeveloping once every block is
// verified, onDone after the renderer's own settling time. onFailed replaces whatever would follow.
import { ENDPOINTS, LedgerClient, XrplFileReader, parseLink } from './xrpl.mjs';
import {
  allVerified,
  blockVerified,
  checkStall,
  chunkArrived,
  createMosaic,
  developed,
  failed,
} from './mosaic-state.mjs';

export const SEEN_KEY = 'xf-mosaic-seen';

const STALL_POLL_MS = 500;
// Cubes the hero shows before the manifest says how many chunks there are.
const PLACEHOLDER_CHUNKS = 1;

function sessionStorageOrNull() {
  try {
    return globalThis.sessionStorage ?? null;
  } catch {
    return null;
  }
}

/** True when this browser session has already played the animation (blocked storage reads as false). */
export function hasSeenMosaic(storage = sessionStorageOrNull()) {
  try {
    return storage?.getItem(SEEN_KEY) === '1';
  } catch {
    return false;
  }
}

export function markMosaicSeen(storage = sessionStorageOrNull()) {
  try {
    storage?.setItem(SEEN_KEY, '1');
  } catch {
    // Not remembered; the next visit plays the full animation again.
  }
}

function defaultCreateReader(parsed) {
  const endpoints = ENDPOINTS[parsed.network];
  if (!endpoints) {
    const error = new Error(`no public node is configured for network ${parsed.network}`);
    error.code = 'UnsupportedNetwork';
    throw error;
  }
  const client = new LedgerClient(endpoints);
  return { read: (...args) => new XrplFileReader(client).read(...args), close: () => client.close() };
}

/**
 * Starts reading `options.link` and reports the read to `options.listener`.
 *
 * @param {object} options
 * @param {string|{network: string, txHash: string}} options.link `xrpl-file://...` text or a parsed link
 * @param {object} [options.listener] optional callbacks, each called with one object:
 *   onManifest({ manifest, chunks, time }), onChunk({ index, bytes, ledger, time }),
 *   onBlockVerified({ firstChunk, lastChunk, time }), onDeveloping({ time }), onDone({ time }),
 *   onFailed({ error }); every phase change also calls onPhase(phase)
 * @param {() => number} [options.developDelay] milliseconds from the last block's verification to
 *   onDone: the renderer's own settling time, 0 when absent
 * @param {object} [env] seams for tests; defaults are the browser's own
 * @returns {{ done: Promise<void>, destroy: () => void, readonly phase: string, readonly state: object,
 *   readonly manifest: object|null }} `done` settles, and never rejects, once the read is done or
 *   failed; `destroy()` abandons the read and releases the connection, the poll and the timer.
 */
export function startHeroRead(options, env = {}) {
  const {
    now = () => performance.now(),
    setTimer = (callback, ms) => setTimeout(callback, ms),
    clearTimer = (id) => clearTimeout(id),
    setPoll = (callback, ms) => setInterval(callback, ms),
    clearPoll = (id) => clearInterval(id),
    createReader = defaultCreateReader,
    storage = sessionStorageOrNull(),
  } = env;
  const listener = options.listener ?? {};
  const developDelay = options.developDelay ?? (() => 0);

  let state = createMosaic({ chunks: PLACEHOLDER_CHUNKS, now: now() });
  let manifest = null;
  let source = null;
  let pollId = null;
  let developTimer = null;
  let settled = false;
  let resolveDone;
  const done = new Promise((resolve) => { resolveDone = resolve; });

  function stopWatching() {
    if (pollId !== null) {
      clearPoll(pollId);
      pollId = null;
    }
  }

  /** The read is over for this page, one way or the other: free the connection and the stall clock. */
  function release() {
    stopWatching();
    const closing = source;
    source = null;
    try {
      closing?.close();
    } catch {
      // A socket that is already gone has nothing left to release.
    }
  }

  function fail(error) {
    if (settled) return;
    const next = failed(state, error);
    if (next === state) return;
    settled = true;
    state = next;
    if (developTimer !== null) clearTimer(developTimer);
    release();
    listener.onPhase?.(state.phase);
    listener.onFailed?.({ error: state.error });
    resolveDone();
  }

  function finish() {
    if (settled || state.phase !== 'developing') return;
    settled = true;
    state = developed(state);
    markMosaicSeen(storage);
    listener.onPhase?.(state.phase);
    listener.onDone?.({ time: now() });
    resolveDone();
  }

  const hooks = {
    onManifest(received) {
      if (settled) return;
      if (state.phase !== 'reading' || manifest !== null) return;
      manifest = received;
      const chunks = Math.ceil(received.size / received.chunk);
      state = createMosaic({ chunks, now: state.activityAt });
      listener.onManifest?.({ manifest: received, chunks, time: now() });
    },
    onChunk(chunk) {
      if (settled) return;
      if (state.phase !== 'reading' || manifest === null) return;
      const time = now();
      const next = chunkArrived(state, chunk, time);
      if (next === state) return;
      state = next;
      listener.onChunk?.({ index: chunk.index, bytes: chunk.bytes, ledger: chunk.ledger, time });
    },
    onBlockVerified({ firstChunk, lastChunk }) {
      if (settled) return;
      if (state.phase !== 'reading' || manifest === null) return;
      state = blockVerified(state, { firstChunk, lastChunk });
      listener.onBlockVerified?.({ firstChunk, lastChunk: Math.min(lastChunk, state.chunks - 1), time: now() });
    },
  };

  function onRead() {
    release();
    if (settled || state.phase !== 'reading') return;
    state = allVerified(state);
    if (state.phase !== 'developing') {
      fail({ code: 'Incomplete', message: 'the read finished with blocks that were never verified' });
      return;
    }
    listener.onPhase?.(state.phase);
    listener.onDeveloping?.({ time: now() });
    // A timer, not the renderer's frame loop: a hidden tab runs no frames and the card must still come.
    developTimer = setTimer(finish, Math.max(0, developDelay()));
  }

  // The stall clock starts now, not at the manifest: a node that never answers the first request
  // must end in the failure state too.
  pollId = setPoll(() => {
    const next = checkStall(state, now());
    if (next !== state) fail(next.error);
  }, STALL_POLL_MS);

  try {
    const parsed = typeof options.link === 'string' ? parseLink(options.link.trim()) : options.link;
    source = createReader(parsed);
    Promise.resolve(source.read(parsed, () => {}, hooks))
      .then(onRead, fail)
      .catch((error) => fail({ code: 'Unknown', message: error?.message }));
  } catch (error) {
    fail(error);
  }

  return {
    done,
    get phase() {
      return state.phase;
    },
    get state() {
      return state;
    },
    get manifest() {
      return manifest;
    },
    destroy() {
      release();
      if (developTimer !== null) clearTimer(developTimer);
      settled = true;
      resolveDone();
    },
  };
}
