// The hero's point cloud, as data. Pure: no DOM, no canvas, no clock of its own. Three parts:
//   - layoutCloud: where things stand. Ledgers are pads of ground; the file's chunks are small cubes
//     standing on them, one cube per chunk; the manifest is one cube a few ledgers later.
//   - scatterPoints: the inside of a cube, placed by the chunk's own bytes.
//   - createTimeline: what is visible and what is lime at a given time. A cube is shown from the moment
//     its chunk arrives and is lime only from the moment the sweep passes it after its block was
//     verified, so a renderer that asks this object cannot draw a claim the read has not made.
//
// Which ledger holds which chunk is NOT read from the ledger: the reader reports a chunk's index and
// bytes, not its ledger. The number of pads is real (the ledger range of the manifest); the spread of
// chunks over them is drawn, and the page says so.

export const MAX_FILE_PADS = 10;

export const PAD_W = 0.118;
export const PAD_STEP = 0.136;
export const CUBE = 0.03;
const GAP = 0.005;
const LEAD_PADS = 6;
const TRAIL_PADS = 5;
// Pads between the file's last ledger and the manifest's: "a few ledgers later".
const MANIFEST_GAP = 3;
// A pad is a tower of layers two cubes wide and two deep; a pad that would stand more than twelve
// layers high gets deeper layers instead, so a file read from few ledgers does not leave the frame.
const COLUMNS = 2;
const MIN_ROWS = 2;
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
 * @param {{ chunks: number, ledgers: number }} options chunk count and the number of ledgers the
 *   manifest's range spans
 */
export function layoutCloud({ chunks, ledgers }) {
  if (!Number.isInteger(chunks) || chunks < 1) throw new RangeError(`a cloud needs a whole number of chunks, got ${chunks}`);
  const span = Number.isFinite(ledgers) && ledgers >= 1 ? Math.floor(ledgers) : 1;
  const filePads = Math.min(MAX_FILE_PADS, chunks, span);
  // The file is centred in the ten pads the scene keeps for it, so the ground and the camera frame do
  // not move when the manifest says how many ledgers there are.
  const filePadFirst = LEAD_PADS + Math.floor((MAX_FILE_PADS - filePads) / 2);
  const manifestPad = filePadFirst + filePads - 1 + MANIFEST_GAP;
  const padCount = PAD_COUNT;

  const cubes = [];
  const slotsUsed = new Array(filePads).fill(0);
  const perPad = new Array(filePads).fill(0);
  for (let i = 0; i < chunks; i += 1) perPad[Math.floor((i * filePads) / chunks)] += 1;
  let top = 0;
  const topOfPad = new Array(filePads).fill(0);
  for (let i = 0; i < chunks; i += 1) {
    const local = Math.floor((i * filePads) / chunks);
    const slot = slotsUsed[local]++;
    const pad = filePadFirst + local;
    const cx = padX(pad, padCount);
    const rows = Math.max(MIN_ROWS, Math.ceil(perPad[local] / (COLUMNS * MAX_LAYERS)));
    const column = slot % COLUMNS;
    const row = Math.floor(slot / COLUMNS) % rows;
    const layer = Math.floor(slot / (COLUMNS * rows));
    const x = cx + (column - (COLUMNS - 1) / 2) * (CUBE + GAP) - CUBE / 2;
    const z = SHELF_Z + (row - (rows - 1) / 2) * (CUBE + GAP) - CUBE / 2;
    const y = groundY(cx, SHELF_Z) + 0.004 + layer * (CUBE + GAP);
    cubes.push({ x, y, z, pad });
    topOfPad[local] = Math.max(topOfPad[local], y + CUBE);
    top = Math.max(top, y + CUBE);
  }
  const tallest = topOfPad.indexOf(Math.max(...topOfPad));

  const mx = padX(manifestPad, padCount);
  const manifest = { x: mx - CUBE / 2, y: groundY(mx, SHELF_Z) + 0.004, z: SHELF_Z - CUBE / 2, pad: manifestPad };

  return {
    padCount,
    filePadFirst,
    filePads,
    manifestPad,
    cube: CUBE,
    cubes,
    manifest,
    top,
    fileSpan: [padX(filePadFirst, padCount) - PAD_W / 2, padX(filePadFirst + filePads - 1, padCount) + PAD_W / 2],
    anchors: {
      file: [padX(filePadFirst + tallest, padCount), topOfPad[tallest] + 0.004, SHELF_Z],
      manifest: [mx, manifest.y + CUBE + 0.004, SHELF_Z],
    },
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

/** The eight corners of the unit cube: the marker of a slot whose chunk has not arrived. */
export function cubeCornerPoints() {
  const out = [];
  for (let a = 0; a < 2; a += 1) for (let b = 0; b < 2; b += 1) for (let c = 0; c < 2; c += 1) out.push(a, b, c);
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
  const count = layout.cubes.length;
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
    chunkArrived(index, bytes, time) {
      if (!Number.isInteger(index) || index < 0 || index >= count || arrivedAt[index] !== null) return;
      arrivedAt[index] = time;
      insides[index] = scatterPoints(bytes);
    },

    /** The block's hash matched: its chunks that are drawn get a sweep and then turn lime. */
    blockVerified({ firstChunk, lastChunk }, time) {
      const first = Math.max(0, firstChunk);
      const last = Math.min(count - 1, lastChunk);
      const centres = [];
      for (let k = first; k <= last; k += 1) {
        if (arrivedAt[k] !== null && limeAt[k] === null) centres.push([k, layout.cubes[k].x + layout.cube / 2]);
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
