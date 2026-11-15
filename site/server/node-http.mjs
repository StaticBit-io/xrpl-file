// node:http on one side, the web Request/Response the card handler and the static site speak on the
// other.

/**
 * The request as a web Request on the configured origin. Neither the Host header nor an absolute-form
 * request target may choose the origin: it ends up in cached cards and og:image URLs.
 */
export function toRequest(req, origin) {
  const target = new URL(req.url, 'http://placeholder');
  const headers = new Headers();
  for (const [name, value] of Object.entries(req.headers)) {
    if (name === 'host' || value === undefined) continue;
    for (const one of Array.isArray(value) ? value : [value]) headers.append(name, one);
  }
  return new Request(new URL(target.pathname + target.search, origin), { method: req.method, headers });
}

/** Writes the response; a HEAD request gets the headers only. Bodies are small (pictures ≤ 1 MB). */
export async function writeResponse(res, response, method) {
  res.writeHead(response.status, Object.fromEntries(response.headers));
  if (method === 'HEAD' || response.body === null) {
    res.end();
    return;
  }
  res.end(Buffer.from(await response.arrayBuffer()));
}
