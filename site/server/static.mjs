// The built site served the way Cloudflare Pages serves it: the same resolution (site/resolve.mjs),
// Pages' default headers, the site's _headers, content-hash ETags and 304s.
import { createHash } from 'node:crypto';
import { extname } from 'node:path';
import { HOST_FILES, resolveRequest, TYPES } from '../resolve.mjs';
import { headersFor } from '../headers.mjs';

// Sent by Pages on every response, whatever _headers says (measured on the live site).
const PAGES_DEFAULTS = [
  ['Access-Control-Allow-Origin', '*'],
  ['Referrer-Policy', 'strict-origin-when-cross-origin'],
];
// Pages' Cache-Control when _headers sets none; redirects and 405s get none.
const DEFAULT_CACHE = { 200: 'public, max-age=0, must-revalidate', 404: 'no-store' };

/** True when an If-None-Match header names the ETag, with weak comparison as for GET. */
function etagMatches(ifNoneMatch, etag) {
  if (!ifNoneMatch) return false;
  return ifNoneMatch.split(',').some((candidate) => {
    const value = candidate.trim();
    return value === '*' || value.replace(/^W\//, '') === etag;
  });
}

/**
 * @param {{ root: string, redirects: object[], headers: object[], release: string,
 *   log?: (line: string) => void, fs?: { stat?: Function, readFile?: Function } }} options
 *   root is the real path of the built site; fs replaces node:fs/promises for tests.
 */
export function createStaticSite({ root, redirects, headers, release, log = () => {}, fs = {} }) {
  const etags = new Map();

  function etagOf(file, body) {
    let etag = etags.get(file);
    if (!etag) {
      etag = `"${createHash('sha256').update(body).digest('hex').slice(0, 32)}"`;
      etags.set(file, etag);
    }
    return etag;
  }

  function baseHeaders(pathname, status) {
    const result = new Headers(PAGES_DEFAULTS);
    if (DEFAULT_CACHE[status]) result.set('Cache-Control', DEFAULT_CACHE[status]);
    for (const [name, value] of headersFor(pathname, headers)) result.set(name, value);
    result.set('X-Xrpl-File-Release', release);
    return result;
  }

  async function respond(request) {
    const url = new URL(request.url);
    const { pathname } = url;
    if (request.method !== 'GET' && request.method !== 'HEAD') {
      const allowed = baseHeaders(pathname, 405);
      allowed.set('Allow', 'GET, HEAD');
      return new Response(null, { status: 405, headers: allowed });
    }
    const result = await resolveRequest(root, pathname, redirects, {
      search: url.search,
      htmlStatus: 308,
      dirStatus: 308,
      hidden: HOST_FILES,
      ...fs,
    });
    if (result.status === 400) return new Response('Bad request\n', { status: 400, headers: baseHeaders(pathname, 400) });
    if (result.status === 500) {
      log(`static ${pathname}: ${result.error?.code ?? ''} ${result.error?.message ?? result.error}`);
      return new Response('Internal error\n', { status: 500, headers: baseHeaders(pathname, 500) });
    }
    const responseHeaders = baseHeaders(pathname, result.status);
    if (result.location) {
      responseHeaders.set('Location', result.location);
      return new Response(null, { status: result.status, headers: responseHeaders });
    }
    responseHeaders.set('Content-Type', result.file ? TYPES[extname(result.file)] ?? 'application/octet-stream' : TYPES['.txt']);
    responseHeaders.set('Content-Length', String(result.body.length));
    if (result.status === 200 && result.file) {
      const etag = etagOf(result.file, result.body);
      responseHeaders.set('ETag', etag);
      if (etagMatches(request.headers.get('if-none-match'), etag)) {
        responseHeaders.delete('Content-Length');
        return new Response(null, { status: 304, headers: responseHeaders });
      }
    }
    const body = request.method === 'HEAD' ? null : result.body;
    return new Response(body, { status: result.status, headers: responseHeaders });
  }

  return { respond };
}
