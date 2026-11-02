// The chunk mosaic: a real xrpl-file read from the ledger and drawn on a canvas, one cell per chunk.
// A cell lights when its chunk arrives, turns accent when its block's hash matches, and once every block
// is verified the cells turn over into the picture. Only what was read and verified is shown; the
// picture is decoded from the verified bytes and never before. State and timing rules live in
// mosaic-state.mjs; this file is the reader wiring and the drawing.
import { ENDPOINTS, LedgerClient, XrplFileReader, formatLink, parseLink } from './xrpl.mjs';
import { pathFromLink } from './route.mjs';
import {
  allVerified,
  blockVerified,
  checkStall,
  chunkArrived,
  createMosaic,
  developDelay,
  developed,
  failed,
  motionTimings,
  textureSpan,
  waveDelay,
} from './mosaic-state.mjs';

export const SEEN_KEY = 'xf-mosaic-seen';

// The grid drawn before the manifest says how many chunks there are, so the hero is not empty while
// the first request is in flight. About the size of the hero file (178,448 bytes).
const PLACEHOLDER_CHUNKS = 180;
const STALL_POLL_MS = 500;
const SVG_NS = 'http://www.w3.org/2000/svg';
const FAILED_ALPHA = 0.6;
const MAX_VEIL = 0.35;
// Pause between the last cell turning accent and the cells turning over; a replay skips it.
const DEVELOP_PAUSE_MS = 240;

/**
 * English placeholders so the mosaic works on its own (tests, a page without copy). The site passes
 * its own EN/RU copy through `options.strings`; none of this text ships as final copy.
 */
const DEFAULT_STRINGS = {
  label: (phase, { chunks }) => {
    const count = chunks === null ? '' : `, ${chunks} chunks`;
    return {
      reading: `A picture being read from the ledger${count}`,
      developing: `The picture is verified${count}`,
      done: `The picture, read from the ledger and verified${count}`,
      failed: 'The picture could not be read from the ledger',
    }[phase];
  },
  caption: ({ chunks, firstLedger, lastLedger }) => `verified · ${chunks} chunks · ledgers ${firstLedger}–${lastLedger}`,
  failed: () => 'The picture could not be read from the ledger just now.',
  fileLinkLabel: 'Open the file page',
};

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

const progress = (elapsed, duration) => {
  if (duration <= 0) return elapsed >= 0 ? 1 : 0;
  const p = Math.min(1, Math.max(0, elapsed / duration));
  return 1 - (1 - p) ** 3;
};

/**
 * Draws the mosaic on `canvas` and reads the file behind `options.link` into it.
 *
 * @param {HTMLCanvasElement} canvas Sized by its CSS box (width); the height follows `aspect`. It gets
 *   role="img", and its aria-label follows the phase.
 * @param {object} options
 * @param {string|{network: string, txHash: string}} options.link `xrpl-file://...` text or a parsed link
 * @param {number} options.aspect picture width / height; the manifest carries no dimensions
 * @param {boolean} [options.reducedMotion] cells change without animation and the picture appears when
 *   verified; defaults to the visitor's prefers-reduced-motion
 * @param {boolean} [options.fast] shortens every animation (the sequence after the last chunk takes under 300 ms); defaults to
 *   whether this browser session has already seen the mosaic (sessionStorage `xf-mosaic-seen`)
 * @param {HTMLElement} [options.caption] gets role="status" and a polite live region: it stays empty
 *   while reading, then holds the verified line or the failure message. It carries data-phase.
 * @param {number} [options.expectedChunks] chunk count of the placeholder grid drawn until the manifest
 *   arrives (default 180); the real count replaces it
 * @param {object} [options.strings] all visible and spoken text, supplied by the caller:
 *   - label(phase, { chunks }) -> the canvas's accessible name; phase is 'reading' | 'developing' |
 *     'done' | 'failed'; chunks is null until the manifest has arrived
 *   - caption({ chunks, firstLedger, lastLedger }) -> the line shown when verified, without the check
 *     mark, which the mosaic draws itself as an inline SVG (the site's fonts have no U+2713)
 *   - failed -> the quiet message when the read stopped; a string, or a function of the error code
 *     ('NodeLacksHistory', 'NodeUnreachable', 'Stalled', ...)
 *   - fileLinkLabel -> text of the link to the file's page (`pathFromLink(link)`)
 * @param {object} [env] seams for tests; defaults are the browser's own
 * @returns {{ done: Promise<void>, destroy: () => void, readonly phase: string }} `done` settles, and
 *   never rejects, once the mosaic has developed or failed; `destroy()` stops everything and abandons
 *   the read.
 */
export function mountMosaic(canvas, options, env = {}) {
  const doc = canvas.ownerDocument;
  const view = doc.defaultView;
  const {
    now = () => performance.now(),
    requestFrame = (callback) => view.requestAnimationFrame(callback),
    cancelFrame = (id) => view.cancelAnimationFrame(id),
    setTimer = (callback, ms) => setTimeout(callback, ms),
    clearTimer = (id) => clearTimeout(id),
    setPoll = (callback, ms) => setInterval(callback, ms),
    clearPoll = (id) => clearInterval(id),
    decode = (bytes, type) => createImageBitmap(new Blob([bytes], { type })),
    createReader = defaultCreateReader,
    storage = sessionStorageOrNull(),
  } = env;
  const { caption = null, aspect, expectedChunks = PLACEHOLDER_CHUNKS } = options;
  const strings = { ...DEFAULT_STRINGS, ...options.strings };
  const reducedMotion = options.reducedMotion ?? (view.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false);
  const fast = options.fast ?? hasSeenMosaic(storage);
  const timings = motionTimings({ fast, reducedMotion });

  const context = canvas.getContext('2d');
  const mountedAt = now();
  let state = createMosaic({ chunks: expectedChunks, aspect, now: mountedAt });
  let manifest = null;
  let chunkCount = null;
  let litAt = [];
  let verifiedAt = [];
  // The end of the last lit/verified transition: the turn-over starts after it.
  let animationEnd = 0;
  // Set once the verified bytes are decoded: the picture and when the turn-over starts and ends.
  let reveal = null;
  let revealTimer = null;
  let source = null;
  let pollId = null;
  let frameId = null;
  let settled = false;
  let destroyed = false;
  let dpr = 1;
  let colors = null;
  let resolveDone;
  const done = new Promise((resolve) => { resolveDone = resolve; });

  canvas.setAttribute('role', 'img');
  canvas.style.aspectRatio = String(aspect);
  if (caption) {
    caption.setAttribute('role', 'status');
    caption.setAttribute('aria-live', 'polite');
  }

  const filePath = (() => {
    try {
      return pathFromLink(typeof options.link === 'string' ? options.link : formatLink(options.link));
    } catch {
      return null;
    }
  })();

  function readColors() {
    const style = view.getComputedStyle(doc.documentElement);
    const token = (name, fallback) => style.getPropertyValue(name).trim() || fallback;
    colors = {
      paper: token('--paper', '#e9eaed'),
      cell: token('--cell', '#8c8f96'),
      lit: token('--cell-lit', '#3a3d44'),
      verified: token('--cell-verified', '#0b62c4'),
    };
  }

  function slot(index, width, height) {
    const col = index % state.columns;
    const row = Math.floor(index / state.columns);
    const x0 = Math.round((col * width) / state.columns);
    const x1 = Math.round(((col + 1) * width) / state.columns);
    const y0 = Math.round((row * height) / state.rows);
    const y1 = Math.round(((row + 1) * height) / state.rows);
    return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
  }

  function drawCell(index, time, span, cellAlpha) {
    const { width, height } = canvas;
    const box = slot(index, width, height);
    const inset = Math.max(1, Math.round(Math.min(box.w, box.h) * 0.06));
    const line = Math.max(1, Math.round(dpr));
    const x = box.x + inset;
    const y = box.y + inset;
    const w = box.w - inset * 2;
    const h = box.h - inset * 2;
    const cell = state.cells[index];
    const lit = cell === 'empty' ? 0 : progress(time - litAt[index], timings.lit);
    const verified = cell === 'verified' ? progress(time - verifiedAt[index], timings.verify) : 0;

    if (lit < 1) {
      context.globalAlpha = cellAlpha * (1 - lit);
      context.strokeStyle = colors.cell;
      context.lineWidth = line;
      context.strokeRect(x + line / 2, y + line / 2, w - line, h - line);
    }
    if (lit > 0) {
      context.globalAlpha = cellAlpha * lit;
      context.fillStyle = colors.lit;
      context.fillRect(x, y, w, h);
      if (verified > 0) {
        context.globalAlpha = cellAlpha * verified;
        context.fillStyle = colors.verified;
        context.fillRect(x, y, w, h);
      }
      const tone = state.texture[index];
      if (tone !== null) {
        // Paper laid over the cell: the darker the chunk's bytes (relative to the rest), the less veil.
        context.globalAlpha = cellAlpha * lit * MAX_VEIL * (1 - (tone - span.min) / (span.max - span.min));
        context.fillStyle = colors.paper;
        context.fillRect(x, y, w, h);
      }
    }
  }

  function drawPictureSlice(index, scale) {
    const { width, height } = canvas;
    const box = slot(index, width, height);
    const col = index % state.columns;
    const row = Math.floor(index / state.columns);
    const sw = reveal.bitmap.width / state.columns;
    const sh = reveal.bitmap.height / state.rows;
    const w = box.w * scale;
    context.drawImage(reveal.bitmap, col * sw, row * sh, sw, sh, box.x + (box.w - w) / 2, box.y, w, box.h);
  }

  /** Paints one frame; returns whether another is needed. */
  function render(time) {
    if (!context || !colors || canvas.width === 0 || canvas.height === 0) return false;
    context.globalAlpha = 1;
    context.clearRect(0, 0, canvas.width, canvas.height);

    if (state.phase === 'done' && reveal) {
      context.drawImage(reveal.bitmap, 0, 0, canvas.width, canvas.height);
      return false;
    }

    const span = textureSpan(state.texture);
    const cellAlpha = state.phase === 'failed' ? FAILED_ALPHA : 1;

    if (state.phase === 'developing' && reveal) {
      for (let i = 0; i < state.columns * state.rows; i += 1) {
        const elapsed = time - reveal.startAt - developDelay(i, state.columns, state.rows, timings.developSpread);
        const turned = timings.flip > 0 ? Math.min(1, Math.max(0, elapsed / timings.flip)) : Number(elapsed >= 0);
        // A cell narrows to its centre line showing the mosaic, then widens showing the picture.
        if (turned >= 1) {
          drawPictureSlice(i, 1);
        } else if (turned > 0.5) {
          drawPictureSlice(i, (turned - 0.5) * 2);
        } else if (i < state.chunks) {
          const box = slot(i, canvas.width, canvas.height);
          const centre = box.x + box.w / 2;
          context.save();
          context.translate(centre, 0);
          context.scale(1 - turned * 2, 1);
          context.translate(-centre, 0);
          drawCell(i, time, span, cellAlpha);
          context.restore();
        }
      }
      context.globalAlpha = 1;
      return time < reveal.endAt;
    }

    for (let i = 0; i < state.chunks; i += 1) drawCell(i, time, span, cellAlpha);
    context.globalAlpha = 1;
    return time < animationEnd;
  }

  function requestDraw() {
    if (frameId !== null || destroyed) return;
    frameId = requestFrame(() => {
      frameId = null;
      if (render(now())) requestDraw();
    });
  }

  function resize() {
    dpr = view.devicePixelRatio || 1;
    const width = Math.max(1, Math.round(canvas.clientWidth * dpr));
    const height = Math.max(1, Math.round(canvas.clientHeight * dpr));
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
    }
    requestDraw();
  }

  function setLabel() {
    canvas.setAttribute('aria-label', strings.label(state.phase, { chunks: chunkCount }));
    canvas.dataset.phase = state.phase;
    if (caption) caption.dataset.phase = state.phase;
  }

  function icon(className, tag, attributes) {
    const element = doc.createElementNS(SVG_NS, 'svg');
    element.setAttribute('class', className);
    element.setAttribute('viewBox', '0 0 16 16');
    element.setAttribute('aria-hidden', 'true');
    element.setAttribute('focusable', 'false');
    const shape = doc.createElementNS(SVG_NS, tag);
    for (const [name, value] of Object.entries(attributes)) shape.setAttribute(name, value);
    element.append(shape);
    return element;
  }

  function writeCaption() {
    if (!caption) return;
    const text = doc.createElement('span');
    text.className = 'mosaic-caption__text';
    if (state.phase === 'failed') {
      const message = typeof strings.failed === 'function' ? strings.failed(state.error.code) : strings.failed;
      text.textContent = message;
      const status = doc.createElement('span');
      status.className = 'mosaic-caption__status';
      status.append(icon('mosaic-caption__mark', 'rect', { x: '3', y: '3', width: '10', height: '10' }), text);
      caption.replaceChildren(status);
      return;
    }
    text.textContent = strings.caption({ chunks: state.chunks, firstLedger: manifest.firstLedger, lastLedger: manifest.lastLedger });
    const status = doc.createElement('span');
    status.className = 'mosaic-caption__status';
    status.append(icon('mosaic-caption__check', 'path', { d: 'M3 8.5l3.5 3.5L13 4.5' }), text);
    const nodes = [status];
    if (filePath) {
      const link = doc.createElement('a');
      link.className = 'mosaic-caption__link';
      link.setAttribute('href', filePath);
      link.textContent = strings.fileLinkLabel;
      nodes.push(link);
    }
    caption.replaceChildren(...nodes);
  }

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
    release();
    setLabel();
    writeCaption();
    requestDraw();
    resolveDone();
  }

  function finishDevelop() {
    if (settled || state.phase !== 'developing') return;
    settled = true;
    state = developed(state);
    setLabel();
    writeCaption();
    markMosaicSeen(storage);
    requestDraw();
    resolveDone();
  }

  const hooks = {
    onManifest(received) {
      if (state.phase !== 'reading') return;
      manifest = received;
      const count = Math.ceil(received.size / received.chunk);
      if (count !== state.chunks) state = createMosaic({ chunks: count, aspect, now: state.activityAt });
      chunkCount = count;
      litAt = new Array(count).fill(-Infinity);
      verifiedAt = new Array(count).fill(-Infinity);
      setLabel();
      requestDraw();
    },
    onChunk(chunk) {
      if (state.phase !== 'reading') return;
      const time = now();
      state = chunkArrived(state, chunk, time);
      litAt[chunk.index] = time;
      animationEnd = Math.max(animationEnd, time + timings.lit);
      requestDraw();
    },
    onBlockVerified({ firstChunk, lastChunk }) {
      if (state.phase !== 'reading') return;
      const time = now();
      state = blockVerified(state, { firstChunk, lastChunk });
      const last = Math.min(lastChunk, state.chunks - 1);
      for (let i = Math.max(0, firstChunk); i <= last; i += 1) {
        verifiedAt[i] = time + waveDelay(i, firstChunk, last, timings.verifyWave);
      }
      animationEnd = Math.max(animationEnd, time + timings.verifyWave + timings.verify);
      requestDraw();
    },
  };

  async function develop(result) {
    state = allVerified(state);
    if (state.phase !== 'developing') {
      fail({ code: 'Incomplete', message: 'the read finished with blocks that were never verified' });
      return;
    }
    stopWatching();
    setLabel();
    let bitmap;
    try {
      bitmap = await decode(result.bytes, result.manifest.type);
    } catch (error) {
      fail({ code: 'DecodeFailed', message: error?.message });
      return;
    }
    if (settled) return;
    const pause = reducedMotion || fast ? 0 : DEVELOP_PAUSE_MS;
    const startAt = Math.max(now(), animationEnd) + pause;
    const endAt = startAt + timings.flip + timings.developSpread;
    reveal = { bitmap, startAt, endAt };
    // A timer as well as the frame loop: a hidden tab runs no frames, and the caption must still arrive.
    revealTimer = setTimer(finishDevelop, Math.max(0, endAt - now()));
    requestDraw();
  }

  function onRead(result) {
    release();
    if (settled || state.phase !== 'reading') return;
    develop(result).catch((error) => fail({ code: 'Unknown', message: error?.message }));
  }

  readColors();
  setLabel();

  const resizeObserver = typeof ResizeObserver === 'function' ? new ResizeObserver(resize) : null;
  resizeObserver?.observe(canvas);
  view.addEventListener?.('resize', resize);
  const themeObserver = typeof MutationObserver === 'function'
    ? new MutationObserver(() => {
      readColors();
      requestDraw();
    })
    : null;
  themeObserver?.observe(doc.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  resize();

  // The stall clock starts now, not at the manifest: a node that never answers the first request
  // must end in the failure state too.
  pollId = setPoll(() => {
    const next = checkStall(state, now());
    if (next !== state) fail(next.error);
  }, STALL_POLL_MS);

  try {
    const parsed = typeof options.link === 'string' ? parseLink(options.link.trim()) : options.link;
    source = createReader(parsed);
    Promise.resolve(source.read(parsed, () => {}, hooks)).then(onRead, fail);
  } catch (error) {
    fail(error);
  }

  return {
    done,
    get phase() {
      return state.phase;
    },
    destroy() {
      destroyed = true;
      release();
      if (revealTimer !== null) clearTimer(revealTimer);
      if (frameId !== null) cancelFrame(frameId);
      resizeObserver?.disconnect();
      themeObserver?.disconnect();
      view.removeEventListener?.('resize', resize);
      settled = true;
      resolveDone();
    },
  };
}
