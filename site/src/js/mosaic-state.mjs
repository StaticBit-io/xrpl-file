// The hero read's state and the rules that move it. Pure: no DOM, no timers, no reader. The clock is
// an argument, so "no chunk for 20 s" is testable without waiting. hero-read.mjs wires these to the real
// reader; cloud.mjs draws what they say. The grid, tone and turn-over helpers date from the flat chunk
// mosaic the point cloud replaced; nothing draws them now.
//
// A cell is 'empty' until its chunk arrives, 'lit' from then on, and 'verified' once the block that
// holds it has matched its hash. A chunk is reported before its block verifies, so lit never means
// verified; the picture itself is only drawn when every cell is verified.

/** How long the reader may go without delivering a chunk, counted from when the mosaic was mounted. */
export const STALL_MS = 20_000;

/**
 * @typedef {'empty'|'lit'|'verified'} CellState
 * @typedef {'reading'|'developing'|'done'|'failed'} Phase
 * @typedef {object} MosaicState
 * @property {number} columns
 * @property {number} rows
 * @property {number} chunks
 * @property {CellState[]} cells one per chunk, in chunk order, row by row
 * @property {(number|null)[]} texture mean byte value / 255 of each chunk read so far, else null
 * @property {Phase} phase
 * @property {{ code: string, message?: string }|null} error set when phase is 'failed'
 * @property {number} activityAt clock reading of the last chunk, or of the mount before the first
 */

/**
 * The grid follows the picture's aspect ratio (width / height): the column count is the one that
 * makes the cells closest to square, and rows are added until every chunk has a cell. Cells past the
 * last chunk do not exist; the last row is simply shorter.
 *
 * @param {{ chunks: number, aspect: number, now?: number }} options
 * @returns {MosaicState}
 */
export function createMosaic({ chunks, aspect, now = 0 }) {
  if (!Number.isInteger(chunks) || chunks < 1) throw new RangeError(`a mosaic needs a whole number of chunks, got ${chunks}`);
  if (!Number.isFinite(aspect) || aspect <= 0) throw new RangeError(`a mosaic needs a positive aspect ratio, got ${aspect}`);
  const columns = Math.min(chunks, Math.max(1, Math.round(Math.sqrt(chunks * aspect))));
  return {
    columns,
    rows: Math.ceil(chunks / columns),
    chunks,
    cells: new Array(chunks).fill('empty'),
    texture: new Array(chunks).fill(null),
    phase: 'reading',
    error: null,
    activityAt: now,
  };
}

function meanOf(bytes) {
  if (bytes.length === 0) return 0;
  let sum = 0;
  for (let i = 0; i < bytes.length; i += 1) sum += bytes[i];
  return sum / bytes.length / 255;
}

/** The chunk's cell lights and takes a tone from the chunk's own bytes. */
export function chunkArrived(state, { index, bytes }, now = state.activityAt) {
  if (state.phase !== 'reading' || !Number.isInteger(index) || index < 0 || index >= state.chunks) return state;
  const cells = state.cells.slice();
  const texture = state.texture.slice();
  // The reader may deliver a chunk a second time only after its block verified; never step back.
  if (cells[index] === 'empty') cells[index] = 'lit';
  texture[index] = meanOf(bytes);
  return { ...state, cells, texture, activityAt: now };
}

/** Marks the block's chunks, and only those, verified. A range running past the last chunk is clamped. */
export function blockVerified(state, { firstChunk, lastChunk }) {
  if (state.phase !== 'reading') return state;
  const cells = state.cells.slice();
  const last = Math.min(lastChunk, state.chunks - 1);
  for (let i = Math.max(0, firstChunk); i <= last; i += 1) cells[i] = 'verified';
  return { ...state, cells };
}

/** Moves a fully verified mosaic to 'developing'; any other state comes back unchanged. */
export function allVerified(state) {
  if (state.phase !== 'reading' || !state.cells.every((cell) => cell === 'verified')) return state;
  return { ...state, phase: 'developing' };
}

/** The picture has been shown. */
export function developed(state) {
  return state.phase === 'developing' ? { ...state, phase: 'done' } : state;
}

/**
 * The read cannot go on. The cells read so far stay as they are. A finished mosaic, or one that has
 * already failed, keeps what it has: the first error is the one that explains the picture.
 */
export function failed(state, error) {
  if (state.phase === 'done' || state.phase === 'failed') return state;
  const code = typeof error?.code === 'string' && error.code !== '' ? error.code : 'Unknown';
  const message = typeof error?.message === 'string' ? error.message : undefined;
  return { ...state, phase: 'failed', error: message === undefined ? { code } : { code, message } };
}

/** Fails a reading mosaic with 'Stalled' once STALL_MS have passed without a chunk. */
export function checkStall(state, now) {
  if (state.phase !== 'reading' || now - state.activityAt < STALL_MS) return state;
  return failed(state, { code: 'Stalled', message: `no chunk arrived for ${STALL_MS / 1000} s` });
}

/**
 * The range of tones read so far. Compressed pictures have a mean byte value near 0.5 in every chunk,
 * so drawing the raw value would show nothing; the real spread is stretched to the cell's full tone
 * range instead. Fewer than two distinct values give the unstretched range.
 */
export function textureSpan(texture) {
  let min = Infinity;
  let max = -Infinity;
  for (const value of texture) {
    if (value === null) continue;
    if (value < min) min = value;
    if (value > max) max = value;
  }
  return max - min > 1e-9 ? { min, max } : { min: 0, max: 1 };
}

/**
 * Animation lengths in milliseconds. Reduced motion changes cells without animation (it wins over
 * fast). A fast replay, for a visitor who has already seen the animation, runs everything that
 * follows the last chunk (the verify wave, then the turn-over) within 300 ms.
 */
export function motionTimings({ fast = false, reducedMotion = false }) {
  if (reducedMotion) return { lit: 0, verify: 0, verifyWave: 0, flip: 0, developSpread: 0 };
  if (fast) return { lit: 40, verify: 40, verifyWave: 40, flip: 80, developSpread: 100 };
  return { lit: 180, verify: 240, verifyWave: 700, flip: 380, developSpread: 1100 };
}

/** Start offset of a cell in the verify wave: it runs along the block's chunks in order. */
export function waveDelay(index, firstChunk, lastChunk, spread) {
  return lastChunk > firstChunk ? ((index - firstChunk) / (lastChunk - firstChunk)) * spread : 0;
}

/** Start offset of a cell in the turn-over: a sweep from the top left corner to the bottom right. */
export function developDelay(index, columns, rows, spread) {
  const steps = columns + rows - 2;
  return steps > 0 ? (((index % columns) + Math.floor(index / columns)) / steps) * spread : 0;
}
