// The site server's entry point, run by systemd as xrpl-file-site.service:
//
//   node /srv/xrpl-file/current/site/server/main.mjs
//
// Environment: PORT (8080), HOST (127.0.0.1), SITE_ORIGIN (https://xrpl-file.staticbit.io),
// CACHE_DIR (/var/cache/xrpl-file). SITE_LEDGER=off refuses every ledger read; the release test uses
// it, as tests have no network.
import { createHash } from 'node:crypto';
import { readFile, realpath } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createApp, createSiteServer, shutdown } from './app.mjs';
import { createDiskCache } from './cache.mjs';
import { createPictureReader } from './picture-reader.mjs';

const DRAIN_MS = 5000;

const log = (line) => console.error(line);
const env = process.env;
const port = Number(env.PORT ?? 8080);
const host = env.HOST ?? '127.0.0.1';
const origin = env.SITE_ORIGIN ?? 'https://xrpl-file.staticbit.io';

// Resolved once: a deploy that moves the `current` link does not change what this process serves.
const siteRoot = await realpath(fileURLToPath(new URL('../dist', import.meta.url)));
const releaseRoot = fileURLToPath(new URL('../..', import.meta.url));

async function releaseId() {
  try {
    const { commit } = JSON.parse(await readFile(join(releaseRoot, 'release.json'), 'utf8'));
    return typeof commit === 'string' ? commit : 'unknown';
  } catch {
    return 'unknown';
  }
}

try {
  // A card page embeds the built file page, so cached card pages belong to that file page's hash.
  const filePage = await readFile(join(siteRoot, 'file.html'));
  const cache = await createDiskCache({
    dir: env.CACHE_DIR ?? '/var/cache/xrpl-file',
    generation: createHash('sha256').update(filePage).digest('hex').slice(0, 16),
    isPageKey: (key) => key.includes('/__cards/') && key.includes('/page/'),
    log,
  });
  const ledgerOff = env.SITE_LEDGER === 'off';
  const pictureReader = createPictureReader({ sourceData: { off: ledgerOff } });
  const app = await createApp({
    siteRoot,
    origin,
    release: await releaseId(),
    cache,
    ...(ledgerOff ? { openLedger: () => { throw new Error('ledger reads are off (SITE_LEDGER=off)'); } } : {}),
    pictureReader,
    log,
  });
  const server = createSiteServer(app, { origin, log });
  server.listen(port, host, () => console.log(`xrpl-file site on http://${host}:${port} (${siteRoot})`));
  let stopping = false;
  const stop = async () => {
    if (stopping) return;
    stopping = true;
    await shutdown(server, app, DRAIN_MS);
    await pictureReader.close();
    process.exit(0);
  };
  process.on('SIGTERM', stop);
  process.on('SIGINT', stop);
} catch (error) {
  log(`xrpl-file site failed to start: ${error?.stack ?? error}`);
  process.exit(1);
}
