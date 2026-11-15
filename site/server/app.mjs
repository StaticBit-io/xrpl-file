// The site on its own server: the built site with Pages' behaviour (static.mjs), and the link-card
// handler for the paths _routes.json gives it, with the platform pieces Pages provided rebuilt here.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { handleCardRequest } from '../cards/handler.mjs';
import { ENDPOINTS, LedgerClient } from '../../web/src/ledger.mjs';
import { parseRedirects } from '../redirects.mjs';
import { parseHeaders } from '../headers.mjs';
import { createStaticSite } from './static.mjs';
import { parseRoutes, routedToFunction } from './routes.mjs';
import { createLimiter } from './limits.mjs';
import { toRequest, writeResponse } from './node-http.mjs';

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms).unref?.());

/**
 * @param {{ siteRoot: string, origin: string, release: string,
 *   cache: { match(key: string): Promise<Response|undefined>, put(key: string, response: Response): Promise<void> },
 *   openLedger?: (network: string) => { close(): void }, maxReads?: number, maxCardRequests?: number,
 *   log?: (line: string) => void }} options
 *   siteRoot is the real path of the built site; its _redirects, _headers and _routes.json are parsed
 *   here, and a file that does not parse rejects, so a broken release never starts.
 */
export async function createApp({
  siteRoot,
  origin,
  release,
  cache,
  openLedger = (network) => new LedgerClient(ENDPOINTS[network]),
  maxReads = 4,
  maxCardRequests = 32,
  log = () => {},
}) {
  const host = (name) => readFile(join(siteRoot, name), 'utf8');
  const site = createStaticSite({
    root: siteRoot,
    redirects: parseRedirects(await host('_redirects')),
    headers: parseHeaders(await host('_headers')),
    release,
    log,
  });
  const routes = parseRoutes(await host('_routes.json'));
  // One CPU serves everyone: past these, a robot gets the static answer rather than a queue.
  const reads = createLimiter(maxReads);
  const cardRequests = createLimiter(maxCardRequests);
  const background = new Set();

  const deps = {
    assets: { fetch: (request) => site.respond(request) },
    passThrough: (request) => site.respond(request),
    cache,
    waitUntil(promise) {
      const tracked = Promise.resolve(promise).catch(() => {});
      background.add(tracked);
      tracked.finally(() => background.delete(tracked));
    },
    // Past the cap this throws, and the handler answers the generic card or the banner, briefly cached.
    openSource(network) {
      const releaseRead = reads.tryAcquire();
      if (!releaseRead) throw new Error('card reads at capacity');
      let client;
      try {
        client = openLedger(network);
      } catch (error) {
        releaseRead();
        throw error;
      }
      return {
        source: client,
        close() {
          releaseRead();
          client.close?.();
        },
      };
    },
    origin,
  };

  function named(response) {
    if (response.headers.get('x-xrpl-file-release') === release) return response;
    const headers = new Headers(response.headers);
    headers.set('X-Xrpl-File-Release', release);
    return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
  }

  async function handle(request) {
    const { pathname } = new URL(request.url);
    if (!routedToFunction(pathname, routes)) return site.respond(request);
    const releaseRequest = cardRequests.tryAcquire();
    if (!releaseRequest) return site.respond(request);
    try {
      return named(await handleCardRequest(request, deps));
    } catch (error) {
      // As passThroughOnException on Pages: whatever the handler did not catch, the static site answers.
      log(`card ${pathname}: ${error?.message ?? error}`);
      return site.respond(request);
    } finally {
      releaseRequest();
    }
  }

  /** Resolves when the background work has settled, or after `ms`. */
  async function settle(ms) {
    const until = delay(ms).then(() => false);
    while (background.size > 0) {
      if ((await Promise.race([Promise.all([...background]).then(() => true), until])) === false) return;
    }
  }

  return { handle, waitUntil: deps.waitUntil, settle };
}

/** The HTTP server in front of the app; requests are read on the configured origin. */
export function createSiteServer(app, { origin, log = () => {} }) {
  return createServer(async (req, res) => {
    try {
      await writeResponse(res, await app.handle(toRequest(req, origin)), req.method);
    } catch (error) {
      log(`request ${req.url}: ${error?.message ?? error}`);
      if (!res.headersSent) res.writeHead(500);
      res.end();
    }
  });
}

/**
 * Stops accepting connections, lets started responses finish and background work settle, all within
 * `ms`; then closes what is left. A picture read still running is a cache fill the next request redoes.
 */
export async function shutdown(server, app, ms = 5000) {
  const closed = new Promise((resolve) => server.close(resolve));
  server.closeIdleConnections();
  await Promise.race([Promise.all([closed, app.settle(ms)]), delay(ms)]);
  server.closeAllConnections();
}
