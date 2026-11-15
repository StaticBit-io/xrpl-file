// A ledger for the picture worker in tests: the worker imports this module by the URL in its
// workerData and opens sources from the plain data the test put there (functions cannot cross into a
// worker). `crashOn` makes the worker die on that hash, as a worker that runs out of memory would.
import { FakeSource } from '../../../web/test/vectors.mjs';

export function openSource(network, at, data) {
  if (data.crashOn === at.hash) process.exit(1);
  const transactions = data.ledgers?.[at.hash] ?? [];
  return { source: new FakeSource(transactions), close() {} };
}
