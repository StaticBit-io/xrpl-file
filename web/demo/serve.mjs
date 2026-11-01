// A tiny static server for trying the element locally: `node web/demo/serve.mjs [port=8790]`.
// It serves web/ on 127.0.0.1 only, never a path outside it and never a directory listing, and
// answers /fixtures/links.json from run/fixtures/links.json (the testnet links of the fixtures,
// which stay out of git).
import { createServer } from 'node:http';
import { readFile, realpath, stat } from 'node:fs/promises';
import { extname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

// The real path, so the containment checks below compare like with like even when web/ is reached through a link.
const WEB_ROOT = await realpath(resolve(fileURLToPath(new URL('..', import.meta.url))));
const FIXTURE_LINKS = resolve(WEB_ROOT, '..', 'run', 'fixtures', 'links.json');
const FIXTURE_ROUTE = '/fixtures/links.json';

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.txt': 'text/plain; charset=utf-8',
};

function send(response, status, type, body = '') {
  response.writeHead(status, {
    'Content-Type': type,
    'Cache-Control': 'no-store',
    'X-Content-Type-Options': 'nosniff',
  });
  response.end(body);
}

const refuse = (response, status) => send(response, status, 'text/plain; charset=utf-8', `${status}\n`);

/** The decoded path, or null when it is not one this server may map to a file. */
function requestPath(rawUrl) {
  let pathname;
  try {
    pathname = decodeURIComponent(new URL(rawUrl, 'http://127.0.0.1').pathname);
  } catch {
    return null;
  }
  // The URL parser has already folded "/./" and "/../" away; what is left must not smuggle them back in.
  if (pathname.includes('\0') || pathname.includes('\\')) return null;
  if (pathname.split('/').includes('..')) return null;
  return pathname;
}

async function serveFile(response, file, head) {
  let info;
  try {
    info = await stat(file);
  } catch {
    return refuse(response, 404);
  }
  if (info.isDirectory()) return refuse(response, 403);
  if (!info.isFile()) return refuse(response, 404);

  const type = TYPES[extname(file).toLowerCase()] ?? 'application/octet-stream';
  return send(response, 200, type, head ? '' : await readFile(file));
}

async function handle(request, response) {
  if (request.method !== 'GET' && request.method !== 'HEAD') return refuse(response, 405);
  const head = request.method === 'HEAD';

  const pathname = requestPath(request.url ?? '/');
  if (pathname === null) return refuse(response, 400);
  if (pathname === FIXTURE_ROUTE) return serveFile(response, FIXTURE_LINKS, head);

  const file = resolve(WEB_ROOT, `.${pathname}`);
  if (file !== WEB_ROOT && !file.startsWith(WEB_ROOT + sep)) return refuse(response, 403);

  // A symlink inside web/ must not lead out of it either.
  let real;
  try {
    real = await realpath(file);
  } catch {
    return refuse(response, 404);
  }
  if (real !== WEB_ROOT && !real.startsWith(WEB_ROOT + sep)) return refuse(response, 403);
  return serveFile(response, real, head);
}

const port = Number(process.argv[2] ?? 8790);
if (!Number.isInteger(port) || port < 0 || port > 65535) {
  console.error(`not a port: ${process.argv[2]}`);
  process.exit(1);
}

createServer((request, response) => {
  handle(request, response).catch(() => {
    if (!response.headersSent) refuse(response, 500);
    else response.destroy();
  });
}).listen(port, '127.0.0.1', () => {
  console.log(`serving ${WEB_ROOT} on http://127.0.0.1:${port}/ (try /demo/dev.html)`);
});
