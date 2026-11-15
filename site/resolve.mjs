// Maps a request path to a file of the built site the way Cloudflare Pages does: the first matching
// _redirects rule (a 200 rule rewrites), pretty URLs, a folder's index.html, the 404 page. Shared by
// the dev server (site/serve.mjs) and the production server (site/server/), which differ only in the
// options they pass.
import { readFile as fsReadFile, stat as fsStat } from 'node:fs/promises';
import { resolve, sep } from 'node:path';
import { applyRedirects } from './redirects.mjs';

/** Files Pages reads as configuration and never publishes. */
export const HOST_FILES = ['/_redirects', '/_headers', '/_routes.json'];

// Only these mean "no such file"; anything else (permissions, I/O) is a fault the visitor must not
// see as a quiet 404.
const MISSING = new Set(['ENOENT', 'ENOTDIR']);

async function readIfFile(root, urlPath, fs) {
  const file = resolve(root, '.' + urlPath);
  if (file !== root && !file.startsWith(root + sep)) return null;
  try {
    if (!(await fs.stat(file)).isFile()) return null;
    return { file, body: await fs.readFile(file) };
  } catch (error) {
    if (MISSING.has(error?.code)) return null;
    throw error;
  }
}

function prettyUrl(htmlPath) {
  return htmlPath.endsWith('/index.html') ? htmlPath.slice(0, -'index.html'.length) : htmlPath.slice(0, -'.html'.length);
}

async function notFound(root, fs) {
  const missing = await readIfFile(root, '/404.html', fs);
  return { status: 404, ...(missing ?? { file: null, body: Buffer.from('Not found\n') }) };
}

/**
 * Maps a request path to { status, file, body }, { status, location }, { status: 400 } or
 * { status: 500, error }.
 * @param {{ search?: string, htmlStatus?: 307 | 308, hidden?: string[], stat?: Function, readFile?: Function }} [options]
 *   search is appended to redirect locations; htmlStatus is the status of the .html → pretty URL
 *   redirect; hidden paths answer 404.
 */
export async function resolveRequest(root, pathname, rules, options = {}) {
  const { search = '', htmlStatus = 307, hidden = [] } = options;
  const fs = { stat: options.stat ?? fsStat, readFile: options.readFile ?? fsReadFile };
  let urlPath;
  try {
    urlPath = decodeURIComponent(pathname);
  } catch {
    return { status: 400 };
  }
  try {
    if (hidden.includes(urlPath)) return await notFound(root, fs);
    const outcome = applyRedirects(urlPath, rules);
    if (outcome && outcome.status !== 200) return outcome;
    const target = outcome ? outcome.path : urlPath;
    if (hidden.includes(target)) return await notFound(root, fs);
    if (target.endsWith('/')) {
      const index = await readIfFile(root, target + 'index.html', fs);
      if (index) return { status: 200, ...index };
    } else {
      const exact = await readIfFile(root, target, fs);
      // Pages' HTML handling also applies to a rewritten target: /x.html becomes /x and /x/index.html
      // becomes /x/ by redirect, so a 200 rule must name the extension-less path.
      if (exact && target.endsWith('.html')) return { status: htmlStatus, location: prettyUrl(target) + search };
      if (exact) return { status: 200, ...exact };
      if (await readIfFile(root, target + '/index.html', fs)) return { status: 301, location: target + '/' + search };
      const page = await readIfFile(root, target + '.html', fs);
      if (page) return { status: 200, ...page };
    }
    return await notFound(root, fs);
  } catch (error) {
    return { status: 500, error };
  }
}
