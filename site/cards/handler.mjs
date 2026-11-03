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
 *   pictureReadCeilingMs?: number,
 *   now?: () => number,
 * }} Deps
 */

// How long the cache keeps an answer. Content addressed by its hash never changes, and neither does
// "these verified bytes are not a picture". A file that is not on the network may still be published
// there, and a cover readCover refused may have been refused for a node's failure, so those are kept
// an hour; a node failure, a limit or a deadline may be gone in minutes.
const LIFETIME = {
  final: 'public, max-age=31536000, immutable',
  hour: 'public, max-age=3600',
  brief: 'public, max-age=300',
};
const PICTURE_LIFETIME = { final: LIFETIME.final, missing: LIFETIME.hour, 'cover-unread': LIFETIME.hour, transient: LIFETIME.brief };
// What the robot itself is told: the card HTML shares its URL with a person's page, so nothing
// downstream may keep it and hand it to a person.
const CLIENT_HTML_CACHE = 'no-cache';

// Robots give up on a slow answer; a manifest (and a cover) usually arrives in well under a second.
const HTML_DEADLINE_MS = 4000;
// A 1 MB picture takes about 3.5 s of wall time to read (spec measurements).
const PICTURE_DEADLINE_MS = 5000;
// Below the 30 s that waitUntil keeps a request's work alive after the response, so a picture read
// always settles, closes its socket and leaves the in-flight map while the platform still runs it.
const PICTURE_READ_CEILING_MS = 25000;
// A read the platform dropped (its request cancelled, its continuations never run) never settles; past
// this age its entry is ignored, so a later request reads again instead of joining it forever.
const IN_FLIGHT_MAX_MS = 30000;

// Bumped when the card output changes: a cached card keeps its HTML for a year otherwise.
const KEY_VERSION = 'v1';

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
  if (card.kind === 'picture') {
    try {
      return await cardPicture(request, card, deps);
    } catch {
      return banner(request, deps);
    }
  }
  // A person is never held up: no cache and no ledger before the static page.
  if (!isLinkPreviewRobot(request.headers.get('user-agent'))) return filePage(request, deps);
  try {
    return await cardPage(request, card, deps);
  } catch {
    // Whatever failed, the robot still gets the page with the site's generic card.
    return filePage(request, deps);
  }
}

function filePage(request, deps) {
  return deps.assets.fetch(new Request(new URL('/file', request.url), request));
}

const pageKey = (deps, { network, hash }) => `${deps.origin}/__cards/${KEY_VERSION}/page/${network}/${hash}`;
const pictureKey = (deps, { network, hash }) => `${deps.origin}/__cards/${KEY_VERSION}/picture/${network}/${hash}`;
const now = (deps) => (deps.now ?? Date.now)();

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

/** The card HTML as the robot gets it, from the copy the cache keeps. */
function forRobot(request, response) {
  const headers = new Headers(response.headers);
  headers.set('Cache-Control', CLIENT_HTML_CACHE);
  headers.set('Vary', 'User-Agent');
  return forMethod(request, new Response(response.body, { status: response.status, headers }));
}

function pageLifetime(card, coverUnread) {
  if (card.status === 'missing') return LIFETIME.hour;
  if (card.status !== 'file') return LIFETIME.brief;
  return coverUnread ? LIFETIME.hour : LIFETIME.final;
}

async function cardPage(request, at, deps) {
  const key = pageKey(deps, at);
  const cached = await deps.cache.match(key);
  if (cached) return forRobot(request, cached);

  const { card, cover, coverUnread } = await readCardBefore(at, deps, deps.htmlDeadlineMs ?? HTML_DEADLINE_MS);
  const page = await filePage(new Request(request.url), deps);
  // A card is never built over an error page, and an error page is never kept.
  if (!page.ok) return forMethod(request, page);
  const html = fileCardHtml(await page.text(), card, { origin: deps.origin, ...at });
  const headers = {
    'Content-Type': 'text/html; charset=utf-8',
    'Cache-Control': pageLifetime(card, coverUnread),
    'X-Content-Type-Options': 'nosniff',
  };
  store(deps, key, new Response(html, { headers }));
  if (card.status === 'file' && card.picture === 'file') deps.waitUntil(loadPicture(at, deps));
  if (card.status === 'file' && card.picture === 'cover') store(deps, pictureKey(deps, at), pictureResponse(cover, LIFETIME.final));
  return forRobot(request, new Response(html, { headers }));
}

/** readCard against the deadline; past it, or when no source can be opened, the card is unavailable. */
async function readCardBefore(at, deps, ms) {
  const unavailable = { card: { status: 'unavailable' }, cover: null, coverUnread: false };
  let opened;
  try {
    opened = deps.openSource(at.network);
    const result = await withDeadline(readCard(opened.source, at), ms);
    return result === TIMEOUT ? unavailable : result;
  } catch {
    return unavailable;
  } finally {
    // Closing also ends a read the deadline left running.
    opened?.close();
  }
}

function pictureResponse({ bytes, type }, cacheControl) {
  return new Response(bytes, {
    headers: { 'Content-Type': type, 'Cache-Control': cacheControl, 'X-Content-Type-Options': 'nosniff' },
  });
}

// Picture reads in flight in this isolate, so /og/ joins the read the page step started instead of
// reading the same file twice. Across isolates a second read is possible; the cache bounds it.
const inFlight = new Map();

/** readPicture under the ceiling; giving up closes the source, which ends the read. */
async function readPictureUnderCeiling(at, deps) {
  let opened;
  try {
    opened = deps.openSource(at.network);
    const result = await withDeadline(readPicture(opened.source, at), deps.pictureReadCeilingMs ?? PICTURE_READ_CEILING_MS);
    return result === TIMEOUT ? { picture: null, outcome: 'transient' } : result;
  } catch {
    return { picture: null, outcome: 'transient' };
  } finally {
    opened?.close();
  }
}

/**
 * Reads the card picture once per isolate and caches it, or the fact that there is none, for as long
 * as that answer holds. Resolves to the picture (or null) and that lifetime; never rejects.
 */
function loadPicture(at, deps) {
  const key = pictureKey(deps, at);
  const running = inFlight.get(key);
  if (running && now(deps) - running.startedAt < IN_FLIGHT_MAX_MS) return running.load;
  const load = (async () => {
    const { picture, outcome } = await readPictureUnderCeiling(at, deps);
    const cacheControl = PICTURE_LIFETIME[outcome];
    try {
      await deps.cache.put(key, picture === null
        ? new Response(null, { headers: { 'Cache-Control': cacheControl, [NO_PICTURE]: 'none' } })
        : pictureResponse(picture, cacheControl));
    } catch {
      // The picture is still answered; the next request reads it again.
    }
    return { picture, cacheControl };
  })();
  const entry = { load, startedAt: now(deps) };
  inFlight.set(key, entry);
  // After the set, not inside the read: a source that fails to open settles the read before it
  // would be in the map, and a delete there would leave this settled entry behind for good.
  load.finally(() => { if (inFlight.get(key) === entry) inFlight.delete(key); });
  return load;
}

async function banner(request, deps) {
  const response = await deps.assets.fetch(new Request(new URL('/og-banner.png', request.url)));
  const headers = new Headers(response.headers);
  headers.set('Cache-Control', LIFETIME.brief);
  headers.set('X-Content-Type-Options', 'nosniff');
  return forMethod(request, new Response(response.body, { status: response.status, headers }));
}

async function cardPicture(request, at, deps) {
  const cached = await deps.cache.match(pictureKey(deps, at));
  if (cached) return cached.headers.has(NO_PICTURE) ? banner(request, deps) : forMethod(request, cached);

  const load = loadPicture(at, deps);
  // Under waitUntil from the start: a robot that hangs up before the deadline must not take the
  // read down with its request, or the read would never settle.
  deps.waitUntil(load);
  const result = await withDeadline(load, deps.pictureDeadlineMs ?? PICTURE_DEADLINE_MS);
  if (result === TIMEOUT || result.picture === null) return banner(request, deps);
  return forMethod(request, pictureResponse(result.picture, result.cacheControl));
}
