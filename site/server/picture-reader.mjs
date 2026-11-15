// The main thread's side of the picture worker: one worker, requests matched to answers by id. A worker
// that dies answers every read in flight as transient and is started again on the next read.
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
  let worker = null;
  let nextId = 0;
  const pending = new Map();

  function settleAll() {
    for (const resolve of pending.values()) resolve(TRANSIENT);
    pending.clear();
  }

  function start() {
    const started = new Worker(new URL('./picture-worker.mjs', import.meta.url), {
      workerData: { sourceModule, sourceData, ceilingMs },
    });
    // An idle worker must not keep the process alive past its shutdown.
    started.unref();
    started.on('message', ({ id, picture, outcome }) => {
      const resolve = pending.get(id);
      pending.delete(id);
      resolve?.({ picture: picture ? { bytes: new Uint8Array(picture.bytes), type: picture.type } : null, outcome });
    });
    const lost = () => {
      if (worker === started) worker = null;
      settleAll();
    };
    started.on('error', lost);
    started.on('exit', lost);
    return started;
  }

  function read(at) {
    worker ??= start();
    const id = nextId++;
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        pending.delete(id);
        resolve(TRANSIENT);
      }, ceilingMs + MAIN_GRACE_MS);
      timer.unref?.();
      pending.set(id, (answer) => {
        clearTimeout(timer);
        resolve(answer);
      });
      worker.postMessage({ id, network: at.network, hash: at.hash });
    });
  }

  async function close() {
    const current = worker;
    worker = null;
    settleAll();
    if (current) await current.terminate();
  }

  return { read, close };
}
