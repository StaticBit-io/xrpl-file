// The hero read's state and the rules that move it. Pure: no DOM, no timers, no reader. The clock is
// an argument, so "no chunk for 20 s" is testable without waiting. hero-read.mjs wires these to the real
// reader; cloud.mjs draws what they say.
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
 * @property {number} chunks
 * @property {CellState[]} cells one per chunk, in chunk order
 * @property {Phase} phase
 * @property {{ code: string, message?: string }|null} error set when phase is 'failed'
 * @property {number} activityAt clock reading of the last chunk, or of the mount before the first
 */

/**
 * @param {{ chunks: number, now?: number }} options
 * @returns {MosaicState}
 */
export function createMosaic({ chunks, now = 0 }) {
  if (!Number.isInteger(chunks) || chunks < 1) throw new RangeError(`a mosaic needs a whole number of chunks, got ${chunks}`);
  return {
    chunks,
    cells: new Array(chunks).fill('empty'),
    phase: 'reading',
    error: null,
    activityAt: now,
  };
}

/** The chunk's cell lights. */
export function chunkArrived(state, { index }, now = state.activityAt) {
  if (state.phase !== 'reading' || !Number.isInteger(index) || index < 0 || index >= state.chunks) return state;
  const cells = state.cells.slice();
  // The reader may deliver a chunk a second time only after its block verified; never step back.
  if (cells[index] === 'empty') cells[index] = 'lit';
  return { ...state, cells, activityAt: now };
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
