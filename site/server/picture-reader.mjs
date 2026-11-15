// The main thread's side of the picture worker: one worker, requests matched to answers by id. A worker
// that dies, or does not answer in time, is dropped: its reads in flight answer transient, and the next
// read starts a new worker.
import { Worker } from 'node:worker_threads';

const PICTURE_READ_CEILING_MS = 25000;
// The worker gives up at its ceiling and closes its source; the main thread waits a little longer so
// the worker's own answer, not this timer, normally decides.
const MAIN_GRACE_MS = 1000;
const TRANSIENT = Object.freeze({ picture: null, outcome: 'transient' });

/**
 * @param {{ sourceModule?: string, sourceData?: object, ceilingMs?: number }} [options]
 *   sourceModule is the URL of a module exporting openSource(network, at, data); sourceData is passed
 *   to it as `data` and must be plain data.
 * @returns {{ read(at: { network: string, hash: string }): Promise<{ picture: { bytes: Uint8Array, type: string } | null, outcome: string }>, close(): Promise<void> }}
 */
export function createPictureReader({
  sourceModule = new URL('./ledger-source.mjs', import.meta.url).href,
  sourceData = {},
  ceilingMs = PICTURE_READ_CEILING_MS,
} = {}) {
  // The current worker and the reads it owes; a worker's 'exit' after it was replaced settles only
  // its own reads, never those already given to its successor.
  let current = null;
  let nextId = 0;

  function drop(entry) {
    if (current === entry) current = null;
    for (const resolve of entry.pending.values()) resolve(TRANSIENT);
    entry.pending.clear();
    entry.worker.terminate().catch(() => {});
  }

  function start() {
    const worker = new Worker(new URL('./picture-worker.mjs', import.meta.url), {
      workerData: { sourceModule, sourceData, ceilingMs },
    });
    // An idle worker must not keep the process alive past its shutdown.
    worker.unref();
    const entry = { worker, pending: new Map() };
    worker.on('message', ({ id, picture, outcome }) => {
      const resolve = entry.pending.get(id);
      entry.pending.delete(id);
      resolve?.({ picture: picture ? { bytes: new Uint8Array(picture.bytes), type: picture.type } : null, outcome });
    });
    worker.on('error', () => drop(entry));
    worker.on('exit', () => drop(entry));
    return entry;
  }

  function read(at) {
    current ??= start();
    const entry = current;
    const id = nextId++;
    return new Promise((resolve) => {
      // A worker stuck in synchronous code would keep the one CPU and every later read; past the
      // limit it is terminated and replaced.
      const timer = setTimeout(() => {
        if (entry.pending.has(id)) drop(entry);
      }, ceilingMs + MAIN_GRACE_MS);
      timer.unref?.();
      entry.pending.set(id, (answer) => {
        clearTimeout(timer);
        resolve(answer);
      });
      entry.worker.postMessage({ id, network: at.network, hash: at.hash });
    });
  }

  async function close() {
    const entry = current;
    current = null;
    if (!entry) return;
    for (const resolve of entry.pending.values()) resolve(TRANSIENT);
    entry.pending.clear();
    await entry.worker.terminate();
  }

  return { read, close };
}
