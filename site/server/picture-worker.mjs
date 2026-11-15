// A card picture read off the main thread: verifying a 1 MB file is hundreds of milliseconds of CPU,
// and on the one main thread a person's page waited behind it (measured on the server, Task 10 of
// docs/superpowers/plans/2026-11-12-site-server.md). Here the operating system shares the CPU instead.
import { parentPort, workerData } from 'node:worker_threads';
import { readPicture } from '../cards/card.mjs';

const { sourceModule, sourceData = {}, ceilingMs } = workerData;
const { openSource } = await import(sourceModule);
const TIMEOUT = Symbol('timeout');

function withDeadline(promise, ms) {
  let timer;
  const deadline = new Promise((resolve) => { timer = setTimeout(() => resolve(TIMEOUT), ms); });
  return Promise.race([promise, deadline]).finally(() => clearTimeout(timer));
}

parentPort.on('message', async ({ id, network, hash }) => {
  let opened;
  let answer = { picture: null, outcome: 'transient' };
  try {
    opened = openSource(network, { hash }, sourceData);
    const result = await withDeadline(readPicture(opened.source, { hash }), ceilingMs);
    if (result !== TIMEOUT) answer = result;
  } catch {
    // A source that cannot open, or a read that throws: the node may do better next time.
  } finally {
    // Closing also ends a read the ceiling left running.
    opened?.close();
  }
  parentPort.postMessage({ id, ...answer });
});
