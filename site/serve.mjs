// Dev server for site/dist: 127.0.0.1 only, applying the same _redirects rules the build checked
// (first match wins, a 200 rule rewrites without redirecting). It never takes over a busy port: on a
// clash it moves to the next one. Not a deployment target.
//
//   node site/build.mjs && node site/serve.mjs
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { dirname, extname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { applyRedirects, parseRedirects } from './redirects.mjs';

export const START_PORT = 8791;
const MAX_PORT_ATTEMPTS = 50;
const HOST = '127.0.0.1';

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.webp': 'image/webp',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
  '.ico': 'image/x-icon',
};

async function readIfFile(root, urlPath) {
  const file = resolve(root, '.' + urlPath);
  if (file !== root && !file.startsWith(root + sep)) return null;
  try {
    if (!(await stat(file)).isFile()) return null;
    return { file, body: await readFile(file) };
  } catch {
    return null;
  }
}

async function loadRules(root) {
  try {
    return parseRedirects(await readFile(join(root, '_redirects'), 'utf8'));
  } catch {
    return [];
  }
}

/** Maps a request path to { status, file, body } or { status, location }. */
export async function resolveRequest(root, pathname, rules) {
  let urlPath;
  try {
    urlPath = decodeURIComponent(pathname);
  } catch {
    return { status: 400 };
  }
  const outcome = applyRedirects(urlPath, rules);
  if (outcome && outcome.status !== 200) return outcome;
  const target = outcome ? outcome.path : urlPath;
  if (target.endsWith('/')) {
    const index = await readIfFile(root, target + 'index.html');
    if (index) return { status: 200, ...index };
  } else {
    const exact = await readIfFile(root, target);
    if (exact) return { status: 200, ...exact };
    if (await readIfFile(root, target + '/index.html')) return { status: 301, location: target + '/' };
    const page = await readIfFile(root, target + '.html');
    if (page) return { status: 200, ...page };
  }
  const missing = await readIfFile(root, '/404.html');
  return { status: 404, ...(missing ?? { file: null, body: Buffer.from('Not found\n') }) };
}

export function createDevServer(root) {
  const siteRoot = resolve(root);
  return createServer(async (request, response) => {
    try {
      const { pathname } = new URL(request.url, `http://${HOST}`);
      const result = await resolveRequest(siteRoot, pathname, await loadRules(siteRoot));
      if (result.location) {
        response.writeHead(result.status, { Location: result.location });
        response.end();
        return;
      }
      if (result.status === 400) {
        response.writeHead(400).end('Bad request\n');
        return;
      }
      const type = result.file ? TYPES[extname(result.file)] ?? 'application/octet-stream' : TYPES['.txt'];
      response.writeHead(result.status, { 'Content-Type': type, 'Cache-Control': 'no-store' });
      response.end(result.body);
    } catch (error) {
      response.writeHead(500).end(`${error.message}\n`);
    }
  });
}

/** Listens on the first free port from `startPort`; resolves with the port actually bound. */
export function listenOnFreePort(server, startPort = START_PORT) {
  return new Promise((resolvePort, reject) => {
    let port = startPort;
    const attempt = () => {
      server.once('error', (error) => {
        if (error.code === 'EADDRINUSE' && port - startPort < MAX_PORT_ATTEMPTS) {
          port += 1;
          attempt();
        } else {
          reject(error);
        }
      });
      server.listen(port, HOST, () => resolvePort(port));
    };
    attempt();
  });
}

const here = dirname(fileURLToPath(import.meta.url));

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = join(here, 'dist');
  if (!(await stat(root).then((s) => s.isDirectory(), () => false))) {
    console.error('site/dist not found; run "node site/build.mjs" first');
    process.exit(1);
  }
  const port = await listenOnFreePort(createDevServer(root));
  console.log(`http://${HOST}:${port}/  (Ctrl+C to stop)`);
}
