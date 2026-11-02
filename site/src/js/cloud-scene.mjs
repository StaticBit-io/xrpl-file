// The hero's point cloud, as data. Pure: no DOM, no canvas, no clock of its own. Three parts:
//   - layoutCloud: where things stand. Ledgers are pads of ground; the file's chunks are small cubes
//     standing on the pad of the ledger they were written in, one cube per chunk; the manifest is one cube
//     a few ledgers later.
//   - scatterPoints: the inside of a cube, placed by the chunk's own bytes.
//   - createTimeline: what is visible and what is lime at a given time. A cube is shown from the moment
//     its chunk arrives and is lime only from the moment the sweep passes it after its block was
//     verified, so a renderer that asks this object cannot draw a claim the read has not made.
//
// Where a chunk stands IS read from the ledger: the reader reports the ledger of the transaction that
// carried it, so a cube stands on the pad of that ledger. With more ledgers than MAX_FILE_PADS a pad
// stands for a run of neighbouring ledgers (layout.ledgersPerPad), and the page says so. Drawn, not
// measured: a cube's place within its pad, the relief of the ground and the gap before the manifest.

export const MAX_FILE_PADS = 10;

export const PAD_W = 0.118;
export const PAD_STEP = 0.136;
export const CUBE = 0.03;
const GAP = 0.005;
const LEAD_PADS = 6;
const TRAIL_PADS = 5;
// Pads between the file's last ledger and the manifest's: "a few ledgers later".
const MANIFEST_GAP = 3;
// A pad is a tower of layers two cubes wide and as deep as the file needs (below), so a file read from few
// ledgers does not leave the frame.
const COLUMNS = 2;
const MIN_ROWS = 2;
// The ground is about 1.3 deep; ten rows of cubes are 0.35 of it. A file so big that one pad would need
// more only grows taller there.
const MAX_ROWS = 10;
const MAX_LAYERS = 12;
const INSIDE_MAX_POINTS = 20;
const SWEEP_MARGIN = 0.06;
const GROUND_POINTS_PER_PAD = 900;
const SHELF_Z = 0.06;
const MIN_FRAME = 1.1;

export const PAD_COUNT = LEAD_PADS + MAX_FILE_PADS + MANIFEST_GAP + 1 + TRAIL_PADS;

/** The stretch of ground the camera frames: the file's ten pads and the manifest's. */
export const FRAME_SPAN = [
  (LEAD_PADS - (PAD_COUNT - 1) / 2) * PAD_STEP - PAD_W / 2,
  (LEAD_PADS + MAX_FILE_PADS - 1 + MANIFEST_GAP - (PAD_COUNT - 1) / 2) * PAD_STEP + PAD_W / 2,
];

export const padX = (pad, padCount) => (pad - (padCount - 1) / 2) * PAD_STEP;

/** The ground is gently uneven so the pads read as terrain, not as a grid. */
export const groundY = (x, z) => 0.016 * Math.sin(x * 3.1 + z * 2.0) + 0.011 * Math.sin(z * 7.3 - x * 1.7);

function rng(seed) {
  let state = seed | 0;
  return () => {
    state = (state + 0x6d2b79f5) | 0;
    let t = Math.imul(state ^ (state >>> 15), 1 | state);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/**
 * Where things stand once the manifest is known; cubes are placed as their chunks arrive (createTimeline).
 *
 * @param {{ chunks: number, ledgers: number, firstLedger?: number }} options chunk count, the number of
 *   ledgers the manifest's range spans and the first of them
 */
export function layoutCloud({ chunks, ledgers, firstLedger = 0 }) {
  if (!Number.isInteger(chunks) || chunks < 1) throw new RangeError(`a cloud needs a whole number of chunks, got ${chunks}`);
  const span = Number.isFinite(ledgers) && ledgers >= 1 ? Math.floor(ledgers) : 1;
  const filePads = Math.min(MAX_FILE_PADS, span);
  // The file is centred in the ten pads the scene keeps for it, so the ground and the camera frame do
  // not move when the manifest says how many ledgers there are.
  const filePadFirst = LEAD_PADS + Math.floor((MAX_FILE_PADS - filePads) / 2);
  const manifestPad = filePadFirst + filePads - 1 + MANIFEST_GAP;
  const padCount = PAD_COUNT;
  // Which pad a chunk lands on is not known until it arrives, and real files are uneven (the hero's
  // 123 chunks sit 108 and 15 on its two ledgers), so every pad's layers are as deep as the whole file
  // would need: no pad can outgrow twelve layers, and the towers read as a bar chart of one footprint.
  const rows = Math.min(MAX_ROWS, Math.max(MIN_ROWS, Math.ceil(chunks / (COLUMNS * MAX_LAYERS))));

  const mx = padX(manifestPad, padCount);
  const manifest = { x: mx - CUBE / 2, y: groundY(mx, SHELF_Z) + 0.004, z: SHELF_Z - CUBE / 2, pad: manifestPad };

  return {
    padCount,
    filePadFirst,
    filePads,
    manifestPad,
    cube: CUBE,
    chunks,
    firstLedger,
    ledgers: span,
    ledgersPerPad: Math.ceil(span / filePads),
    capped: span > filePads,
    rows,
    manifest,
    fileSpan: [padX(filePadFirst, padCount) - PAD_W / 2, padX(filePadFirst + filePads - 1, padCount) + PAD_W / 2],
    anchors: {
      manifest: [mx, manifest.y + CUBE + 0.004, SHELF_Z],
    },
  };
}

/** The file pad (0-based, within the file's pads) of an absolute ledger index; the nearest end for one outside the range. */
export function padOfLedger(layout, ledger) {
  const offset = Math.min(layout.ledgers - 1, Math.max(0, ledger - layout.firstLedger));
  return Math.min(layout.filePads - 1, Math.floor((offset * layout.filePads) / layout.ledgers));
}

/** The cube in slot `slot` (arrival order) of file pad `local`. */
export function cubeAt(layout, local, slot) {
  const pad = layout.filePadFirst + local;
  const cx = padX(pad, layout.padCount);
  const column = slot % COLUMNS;
  const row = Math.floor(slot / COLUMNS) % layout.rows;
  const layer = Math.floor(slot / (COLUMNS * layout.rows));
  return {
    x: cx + (column - (COLUMNS - 1) / 2) * (CUBE + GAP) - CUBE / 2,
    y: groundY(cx, SHELF_Z) + 0.004 + layer * (CUBE + GAP),
    z: SHELF_Z + (row - (layout.rows - 1) / 2) * (CUBE + GAP) - CUBE / 2,
    pad,
  };
}

/**
 * The stretch of ground the camera frames once the layout is known: the file's pads and the manifest's,
 * with two pads of ground on either side. Never narrower than MIN_FRAME so a file on one or two ledgers
 * is not blown up past its cubes.
 */
export function frameFor(layout) {
  const left = layout.fileSpan[0] - 2 * PAD_STEP;
  const right = layout.manifest.x + CUBE + 2 * PAD_STEP;
  const half = Math.max(MIN_FRAME, right - left) / 2;
  const centre = (left + right) / 2;
  return [centre - half, centre + half];
}

/** Ground points, x y z and a base brightness each; the same for the same pad count. */
export function groundPoints(padCount) {
  const random = rng(1729);
  const out = [];
  for (let pad = 0; pad < padCount; pad += 1) {
    const left = padX(pad, padCount) - PAD_W / 2;
    const density = 0.55 + random() * 0.45;
    const n = Math.round(GROUND_POINTS_PER_PAD * density);
    for (let i = 0; i < n; i += 1) {
      const x = left + random() * PAD_W;
      const z = (random() - 0.5) * 1.3;
      out.push(x, groundY(x, z) + (random() - 0.5) * 0.004, z, 0.5 + random() * 0.35);
    }
  }
  return Float32Array.from(out);
}

/**
 * The inside of a chunk's cube: points whose coordinates are the chunk's own bytes, three at a time,
 * spaced evenly through the chunk. Coordinates are 0..1 inside the cube. A chunk too short for even
 * one point gives none.
 */
export function scatterPoints(bytes, maxPoints = INSIDE_MAX_POINTS) {
  const usable = bytes.length - 2;
  if (usable <= 0) return new Float32Array(0);
  const count = Math.min(maxPoints, Math.floor(bytes.length / 3));
  const step = Math.max(3, Math.floor(usable / count));
  const out = [];
  for (let j = 0; j < count; j += 1) {
    const at = j * step;
    if (at + 2 >= bytes.length) break;
    out.push(bytes[at] / 255, bytes[at + 1] / 255, bytes[at + 2] / 255);
  }
  return Float32Array.from(out);
}

/** Unit-cube points along the 12 edges, `divisions` steps per edge. */
export function cubeEdgePoints(divisions = 4) {
  const out = [];
  const seen = new Set();
  for (let a = 0; a <= divisions; a += 1) {
    for (let b = 0; b <= divisions; b += 1) {
      for (let c = 0; c <= divisions; c += 1) {
        const extremes = Number(a === 0 || a === divisions) + Number(b === 0 || b === divisions) + Number(c === 0 || c === divisions);
        if (extremes < 2) continue;
        const key = (a * (divisions + 1) + b) * (divisions + 1) + c;
        if (seen.has(key)) continue;
        seen.add(key);
        out.push(a / divisions, b / divisions, c / divisions);
      }
    }
  }
  return Float32Array.from(out);
}

/**
 * Animation lengths in milliseconds. Reduced motion changes everything at once (it wins over fast).
 * A replay, for a visitor who has already seen the animation, finishes everything that follows the
 * last chunk (sweep, fade, settling) within 300 ms.
 */
export function cloudTimings({ fast = false, reducedMotion = false }) {
  if (reducedMotion) return { lit: 0, sweep: 0, lime: 0, settle: 0, zoom: 0 };
  if (fast) return { lit: 40, sweep: 80, lime: 60, settle: 60, zoom: 60 };
  return { lit: 220, sweep: 1600, lime: 500, settle: 500, zoom: 700 };
}

const ramp = (elapsed, length) => {
  if (length <= 0) return elapsed >= 0 ? 1 : 0;
  return Math.min(1, Math.max(0, elapsed / length));
};

const HIDDEN = Object.freeze({ shown: false, lit: 0, lime: 0 });

/**
 * What is on screen at a given time, built only from what the read has reported. Times are the
 * caller's clock readings in milliseconds; none is read from a clock here.
 */
export function createTimeline({ layout, timings }) {
  const count = layout.chunks;
  const placed = new Array(count).fill(null);
  const slots = new Array(layout.filePads).fill(0);
  const tops = new Array(layout.filePads).fill(null);
  const arrivedAt = new Array(count).fill(null);
  const limeAt = new Array(count).fill(null);
  const insides = new Array(count).fill(null);
  const sweeps = [];
  const manifest = { shownAt: null, limeAt: null };
  let sweepsEnd = 0;
  let limeEnd = 0;

  const visualOf = (shownAt, verifiedAt, time) => {
    if (shownAt === null) return HIDDEN;
    return {
      shown: true,
      lit: ramp(time - shownAt, timings.lit),
      lime: verifiedAt === null ? 0 : ramp(time - verifiedAt, timings.lime),
    };
  };

  return {
    /** `ledger` is the ledger of the chunk's transaction; a chunk without one has no place to stand and is not drawn. */
    chunkArrived(index, bytes, time, ledger) {
      if (!Number.isInteger(index) || index < 0 || index >= count || arrivedAt[index] !== null) return;
      if (!Number.isInteger(ledger)) return;
      const local = padOfLedger(layout, ledger);
      const cube = cubeAt(layout, local, slots[local]++);
      placed[index] = cube;
      tops[local] = Math.max(tops[local] ?? 0, cube.y + layout.cube);
      arrivedAt[index] = time;
      insides[index] = scatterPoints(bytes);
    },

    /** Where chunk `index` stands, or null until it has arrived. */
    cube: (index) => placed[index] ?? null,

    /** The point the file's callout points at: above the tallest stack so far, over the file's pads before any cube. */
    fileAnchor() {
      let tallest = -1;
      for (let local = 0; local < tops.length; local += 1) {
        if (tops[local] !== null && (tallest < 0 || tops[local] > tops[tallest])) tallest = local;
      }
      if (tallest < 0) {
        const x = (layout.fileSpan[0] + layout.fileSpan[1]) / 2;
        return [x, groundY(x, SHELF_Z) + 0.004, SHELF_Z];
      }
      return [padX(layout.filePadFirst + tallest, layout.padCount), tops[tallest] + 0.004, SHELF_Z];
    },

    /** The block's hash matched: its chunks that are drawn get a sweep and then turn lime. */
    blockVerified({ firstChunk, lastChunk }, time) {
      const first = Math.max(0, firstChunk);
      const last = Math.min(count - 1, lastChunk);
      const centres = [];
      for (let k = first; k <= last; k += 1) {
        if (arrivedAt[k] !== null && limeAt[k] === null) centres.push([k, placed[k].x + layout.cube / 2]);
      }
      if (centres.length === 0) return;
      const xs = centres.map(([, x]) => x);
      const from = Math.min(...xs) - SWEEP_MARGIN;
      const to = Math.max(...xs) + SWEEP_MARGIN;
      for (const [k, x] of centres) limeAt[k] = time + timings.sweep * ((x - from) / (to - from));
      sweeps.push({ start: time, from, to });
      sweepsEnd = Math.max(sweepsEnd, time + timings.sweep);
      limeEnd = Math.max(limeEnd, time + timings.sweep + timings.lime);
    },

    showManifest(time) {
      if (manifest.shownAt === null) manifest.shownAt = time;
    },

    /** Everything checked out: the manifest turns lime once the last sweep is through. */
    manifestVerified(time) {
      if (manifest.limeAt !== null) return;
      manifest.limeAt = Math.max(time, sweepsEnd);
      limeEnd = Math.max(limeEnd, manifest.limeAt + timings.lime);
    },

    visual: (index, time) => visualOf(arrivedAt[index] ?? null, limeAt[index] ?? null, time),
    manifestVisual: (time) => visualOf(manifest.shownAt, manifest.limeAt, time),
    inside: (index) => insides[index] ?? null,

    /** The sweep's x at `time`, or null when none is running. */
    sweep(time) {
      for (const run of sweeps) {
        if (timings.sweep > 0 && time >= run.start && time <= run.start + timings.sweep) {
          return { x: run.from + (run.to - run.from) * ((time - run.start) / timings.sweep) };
        }
      }
      return null;
    },

    /** Milliseconds until the last colour change and the settling pause are over. */
    settleDelay: (time) => Math.max(0, limeEnd + timings.settle - time),
  };
}
