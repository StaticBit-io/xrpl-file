// The hero: a point cloud of a stretch of the XRP Ledger seen from above at an oblique angle, driven by
// the real read of the hero file. Ledgers are pads of ground; each chunk of the file is a small cube
// standing on the pad of the ledger its chunk was written in (the reader reports it), its inside scattered
// by that chunk's own bytes; the manifest is one cube a few ledgers later. Nothing is drawn ahead of the
// read: a cube appears, on its own ledger, when its chunk arrives, turns
// lime (moss in the light theme) only when the sweep passes it after its block was verified, and the
// small <xrpl-file> card with its "verified" line appears only when every block has been verified.
//
// The reader wiring, the failure paths and the release of the connection are hero-read.mjs; what is
// visible at a given time is cloud-scene.mjs. This file is the drawing, the callouts and the card.
import { formatLink } from './xrpl.mjs';
import { pathFromLink } from './route.mjs';
import { hasSeenMosaic, startHeroRead } from './hero-read.mjs';
import {
  CUBE,
  FRAME_SPAN,
  PAD_COUNT,
  cloudTimings,
  createTimeline,
  cubeEdgePoints,
  frameFor,
  groundPoints,
  layoutCloud,
} from './cloud-scene.mjs';

const SVG_NS = 'http://www.w3.org/2000/svg';
// The point buffer is uploaded whole on every frame, so it is rendered at no more than 1.5 device pixels
// per CSS pixel (about 56% of the bytes of 2): points are one or two pixels wide and stay crisp.
const MAX_DPR = 1.5;
// While the scene sways, a frame every ~33 ms is smooth enough and halves the cost.
const FRAME_MS = 33;
const FAILED_ALPHA = 0.6;
const SWEEP_REACH = 0.05;
const ELEMENT_SCRIPT = '/element/1.1.0/xrpl-file-element.js';

const FALLBACK_COLOURS = {
  paper: [10, 14, 20],
  lo: [40, 48, 60],
  mid: [110, 124, 140],
  hi: [214, 224, 235],
  lime: [200, 240, 70],
  rule: [90, 100, 115],
};

const EDGES = cubeEdgePoints(4);

const clamp01 = (value) => Math.min(1, Math.max(0, value));

// After a verified read the scene makes one full turn and stops on the angle where the sway left it.
// 4.5 s keeps it under the 5 s past which motion that starts on its own needs a pause control
// (WCAG 2.2.2), and is slow enough for the cubes to be followed round.
export const TURN_MS = 4500;
// The figures fade out over the start of the turn and back in over its end, following their cubes
// meanwhile, so no figure is left pointing at a place its cube has moved away from.
export const TURN_FADE_MS = 400;

/** The yaw the turn adds `elapsed` ms after it starts: 0 to 2π, eased in and out, so it starts and ends at rest. */
export function turnOffset(elapsed) {
  const progress = clamp01(elapsed / TURN_MS);
  return 2 * Math.PI * progress * progress * (3 - 2 * progress);
}

/** English placeholders so the cloud works on its own (tests, a page without copy). */
const DEFAULT_STRINGS = {
  label: (phase, { chunks }) => `Point cloud, ${phase}${chunks === null ? '' : `, ${chunks} chunks`}`,
  caption: ({ chunks, firstLedger, lastLedger }) => `read · ${chunks} chunks · ledgers ${firstLedger}–${lastLedger}`,
  failed: () => 'The file could not be read from the ledger just now.',
  fileLinkLabel: 'Open the file page',
  verified: 'verified',
  note: () => 'Each cube stands on the ledger its chunk was written in. The ground is drawn.',
  fileCallout: (chunks) => ({ caption: 'The file', value: String(chunks), unit: 'chunks' }),
  linkCallout: (bytes) => ({ caption: 'The link, the manifest hash', value: String(bytes), unit: 'bytes' }),
};

function createColourReader(doc) {
  let probe = null;
  try {
    probe = doc.createElement('canvas').getContext('2d', { willReadFrequently: true });
  } catch {
    probe = null;
  }
  return (css, fallback) => {
    if (!probe || !css) return fallback;
    try {
      probe.clearRect(0, 0, 1, 1);
      probe.fillStyle = '#000';
      probe.fillStyle = css;
      probe.fillRect(0, 0, 1, 1);
      const data = probe.getImageData(0, 0, 1, 1).data;
      return [data[0], data[1], data[2]];
    } catch {
      return fallback;
    }
  };
}

function svgCheck(doc) {
  const svg = doc.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('class', 'hero-card__check');
  svg.setAttribute('viewBox', '0 0 16 16');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  const path = doc.createElementNS(SVG_NS, 'path');
  path.setAttribute('d', 'M3 8.5l3.5 3.5L13 4.5');
  svg.append(path);
  return svg;
}

function defaultLoadElement(doc) {
  if (doc.defaultView?.customElements?.get('xrpl-file')) return;
  if (doc.querySelector?.(`script[src="${ELEMENT_SCRIPT}"]`)) return;
  const script = doc.createElement('script');
  script.src = ELEMENT_SCRIPT;
  script.defer = true;
  doc.head.append(script);
}

/**
 * Draws the cloud on `canvas` and reads the file behind `options.link` into it.
 *
 * @param {HTMLCanvasElement} canvas Sized by its CSS box; it gets role="img" and an aria-label that
 *   follows the phase.
 * @param {object} options
 * @param {string|{network: string, txHash: string}} options.link `xrpl-file://...` text or a parsed link
 * @param {number} [options.aspect] width / height of the file, so the card keeps its box while the
 *   element loads
 * @param {boolean} [options.reducedMotion] one still frame per change, no sweep, no sway; defaults to
 *   the visitor's prefers-reduced-motion
 * @param {boolean} [options.fast] a replay: everything after the last chunk takes under 300 ms;
 *   defaults to whether this browser session has already seen the hero (sessionStorage `xf-mosaic-seen`)
 * @param {HTMLElement} [options.caption] role="status", a polite live region: empty while reading, then
 *   the line for a finished read or the quiet failure message. It carries data-phase.
 * @param {HTMLElement} [options.note] gets the sentence saying what is real and what is drawn; empty and
 *   hidden until the manifest names the ledger range, since there is nothing to describe before that
 * @param {{ file: HTMLElement, link: HTMLElement }} [options.callouts] the two figures with leaders;
 *   each is filled and shown when its data exists
 * @param {HTMLElement} [options.card] holds the verified file and its line; hidden until every block
 *   has been verified
 * @param {object} [options.strings] all visible and spoken text, supplied by the caller (see
 *   DEFAULT_STRINGS for the shape): label(phase, { chunks }), caption({ chunks, firstLedger,
 *   lastLedger }), failed(code), fileLinkLabel, verified, note({ ledgers, ledgersPerPad, capped }) (a
 *   string also works; it is called once the manifest is known), fileCallout(chunks) and linkCallout(bytes),
 *   each callout giving { caption, value, unit }
 * @param {object} [env] seams for tests; defaults are the browser's own
 * @returns {{ done: Promise<void>, destroy: () => void, readonly phase: string }} `done` settles, and
 *   never rejects, once the read is done or failed; `destroy()` stops everything and abandons the read.
 */
export function mountCloud(canvas, options, env = {}) {
  const doc = canvas.ownerDocument;
  const view = doc.defaultView;
  const {
    now = () => performance.now(),
    requestFrame = (callback) => view.requestAnimationFrame(callback),
    cancelFrame = (id) => view.cancelAnimationFrame(id),
    startRead = startHeroRead,
    loadElement = defaultLoadElement,
    storage,
    ...readerEnv
  } = env;
  const { caption = null, note = null, callouts = null, card = null } = options;
  const strings = { ...DEFAULT_STRINGS, ...options.strings };
  const reducedMotion = options.reducedMotion ?? (view.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false);
  const fast = options.fast ?? hasSeenMosaic(storage);
  const timings = cloudTimings({ fast, reducedMotion });

  const linkText = typeof options.link === 'string' ? options.link.trim() : formatLink(options.link);
  const filePath = (() => {
    try {
      return pathFromLink(linkText);
    } catch {
      return null;
    }
  })();
  const linkBytes = new TextEncoder().encode(linkText).length;

  const context = canvas.getContext('2d');
  const ground = groundPoints(PAD_COUNT);
  const readColour = createColourReader(doc);
  let colours = FALLBACK_COLOURS;
  let layout = null;
  let timeline = null;
  let chunkCount = null;
  let manifest = null;
  let phase = 'reading';
  let failedAlpha = 1;
  let destroyed = false;
  let frameId = null;
  let lastFrameAt = -Infinity;
  let animatedUntil = 0;
  // The sway stops where it is when the read ends (done or failed). A verified read then turns the scene
  // once round to that same angle; after that nothing asks for a frame.
  let swayEnd = Infinity;
  let turnStart = null;
  let turnEnd = -Infinity;
  // The camera frame: the whole stretch the scene can use until the manifest names the ledgers, then
  // eased to the ground the file and the manifest actually occupy.
  let frameFrom = FRAME_SPAN;
  let frameTo = FRAME_SPAN;
  let frameStart = 0;
  let onScreen = true;
  let dpr = 1;
  let image = null;
  let pixels = null;
  let cardShown = false;
  const calloutSizes = { file: null, link: null };
  let paper32 = 0;
  const projected = new Float64Array(4);

  canvas.setAttribute('role', 'img');
  if (caption) {
    caption.setAttribute('role', 'status');
    caption.setAttribute('aria-live', 'polite');
  }
  // The note describes the cubes, so it stays empty (and takes no room) until the manifest names the
  // ledgers and the cubes can exist; a read that fails before that never says anything about them.
  function writeNote(detail) {
    if (!note) return;
    const text = detail === null ? '' : typeof strings.note === 'function' ? strings.note(detail) : strings.note;
    note.textContent = text;
    note.hidden = text === '';
  }
  writeNote(null);

  function readColours() {
    const style = view.getComputedStyle(doc.documentElement);
    const token = (name) => style.getPropertyValue(name).trim();
    colours = {
      paper: readColour(token('--paper'), FALLBACK_COLOURS.paper),
      lo: readColour(token('--point-lo'), FALLBACK_COLOURS.lo),
      mid: readColour(token('--point-mid'), FALLBACK_COLOURS.mid),
      hi: readColour(token('--point-hi'), FALLBACK_COLOURS.hi),
      lime: readColour(token('--accent'), FALLBACK_COLOURS.lime),
      rule: readColour(token('--rule'), FALLBACK_COLOURS.rule),
    };
    const [r, g, b] = colours.paper;
    paper32 = (255 << 24) | (b << 16) | (g << 8) | r;
    if (cardShown) syncCardTheme();
  }

  function setLabel() {
    canvas.setAttribute('aria-label', strings.label(phase, { chunks: chunkCount }));
    canvas.dataset.phase = phase;
    if (caption) caption.dataset.phase = phase;
  }

  function turning(time) {
    return turnStart !== null && time < turnEnd;
  }

  /** How visible the figures are: 1 outside the turn, fading out at its start and back in at its end. */
  function figureAlpha(time) {
    if (!turning(time)) return 1;
    const fadeOut = 1 - (time - turnStart) / TURN_FADE_MS;
    const fadeIn = 1 - (turnStart + TURN_MS - time) / TURN_FADE_MS;
    return clamp01(Math.max(fadeOut, fadeIn));
  }

  function startTurn(time) {
    // Nobody would see it: the scene goes straight to the angle it would have ended on.
    if (reducedMotion || doc.hidden || !onScreen) return;
    turnStart = time;
    turnEnd = time + TURN_MS;
  }

  /** Ends a running turn on its last frame, which is the angle it started from. */
  function endTurn() {
    if (!turning(now())) return;
    turnEnd = now();
    requestDraw();
  }

  function currentFrame(time) {
    const eased = timings.zoom > 0 ? clamp01((time - frameStart) / timings.zoom) : 1;
    const smooth = eased * eased * (3 - 2 * eased);
    return [
      frameFrom[0] + (frameTo[0] - frameFrom[0]) * smooth,
      frameFrom[1] + (frameTo[1] - frameFrom[1]) * smooth,
    ];
  }

  // Camera: an oblique view from above, swaying a little. Framed on the ground the file and the
  // manifest can occupy, so nothing moves when the manifest arrives.
  function camera(time) {
    const width = canvas.width;
    const height = canvas.height;
    const sway = reducedMotion ? 0 : 0.22 * Math.sin(Math.min(time, swayEnd) / 9000);
    const yaw = -0.5 + sway + (turning(time) ? turnOffset(time - turnStart) : 0);
    const elevation = 0.62;
    const [frameLeft, frameRight] = currentFrame(time);
    const spanUnits = frameRight - frameLeft;
    const centreX = (frameLeft + frameRight) / 2;
    const narrow = width < 520 * dpr;
    const scale = (width * (narrow ? 0.95 : 0.8)) / spanUnits;
    const cy = Math.cos(yaw);
    const sy = Math.sin(yaw);
    return {
      cy,
      sy,
      ce: Math.cos(elevation),
      se: Math.sin(elevation),
      f: 2.4,
      scale,
      ox: width * 0.5 - centreX * cy * scale,
      oy: height * (narrow ? 0.7 : 0.68),
    };
  }

  function project(out, x, y, z, cam) {
    const xr = x * cam.cy - z * cam.sy;
    const zr = x * cam.sy + z * cam.cy;
    const up = y * cam.ce + zr * cam.se;
    const depth = zr * cam.ce - y * cam.se;
    const s = cam.f / (cam.f + depth);
    out[0] = cam.ox + xr * s * cam.scale;
    out[1] = cam.oy - up * s * cam.scale;
    out[2] = s;
    out[3] = depth;
  }

  /** One point into the pixel buffer: projected, depth-faded, sweep-lit, blended over what is there. */
  function plot(cam, sweepX, x, y, z, base, kind, mixToLime, tone) {
    project(projected, x, y, z, cam);
    const width = canvas.width;
    const height = canvas.height;
    const sx = projected[0] | 0;
    const sy = projected[1] | 0;
    if (sx < 0 || sy < 0 || sx >= width - 1 || sy >= height - 1) return;
    const { lo, mid, hi, lime } = colours;
    let r;
    let g;
    let b;
    if (kind === 0) {
      r = lo[0] + (mid[0] - lo[0]) * base;
      g = lo[1] + (mid[1] - lo[1]) * base;
      b = lo[2] + (mid[2] - lo[2]) * base;
    } else {
      r = mid[0] + (hi[0] - mid[0]) * tone;
      g = mid[1] + (hi[1] - mid[1]) * tone;
      b = mid[2] + (hi[2] - mid[2]) * tone;
    }
    let alpha = base * (1 - Math.min(0.75, Math.max(0, (projected[3] + 0.4) * 0.55)));
    // Cubes stand out of the ground: the same depth fade, but never as faint as a ground point.
    if (kind > 0) alpha = Math.min(1, alpha + 0.25);
    alpha *= failedAlpha;
    if (sweepX !== null) {
      const reach = Math.abs(x - sweepX);
      if (reach < SWEEP_REACH) {
        const l = 1 - reach / SWEEP_REACH;
        r += (hi[0] - r) * l;
        g += (hi[1] - g) * l;
        b += (hi[2] - b) * l;
        alpha = Math.min(1, alpha + l * 0.6);
      }
    }
    if (mixToLime > 0) {
      r += (lime[0] - r) * mixToLime;
      g += (lime[1] - g) * mixToLime;
      b += (lime[2] - b) * mixToLime;
    }
    const size = projected[2] * dpr * (kind > 0 ? 1.5 : 1.25);
    const side = kind > 0 || size > 1.6 ? 2 : 1;
    const a = Math.min(1, alpha * (side === 1 ? Math.min(1, size) : 1));
    const data = image.data;
    for (let yy = 0; yy < side; yy += 1) {
      for (let xx = 0; xx < side; xx += 1) {
        const q = ((sy + yy) * width + sx + xx) * 4;
        data[q] += (r - data[q]) * a;
        data[q + 1] += (g - data[q + 1]) * a;
        data[q + 2] += (b - data[q + 2]) * a;
      }
    }
  }

  function plotCube(cam, sweepX, origin, template, mixToLime, tone, base) {
    for (let i = 0; i < template.length; i += 3) {
      plot(cam, sweepX, origin.x + template[i] * CUBE, origin.y + template[i + 1] * CUBE, origin.z + template[i + 2] * CUBE, base, 1, mixToLime, tone);
    }
  }

  function drawScene(time) {
    const cam = camera(time);
    const sweep = timeline?.sweep(time) ?? null;
    const sweepX = sweep === null ? null : sweep.x;
    pixels.fill(paper32);

    for (let i = 0; i < ground.length; i += 4) plot(cam, sweepX, ground[i], ground[i + 1], ground[i + 2], ground[i + 3], 0, 0, 0);

    if (layout !== null) {
      for (let k = 0; k < layout.chunks; k += 1) {
        const cube = timeline.cube(k);
        if (cube === null) continue;
        const visual = timeline.visual(k, time);
        const tone = 0.55 + 0.45 * visual.lit;
        plotCube(cam, sweepX, cube, EDGES, visual.lime, tone, 0.8 * visual.lit);
        const inside = timeline.inside(k);
        for (let i = 0; i < inside.length; i += 3) {
          plot(cam, sweepX, cube.x + inside[i] * CUBE, cube.y + inside[i + 1] * CUBE, cube.z + inside[i + 2] * CUBE, 0.6 * visual.lit, 1, visual.lime, tone);
        }
      }
      const shown = timeline.manifestVisual(time);
      if (shown.shown) plotCube(cam, sweepX, layout.manifest, EDGES, shown.lime, 0.9, 0.95 * shown.lit);
    }
    context.putImageData(image, 0, 0);
    return cam;
  }

  function drawLeader(ax, ay, topY, alpha) {
    context.globalAlpha = alpha;
    context.strokeStyle = `rgb(${colours.rule.join(',')})`;
    context.lineWidth = dpr;
    context.beginPath();
    context.moveTo(Math.round(ax) + 0.5, ay);
    context.lineTo(Math.round(ax) + 0.5, topY);
    context.stroke();
    context.fillStyle = `rgb(${colours.hi.join(',')})`;
    context.beginPath();
    context.arc(ax, ay, 1.5 * dpr, 0, Math.PI * 2);
    context.fill();
    context.globalAlpha = 1;
  }

  // The two figures sit in the top band of the frame, over the sky, one row each, so neither can land on
  // the stacks and neither leader crosses the other figure: the link's row is the higher one and its
  // leader is the one that runs past the file's figure.
  const CALLOUT_TOP = 0.04;
  const CALLOUT_ROW_GAP = 8;

  /** `side` is the side of its leader the figure's text extends to: 'left' or 'right'. */
  function placeCallout(element, key, anchor, row, side, cam, alpha) {
    if (!element || !anchor) return;
    element.style.opacity = alpha < 1 ? String(alpha) : '';
    if (alpha <= 0) {
      element.classList?.toggle('on', false);
      return;
    }
    project(projected, anchor[0], anchor[1], anchor[2], cam);
    const ax = projected[0];
    const ay = projected[1];
    const width = canvas.width;
    const height = canvas.height;
    const inside = ax > width * 0.06 && ax < width * 0.94 && ay > height * 0.2 && ay < height * 0.95;
    element.classList?.toggle('on', inside);
    if (!inside) return;
    const size = calloutSizes[key] ?? { w: 0, h: 0 };
    const above = row === 0 ? 0 : (calloutSizes.link?.h ?? 0) + CALLOUT_ROW_GAP;
    const top = canvas.clientHeight * CALLOUT_TOP + above;
    drawLeader(ax, ay, Math.min(ay, (top + size.h) * dpr), alpha);
    const left = canvas.offsetLeft + ax / dpr;
    const flip = side === 'left' || left + size.w > canvas.offsetLeft + canvas.clientWidth - 8;
    const x = Math.max(canvas.offsetLeft + 8, flip ? left - size.w : left);
    element.style.transform = `translate(${Math.round(x)}px, ${Math.round(canvas.offsetTop + top)}px)`;
    element.style.textAlign = flip ? 'right' : '';
  }

  /** Paints one frame; returns whether another is needed. */
  function render(time) {
    if (!context || !image || canvas.width === 0 || canvas.height === 0) return false;
    const cam = drawScene(time);
    if (layout !== null && callouts) {
      const alpha = figureAlpha(time);
      placeCallout(callouts.file, 'file', timeline.fileAnchor(), 1, 'left', cam, alpha);
      placeCallout(callouts.link, 'link', layout.anchors.manifest, 0, 'right', cam, alpha);
    }
    const swaying = !reducedMotion && time < swayEnd;
    return swaying || turning(time) || time < animatedUntil;
  }

  function requestDraw() {
    if (frameId !== null || destroyed) return;
    frameId = requestFrame(() => {
      frameId = null;
      const time = now();
      if (time - lastFrameAt < FRAME_MS && onScreen && !reducedMotion) {
        requestDraw();
        return;
      }
      lastFrameAt = time;
      if (render(time) && onScreen && !doc.hidden) requestDraw();
    });
  }

  function measureCallouts() {
    if (!callouts) return;
    for (const key of ['file', 'link']) {
      const element = callouts[key];
      calloutSizes[key] = element ? { w: element.offsetWidth ?? 0, h: element.offsetHeight ?? 0 } : null;
    }
  }

  function resize() {
    dpr = Math.min(view.devicePixelRatio || 1, MAX_DPR);
    const width = Math.max(1, Math.round(canvas.clientWidth * dpr));
    const height = Math.max(1, Math.round(canvas.clientHeight * dpr));
    if (canvas.width !== width || canvas.height !== height || image === null) {
      canvas.width = width;
      canvas.height = height;
      image = context?.createImageData?.(width, height) ?? null;
      pixels = image ? new Uint32Array(image.data.buffer) : null;
    }
    measureCallouts();
    requestDraw();
  }

  function fillCallout(element, text) {
    if (!element) return;
    const label = doc.createElement('span');
    label.textContent = text.caption;
    const value = doc.createElement('strong');
    value.append(text.value);
    const unit = doc.createElement('span');
    unit.className = 'u';
    unit.textContent = ` ${text.unit}`;
    value.append(unit);
    element.replaceChildren(label, value);
  }

  function writeCaption() {
    if (!caption) return;
    const text = doc.createElement('span');
    text.className = 'hero-caption__text';
    if (phase === 'failed') {
      const code = failure?.code ?? 'Unknown';
      text.textContent = typeof strings.failed === 'function' ? strings.failed(code) : strings.failed;
    } else {
      text.textContent = strings.caption({ chunks: chunkCount, firstLedger: manifest.firstLedger, lastLedger: manifest.lastLedger });
    }
    caption.replaceChildren(text);
  }

  function syncCardTheme() {
    const element = card?.querySelector?.('xrpl-file');
    element?.setAttribute('theme', doc.documentElement.dataset.theme === 'light' ? 'light' : 'dark');
  }

  function showCard() {
    if (!card || cardShown) return;
    cardShown = true;
    const element = doc.createElement('xrpl-file');
    element.setAttribute('src', linkText);
    element.setAttribute('caption', 'off');
    element.setAttribute('loading', 'eager');
    // The logo is a transparent PNG: it sits on the card, not on the element's own box.
    element.setAttribute('background', 'transparent');
    if (options.aspect) element.style.aspectRatio = String(options.aspect);
    const line = doc.createElement('p');
    line.className = 'hero-card__line';
    const status = doc.createElement('span');
    status.className = 'hero-card__status';
    status.append(svgCheck(doc), strings.verified);
    line.append(status);
    if (filePath) {
      const link = doc.createElement('a');
      link.className = 'hero-card__link';
      link.setAttribute('href', filePath);
      link.textContent = strings.fileLinkLabel;
      line.append(link);
    }
    card.replaceChildren(element, line);
    syncCardTheme();
    card.hidden = false;
    loadElement(doc);
  }

  let failure = null;
  const listener = {
    onPhase(next) {
      phase = next;
      if (next === 'failed') failedAlpha = FAILED_ALPHA;
      if ((next === 'done' || next === 'failed') && swayEnd === Infinity) {
        swayEnd = now();
        if (next === 'done') startTurn(swayEnd);
      }
      setLabel();
    },
    onManifest({ manifest: received, chunks, time }) {
      manifest = received;
      chunkCount = chunks;
      const ledgers = received.lastLedger - received.firstLedger + 1;
      layout = layoutCloud({ chunks, ledgers, firstLedger: received.firstLedger });
      writeNote({ ledgers: layout.ledgers, ledgersPerPad: layout.ledgersPerPad, capped: layout.capped });
      timeline = createTimeline({ layout, timings });
      timeline.showManifest(time);
      frameFrom = currentFrame(time);
      frameTo = frameFor(layout);
      frameStart = time;
      animatedUntil = Math.max(animatedUntil, time + Math.max(timings.lit, timings.zoom));
      fillCallout(callouts?.file, strings.fileCallout(chunks));
      fillCallout(callouts?.link, strings.linkCallout(linkBytes));
      measureCallouts();
      setLabel();
      requestDraw();
    },
    onChunk({ index, bytes, ledger, time }) {
      timeline?.chunkArrived(index, bytes, time, ledger);
      animatedUntil = Math.max(animatedUntil, time + timings.lit);
      requestDraw();
    },
    onBlockVerified({ firstChunk, lastChunk, time }) {
      timeline?.blockVerified({ firstChunk, lastChunk }, time);
      animatedUntil = Math.max(animatedUntil, time + timings.sweep + timings.lime);
      requestDraw();
    },
    onDeveloping({ time }) {
      timeline?.manifestVerified(time);
      animatedUntil = Math.max(animatedUntil, time + timeline.settleDelay(time));
      requestDraw();
    },
    onDone() {
      writeCaption();
      callouts?.file?.setAttribute?.('data-state', 'verified');
      showCard();
      requestDraw();
    },
    onFailed({ error }) {
      failure = error;
      writeCaption();
      requestDraw();
    },
  };

  readColours();
  setLabel();

  const read = startRead(
    { link: options.link, listener, developDelay: () => timeline?.settleDelay(now()) ?? 0 },
    { now, storage, ...readerEnv },
  );

  const resizeObserver = typeof ResizeObserver === 'function' ? new ResizeObserver(resize) : null;
  resizeObserver?.observe(canvas);
  view.addEventListener?.('resize', resize);
  const themeObserver = typeof MutationObserver === 'function'
    ? new MutationObserver(() => {
      readColours();
      requestDraw();
    })
    : null;
  themeObserver?.observe(doc.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  // A cloud nobody can see is not worth its frames; the read and its clocks run regardless.
  const visibilityObserver = typeof IntersectionObserver === 'function'
    ? new IntersectionObserver((entries) => {
      onScreen = entries.at(-1).isIntersecting;
      if (onScreen) requestDraw();
      else endTurn();
    })
    : null;
  visibilityObserver?.observe(canvas);
  const onVisibility = () => {
    if (doc.hidden) endTurn();
    else requestDraw();
  };
  doc.addEventListener?.('visibilitychange', onVisibility);
  // Reaching for the cloud or for the card in it stops the turn where it would have ended.
  const stage = canvas.parentElement ?? canvas;
  stage.addEventListener?.('pointerdown', endTurn);
  stage.addEventListener?.('keydown', endTurn);
  resize();

  return {
    done: read.done,
    get phase() {
      return read.phase;
    },
    destroy() {
      destroyed = true;
      read.destroy();
      if (frameId !== null) cancelFrame(frameId);
      resizeObserver?.disconnect();
      themeObserver?.disconnect();
      visibilityObserver?.disconnect();
      view.removeEventListener?.('resize', resize);
      doc.removeEventListener?.('visibilitychange', onVisibility);
      stage.removeEventListener?.('pointerdown', endTurn);
      stage.removeEventListener?.('keydown', endTurn);
    },
  };
}
