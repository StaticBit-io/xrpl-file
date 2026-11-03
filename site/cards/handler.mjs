// The card requests: a file page asked for by a link-preview robot, and a card picture. Everything the
// platform provides (static assets, a cache, a ledger connection, work after the response) comes in as
// `deps`, so this runs under the Pages Function and could run under Node on the project's own server.
import { isLinkPreviewRobot, parseCardPath } from './rules.mjs';
import { readCard, readPicture } from './card.mjs';
import { fileCardHtml } from './html.mjs';

/**
 * @typedef {{
 *   assets: { fetch(request: Request): Promise<Response> },
 *   passThrough(request: Request): Promise<Response>,
 *   cache: { match(key: string): Promise<Response | undefined>, put(key: string, response: Response): Promise<void> },
 *   waitUntil(promise: Promise<unknown>): void,
 *   openSource(network: string): { source: object, close(): void },
 *   origin: string,
 *   htmlDeadlineMs?: number,
 *   pictureDeadlineMs?: number,
 * }} Deps
 */

// Content addressed by its hash never changes. A file that is not on the network may still be
// published there, so "missing" is kept an hour; anything else (node down, limit hit, deadline) may
// be gone in minutes.
const LIFETIME = {
  file: 'public, max-age=31536000, immutable',
  missing: 'public, max-age=3600',
  unavailable: 'public, max-age=300',
};
// Robots give up on a slow answer; a manifest (and a cover) usually arrives in well under a second.
const HTML_DEADLINE_MS = 4000;
// A 1 MB picture takes about 3.5 s of wall time to read (spec measurements).
const PICTURE_DEADLINE_MS = 5000;

// Paths the static rule "/:network/:hash[/] -> /file" used to serve. Pages applies no _redirects rule
// on a path a function owns, so the rule is kept here for the two-segment paths that are not cards.
const FILE_PAGE_SHAPE = /^\/[^/]+\/[^/]+\/?$/;
const NO_PICTURE = 'x-card-picture';
const TIMEOUT = Symbol('timeout');

/** @param {Request} request @param {Deps} deps */
export async function handleCardRequest(request, deps) {
  if (request.method !== 'GET' && request.method !== 'HEAD') return deps.passThrough(request);
  const { pathname } = new URL(request.url);
  const card = parseCardPath(pathname);
  if (card === null) return FILE_PAGE_SHAPE.test(pathname) ? filePage(request, deps) : deps.passThrough(request);
  if (card.kind === 'picture') return cardPicture(request, card, deps);
  // A person is never held up: no cache and no ledger before the static page.
  if (!isLinkPreviewRobot(request.headers.get('user-agent'))) return filePage(request, deps);
  return cardPage(request, card, deps);
}

function filePage(request, deps) {
  return deps.assets.fetch(new Request(new URL('/file', request.url), request));
}

const pageKey = (deps, { network, hash }) => `${deps.origin}/__cards/page/${network}/${hash}`;
const pictureKey = (deps, { network, hash }) => `${deps.origin}/__cards/picture/${network}/${hash}`;

function withDeadline(promise, ms) {
  let timer;
  const deadline = new Promise((resolve) => { timer = setTimeout(() => resolve(TIMEOUT), ms); });
  return Promise.race([promise, deadline]).finally(() => clearTimeout(timer));
}

/** The response without its body for a HEAD request. */
function forMethod(request, response) {
  return request.method === 'HEAD' ? new Response(null, { status: response.status, headers: response.headers }) : response;
}

/** Puts after the response, never failing it: a cache that refuses only costs a later re-read. */
function store(deps, key, response) {
  deps.waitUntil(Promise.resolve().then(() => deps.cache.put(key, response)).catch(() => {}));
}

async function cardPage(request, at, deps) {
  const key = pageKey(deps, at);
  const cached = await deps.cache.match(key);
  if (cached) return forMethod(request, cached);

  const { card, cover } = await readCardBefore(at, deps, deps.htmlDeadlineMs ?? HTML_DEADLINE_MS);
  const staticHtml = await (await filePage(new Request(request.url), deps)).text();
  const html = fileCardHtml(staticHtml, card, { origin: deps.origin, ...at });
  const headers = {
    'Content-Type': 'text/html; charset=utf-8',
    'Cache-Control': LIFETIME[card.status],
    'X-Content-Type-Options': 'nosniff',
  };
  store(deps, key, new Response(html, { headers }));
  if (card.status === 'file' && card.picture === 'file') deps.waitUntil(loadPicture(at, deps));
  if (card.status === 'file' && card.picture === 'cover') store(deps, pictureKey(deps, at), pictureResponse(cover));
  return forMethod(request, new Response(html, { headers }));
}

/** readCard against the deadline; past it, or when no source can be opened, the card is unavailable. */
async function readCardBefore(at, deps, ms) {
  let opened;
  try {
    opened = deps.openSource(at.network);
    const result = await withDeadline(readCard(opened.source, at), ms);
    return result === TIMEOUT ? { card: { status: 'unavailable' }, cover: null } : result;
  } catch {
    return { card: { status: 'unavailable' }, cover: null };
  } finally {
    // Closing also ends a read the deadline left running.
    opened?.close();
  }
}

function pictureResponse({ bytes, type }) {
  return new Response(bytes, {
    headers: { 'Content-Type': type, 'Cache-Control': LIFETIME.file, 'X-Content-Type-Options': 'nosniff' },
  });
}

// Picture reads in flight in this isolate, so /og/ joins the read the page step started instead of
// reading the same file twice. Across isolates a second read is possible; the cache bounds it.
const inFlight = new Map();

/**
 * Reads the card picture once per isolate and caches it (a year) or the fact that there is none
 * (minutes). Resolves to the picture or null; never rejects.
 */
function loadPicture(at, deps) {
  const key = pictureKey(deps, at);
  let load = inFlight.get(key);
  if (load) return load;
  load = (async () => {
    let opened;
    try {
      opened = deps.openSource(at.network);
      const picture = await readPicture(opened.source, at);
      try {
        await deps.cache.put(key, picture === null
          ? new Response(null, { headers: { 'Cache-Control': LIFETIME.unavailable, [NO_PICTURE]: 'none' } })
          : pictureResponse(picture));
      } catch {
        // The picture is still answered; the next request reads it again.
      }
      return picture;
    } catch {
      return null;
    } finally {
      opened?.close();
    }
  })();
  inFlight.set(key, load);
  // After the set, not inside the read: a source that fails to open settles the read before it
  // would be in the map, and a delete there would leave this settled entry behind for good.
  load.finally(() => { if (inFlight.get(key) === load) inFlight.delete(key); });
  return load;
}

async function banner(request, deps) {
  const response = await deps.assets.fetch(new Request(new URL('/og-banner.png', request.url)));
  const headers = new Headers(response.headers);
  headers.set('Cache-Control', LIFETIME.unavailable);
  headers.set('X-Content-Type-Options', 'nosniff');
  return forMethod(request, new Response(response.body, { status: response.status, headers }));
}

async function cardPicture(request, at, deps) {
  const cached = await deps.cache.match(pictureKey(deps, at));
  if (cached) return cached.headers.has(NO_PICTURE) ? banner(request, deps) : forMethod(request, cached);

  const load = loadPicture(at, deps);
  const picture = await withDeadline(load, deps.pictureDeadlineMs ?? PICTURE_DEADLINE_MS);
  if (picture === TIMEOUT) {
    // The read keeps going so the next request finds the picture in the cache.
    deps.waitUntil(load);
    return banner(request, deps);
  }
  if (picture === null) return banner(request, deps);
  return forMethod(request, pictureResponse(picture));
}
