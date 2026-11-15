// The ledger the picture worker reads from in production. The worker imports a source module by URL
// (functions cannot be handed to a worker); tests give it site/test/support/fake-ledger-source.mjs.
import { ENDPOINTS, LedgerClient } from '../../web/src/ledger.mjs';

/** @param {{ off?: boolean }} data `off` refuses every read (SITE_LEDGER=off). */
export function openSource(network, at, data) {
  if (data.off) throw new Error('ledger reads are off (SITE_LEDGER=off)');
  const client = new LedgerClient(ENDPOINTS[network]);
  return { source: client, close: () => client.close() };
}
