// Cloudflare Pages Function for file link cards. site/src/_routes.json sends it only /<network>/* and
// /og/*; everything else is served by Pages as static files, without an invocation. The card logic
// lives in site/cards/ and knows nothing of Cloudflare; this file only hands it the platform.
import { handleCardRequest } from '../cards/handler.mjs';
import { ENDPOINTS, LedgerClient } from '../../web/src/ledger.mjs';

export function onRequest(context) {
  const { request } = context;
  // An exception the handler did not catch serves the static site instead of an error page.
  context.passThroughOnException();
  // Per data centre; the keys are URLs under the site's own origin that no page links to.
  const cache = caches.default;
  return handleCardRequest(request, {
    assets: context.env.ASSETS,
    // Pages applies the static rules (headers, the 404 page) to what next() serves.
    passThrough: () => context.next(),
    cache: {
      match: (key) => cache.match(new Request(key)),
      put: (key, response) => cache.put(new Request(key), response),
    },
    waitUntil: (promise) => context.waitUntil(promise),
    openSource(network) {
      const client = new LedgerClient(ENDPOINTS[network]);
      return { source: client, close: () => client.close() };
    },
    origin: new URL(request.url).origin,
  });
}
