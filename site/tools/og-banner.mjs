// Draws the site's link-card banner, site/src/og-banner.png (1200 x 630), the picture Telegram, X and
// other link previews show for the home pages and, until file cards exist, for a file link:
//
//   node web/build.mjs && node site/build.mjs && node site/tools/og-banner.mjs
//
// The banner is the wordmark beside the hero's own point cloud: the real scene (js/cloud.mjs) reading the
// real hero file (HERO_LINK, mainnet) through the real reader, photographed once every block is verified.
// It carries no sentence, so one picture serves all six languages; the card's title and description are
// in the page's language.
//
// The page is served from the built site (site/dist, which the tool needs) and rendered by headless
// Chrome or Edge, driven over the DevTools protocol so the shot waits for the read instead of a guessed
// delay. The PNG is committed; CI only checks its size (og-banner.test.mjs), since the read and a
// browser's rendering are not byte-stable. CHROME overrides the browser path.
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { readFile, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { dirname, extname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { MARK_CELLS } from '../chrome-markup.mjs';

const here = dirname(fileURLToPath(import.meta.url));
export const BANNER_FILE = join(here, '..', 'src', 'og-banner.png');
export const BANNER_WIDTH = 1200;
export const BANNER_HEIGHT = 630;
const DIST = resolve(here, '..', 'dist');
const PAGE_NAME = '__og-banner.html';
// The canvas: its centre, where the scene puts the cubes, at SHOT_LEFT + SHOT_WIDTH / 2 = 880 px, clear
// of the wordmark's right edge (about 525 px).
const SHOT_WIDTH = 1240;
const SHOT_LEFT = 260;
// A mainnet read of the 123-chunk logo takes a couple of seconds; this only bounds a stuck node.
const READ_TIMEOUT_MS = 60_000;

function mark() {
  const rects = MARK_CELLS.map(([column, row, state]) => {
    const cls = state ? ` class="${state}"` : '';
    return `<rect${cls} x="${0.5 + column * 6.5}" y="${0.5 + row * 6.5}" width="5" height="5" rx="0.5"/>`;
  }).join('');
  return `<svg class="mark" width="72" height="72" viewBox="0 0 19 19" aria-hidden="true">${rects}</svg>`;
}

export function bannerHtml() {
  return `<!doctype html>
<html lang="en" data-theme="dark"><head><meta charset="utf-8">
<link rel="stylesheet" href="/css/site.css">
<style>
html, body { margin: 0; width: ${BANNER_WIDTH}px; height: ${BANNER_HEIGHT}px; overflow: hidden; background: var(--paper); }
body { display: flex; flex-direction: row; align-items: center; justify-content: flex-start; gap: 0;
  box-sizing: border-box; padding: 0 0 0 88px; min-height: 0; }
.word { flex: none; display: flex; align-items: center; gap: 26px; color: var(--ink); font-size: 96px;
  font-weight: var(--weight-label); font-variation-settings: "SHRP" 100; letter-spacing: -0.02em; line-height: 1; }
.mark rect { fill: none; stroke: var(--ink-muted); stroke-width: 1; }
.mark .is-lit { fill: var(--ink-2); stroke: var(--ink-2); }
.mark .is-verified { fill: var(--cell-verified); stroke: var(--cell-verified); }
.word { position: relative; z-index: 1; }
/* The scene frames the file's cubes at the canvas centre, so a canvas wider than the banner and cut off
   on the right puts them right of the wordmark. The ground runs full bleed and fades under the text. */
.shot { position: absolute; inset: 0; overflow: hidden; }
.shot .cloud { position: absolute; left: ${SHOT_LEFT}px; top: 0; width: ${SHOT_WIDTH}px; height: ${BANNER_HEIGHT}px; aspect-ratio: auto; }
.shot::after { content: ""; position: absolute; inset: 0;
  background: linear-gradient(to right, var(--paper) 0, var(--paper) 30%, transparent 58%); }
</style></head>
<body>
<div class="word">${mark()}<span>xrpl-file</span></div>
<div class="shot"><canvas id="cloud" class="cloud"></canvas></div>
<script type="module">
import { mountCloud } from '/js/cloud.mjs';
import { startHeroRead } from '/js/hero-read.mjs';
import { HERO_ASPECT, HERO_LINK } from '/js/config.mjs';
const cloud = mountCloud(document.getElementById('cloud'),
  { link: HERO_LINK, aspect: HERO_ASPECT, reducedMotion: true, turn: false },
  { startRead: startHeroRead, loadElement: () => {} });
cloud.done.then(() => { window.__banner = { phase: cloud.phase, settle: () => cloud.settleDelay() }; });
</script>
</body></html>
`;
}

const TYPES = { '.html': 'text/html', '.css': 'text/css', '.mjs': 'text/javascript', '.js': 'text/javascript',
  '.json': 'application/json', '.woff2': 'font/woff2', '.png': 'image/png', '.svg': 'image/svg+xml' };

function serveDist() {
  const server = createServer(async (request, response) => {
    const path = decodeURIComponent(new URL(request.url, 'http://x').pathname);
    const file = resolve(DIST, '.' + path);
    if (!file.startsWith(DIST + sep)) return response.writeHead(403).end();
    try {
      const body = await readFile(file);
      response.writeHead(200, { 'content-type': TYPES[extname(file)] ?? 'application/octet-stream' }).end(body);
    } catch {
      response.writeHead(404).end();
    }
  });
  return new Promise((done) => server.listen(0, '127.0.0.1', () => done(server)));
}

function findBrowser() {
  const candidates = [
    process.env.CHROME,
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    '/usr/bin/google-chrome',
    '/usr/bin/chromium',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  ].filter(Boolean);
  const found = candidates.find((path) => existsSync(path));
  if (!found) throw new Error('no Chrome or Edge found; set CHROME to the browser executable');
  return found;
}

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

/** The DevTools WebSocket of the browser's first page, once the browser listens. */
async function pageSocketUrl(port) {
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      const page = targets.find((target) => target.type === 'page');
      if (page) return page.webSocketDebuggerUrl;
    } catch {
      // The browser is still starting.
    }
    await sleep(100);
  }
  throw new Error('the browser did not open its DevTools port');
}

function devtools(url) {
  const socket = new WebSocket(url);
  let nextId = 1;
  const pending = new Map();
  socket.addEventListener('message', (event) => {
    const message = JSON.parse(event.data);
    const waiter = pending.get(message.id);
    if (!waiter) return;
    pending.delete(message.id);
    if (message.error) waiter.reject(new Error(message.error.message));
    else waiter.resolve(message.result);
  });
  const opened = new Promise((done, fail) => {
    socket.addEventListener('open', done, { once: true });
    socket.addEventListener('error', () => fail(new Error('DevTools socket failed')), { once: true });
  });
  return {
    opened,
    send: (method, params = {}) => new Promise((resolve_, reject) => {
      const id = nextId++;
      pending.set(id, { resolve: resolve_, reject });
      socket.send(JSON.stringify({ id, method, params }));
    }),
    close: () => socket.close(),
  };
}

async function evaluate(cdp, expression) {
  const { result } = await cdp.send('Runtime.evaluate', { expression, returnByValue: true });
  return result.value;
}

export async function drawBanner() {
  if (!existsSync(join(DIST, 'js', 'cloud.mjs'))) throw new Error('site/dist is not built: run node web/build.mjs && node site/build.mjs');
  const pageFile = join(DIST, PAGE_NAME);
  await writeFile(pageFile, bannerHtml());
  const server = await serveDist();
  const profile = mkdtempSync(join(tmpdir(), 'og-banner-'));
  const port = 9300 + Math.floor(Math.random() * 500);
  const browser = spawn(findBrowser(), [
    '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
    `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, `--window-size=${BANNER_WIDTH},${BANNER_HEIGHT}`,
    'about:blank',
  ], { stdio: 'ignore' });
  try {
    const cdp = devtools(await pageSocketUrl(port));
    await cdp.opened;
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: BANNER_WIDTH, height: BANNER_HEIGHT, deviceScaleFactor: 1, mobile: false });
    await cdp.send('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/${PAGE_NAME}` });
    const started = Date.now();
    let phase = null;
    while (Date.now() - started < READ_TIMEOUT_MS) {
      phase = await evaluate(cdp, 'window.__banner ? window.__banner.phase : null');
      if (phase !== null && (await evaluate(cdp, 'window.__banner.settle()')) === 0) break;
      await sleep(250);
    }
    if (phase !== 'done') throw new Error(`the hero read ended as ${phase ?? 'nothing within the timeout'}, not done`);
    // One more frame after the last change, so the canvas holds the settled scene.
    await sleep(500);
    const { data } = await cdp.send('Page.captureScreenshot', { format: 'png' });
    await writeFile(BANNER_FILE, Buffer.from(data, 'base64'));
    cdp.close();
  } finally {
    browser.kill();
    server.close();
    await rm(pageFile, { force: true });
    // The browser may still hold its profile for a moment after the kill.
    await sleep(500);
    rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  await drawBanner();
  console.log(`wrote ${BANNER_FILE}`);
}
