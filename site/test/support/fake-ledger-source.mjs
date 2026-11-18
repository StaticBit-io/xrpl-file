// A ledger for the picture worker in tests: the worker imports this module by the URL in its
// workerData and opens sources from the plain data the test put there (functions cannot cross into a
// worker). `crashOn` makes the worker die on that hash, as a worker that runs out of memory would;
// `hangOn` makes it spin for ever.
import { FakeSource } from '../../../web/test/vectors.mjs';

export function openSource(network, at, data) {
  if (data.crashOn === at.hash) process.exit(1);
  // A worker stuck in synchronous code, which only terminate() can stop.
  if (data.hangOn === at.hash) for (;;);
  const transactions = data.ledgers?.[at.hash] ?? [];
  return { source: new FakeSource(transactions), close() {} };
}
