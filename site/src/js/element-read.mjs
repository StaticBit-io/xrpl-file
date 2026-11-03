// The file page's read of its file, as the point cloud sees it: the <xrpl-file> element does the
// reading, and this module turns the events it dispatches into the listener calls cloud.mjs already
// understands (the same contract as hero-read.mjs, minus the reader, the stall clock and the
// developing phase: the element owns its own failure and timing).
//
// Order of calls, each reported once and never ahead of the element: onManifest, then onChunk the
// first time a cube is touched, onBlockVerified when every chunk of a cube is verified, then onDone
// on `load` or onFailed on `error`.

// The cloud shows at most this many cubes, however large the file: a file of 20000 chunks would
// otherwise ask the scene for thousands of cubes.
export const CUBE_CAP = 240;

const SEED_BYTES = 32;
const EVENTS = ['xrpl-file-manifest', 'xrpl-file-chunk', 'xrpl-file-block', 'load', 'error'];

/**
 * The element's chunk events carry no payload, but a cube's inside needs bytes to scatter. These are
 * derived from the cube and its ledger alone, so a reload of the same file draws the same cube.
 *
 * @returns {Uint8Array} 32 xorshift32 bytes seeded from both values
 */
export function seedBytes(index, ledger) {
  // Each input goes through its own odd multiplier so (a, b) and (b, a) do not collide.
  let state = (Math.imul((index >>> 0) + 1, 0x9e3779b1) ^ Math.imul((ledger >>> 0) + 1, 0x85ebca6b)) >>> 0;
  if (state === 0) state = 0x6d2b79f5;
  const out = new Uint8Array(SEED_BYTES);
  for (let i = 0; i < SEED_BYTES; i += 1) {
    state ^= state << 13;
    state >>>= 0;
    state ^= state >>> 17;
    state ^= state << 5;
    state >>>= 0;
    // The high byte: xorshift's low bits are the weakest.
    out[i] = state >>> 24;
  }
  return out;
}

/**
 * Reports the element's read to `options.listener`.
 *
 * @param {object} options
 * @param {object} [options.listener] optional callbacks, each called with one object:
 *   onManifest({ manifest, chunks, fileChunks, perCube, time }) where `chunks` is the cube count,
 *   onChunk({ index, bytes, ledger, time }) where `index` is the cube, onBlockVerified({ firstChunk,
 *   lastChunk, time }) naming one cube, onDone({ time }), onFailed({ error }); every phase change
 *   also calls onPhase(phase)
 * @param {object} env
 * @param {EventTarget} env.element the `<xrpl-file>` element
 * @param {() => number} [env.now] the clock
 * @returns {{ done: Promise<void>, destroy: () => void, readonly phase: string, readonly state: object,
 *   readonly manifest: object|null }} `done` settles, and never rejects, once the read is done or
 *   failed; `destroy()` stops listening.
 */
export function startElementRead(options, env) {
  const { element, now = () => performance.now() } = env;
  const listener = options.listener ?? {};

  let phase = 'reading';
  let manifest = null;
  let fileChunks = 0;
  let perCube = 1;
  let cubes = 0;
  // Per cube: whether a chunk has appeared, how many of its chunks are verified, how many it holds.
  let seen = new Uint8Array(0);
  let verified = new Uint32Array(0);
  let sizes = new Uint32Array(0);
  let settled = false;
  let resolveDone;
  const done = new Promise((resolve) => { resolveDone = resolve; });

  function detach() {
    for (const type of EVENTS) element.removeEventListener(type, handlers[type]);
  }

  function settle(next) {
    settled = true;
    phase = next;
    detach();
  }

  function onManifest(event) {
    if (settled || manifest !== null) return;
    const detail = event.detail;
    if (!(detail?.chunks >= 1)) return;
    manifest = detail;
    fileChunks = detail.chunks;
    perCube = Math.max(1, Math.ceil(fileChunks / CUBE_CAP));
    cubes = Math.ceil(fileChunks / perCube);
    seen = new Uint8Array(cubes);
    verified = new Uint32Array(cubes);
    sizes = new Uint32Array(cubes).fill(perCube);
    // The last cube holds whatever is left, which is fewer than perCube unless it divides evenly.
    sizes[cubes - 1] = fileChunks - perCube * (cubes - 1);
    listener.onManifest?.({ manifest: detail, chunks: cubes, fileChunks, perCube, time: now() });
  }

  function onChunk(event) {
    if (settled || manifest === null) return;
    const { index, ledger } = event.detail ?? {};
    if (!Number.isInteger(index) || index < 0 || index >= fileChunks) return;
    const cube = Math.floor(index / perCube);
    if (seen[cube] === 1) return;
    seen[cube] = 1;
    listener.onChunk?.({ index: cube, ledger, bytes: seedBytes(cube, ledger), time: now() });
  }

  function onBlock(event) {
    if (settled || manifest === null) return;
    const { firstChunk, lastChunk } = event.detail ?? {};
    if (!Number.isInteger(firstChunk) || !Number.isInteger(lastChunk)) return;
    const first = Math.max(0, firstChunk);
    const last = Math.min(fileChunks - 1, lastChunk);
    if (first > last) return;
    const time = now();
    for (let cube = Math.floor(first / perCube); cube <= Math.floor(last / perCube); cube += 1) {
      const from = Math.max(first, cube * perCube);
      const to = Math.min(last, cube * perCube + sizes[cube] - 1);
      const before = verified[cube];
      verified[cube] += to - from + 1;
      if (before < sizes[cube] && verified[cube] >= sizes[cube]) {
        listener.onBlockVerified?.({ firstChunk: cube, lastChunk: cube, time });
      }
    }
  }

  function onLoad() {
    if (settled) return;
    settle('done');
    listener.onPhase?.(phase);
    listener.onDone?.({ time: now() });
    resolveDone();
  }

  function onError(event) {
    if (settled) return;
    settle('failed');
    // The element names the failure in `name`; `code` is accepted too for a detail that carries one.
    const code = event.detail?.code ?? event.detail?.name ?? 'Unknown';
    listener.onPhase?.(phase);
    listener.onFailed?.({ error: { code } });
    resolveDone();
  }

  const handlers = {
    'xrpl-file-manifest': onManifest,
    'xrpl-file-chunk': onChunk,
    'xrpl-file-block': onBlock,
    load: onLoad,
    error: onError,
  };
  for (const type of EVENTS) element.addEventListener(type, handlers[type]);

  return {
    done,
    get phase() {
      return phase;
    },
    get state() {
      return { phase, chunks: cubes, fileChunks, perCube };
    },
    get manifest() {
      return manifest;
    },
    destroy() {
      settled = true;
      detach();
      resolveDone();
    },
  };
}
