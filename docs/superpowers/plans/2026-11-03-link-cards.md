# File Link Cards Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A link-preview robot that fetches `https://xrpl-file.staticbit.io/<network>/<hash>` gets the file's name, type, size and (for a picture or a file with a cover) a verified picture, while people get the static file page exactly as today.

**Architecture:** A platform-neutral core in `site/cards/` (robot detection, card reading through the existing reader, tag rendering, the request handler with injected cache/assets/ledger) and a thin Cloudflare Pages Function in `site/functions/[[path]].js`. `site/src/_routes.json` limits the function to `/mainnet/*`, `/testnet/*`, `/devnet/*` and `/og/*`; everything else stays static and free. Results go to Cloudflare's Cache API, successes for a year.

**Tech Stack:** Node 24 ES modules, `node:test`, the reader in `web/src/` (`XrplFileReader`, `LedgerClient`, `ENDPOINTS`, `parseLink`), Cloudflare Pages Functions via wrangler `4.151.1` (bundled by wrangler, no npm dependencies added).

**Spec:** `docs/superpowers/specs/2026-11-03-link-cards-design.md`

## Global Constraints

- People are never held up: for a non-robot request the handler makes no ledger call and no cache call before handing the request to the static assets.
- Every byte that reaches a card picture is verified by `XrplFileReader` (hash) and by its type's magic bytes.
- Any failure gives the generic card (robot HTML) or the banner (picture), never a 5xx, never an empty card.
- File picture: manifest type `image/png`, `image/jpeg`, `image/webp` or `image/gif`, size at most `1_000_000` bytes. SVG is never a card picture.
- Cover: exactly what `XrplFileReader#readCover` returns (it already enforces `COVER_TYPES` = webp/jpeg, 64 KB and section 4.1). Do not widen the reader.
- Cache: success `public, max-age=31536000, immutable`; `ManifestNotFound` `public, max-age=3600`; any other failure `public, max-age=300`.
- Deadlines: robot HTML waits at most `4000` ms for manifest (+cover); `/og/` waits at most `5000` ms for the picture.
- `limits.cpu_ms = 5000` in `site/wrangler.toml`.
- Card copy is English, no em dash (`writing-site-copy` skill); the manifest name is untrusted: HTML-escaped and cut to 100 characters.
- No new npm dependency for `site/`; tests run with `node --test site/test/*.test.mjs` and `cd web && node --test test/*.test.mjs`.
- Wrangler stays pinned at `4.151.1` (as `deploy-site` already uses).

## Review Focus

1. A person's browser on `/mainnet/<hash>` while every node is down: the file page loads (200, the static HTML), no ledger call. Test in Task 4.
2. A robot on a malformed file path (`/mainnet/xyz`, `/mainnet/<hash>/extra`) or on `/og/` with junk: the request passes through to the static assets, no 5xx. Tests in Tasks 1 and 4.
3. A manifest name carrying `"><script>` or 300 characters: escaped and cut in every tag. Test in Task 3.
4. A robot while the node is unreachable: 200 with the generic card, cached for 300 s, and a later request retries the ledger. Test in Task 4.
5. A hash-verified file whose manifest says `image/png` but whose bytes are not a PNG: `/og/` answers the banner. Test in Task 2.

---

## File Structure

| File | Responsibility |
|---|---|
| `site/cards/rules.mjs` (create) | robot user agents, card paths, picture types, ceilings, magic bytes |
| `site/cards/card.mjs` (create) | reading a card (manifest, cover) and a picture through `XrplFileReader`; the card's words |
| `site/cards/html.mjs` (create) | putting a file card into the static file page's `<head>` |
| `site/cards/handler.mjs` (create) | the two request handlers with injected cache, assets, ledger and `waitUntil`; deadlines, cache lifetimes, in-flight reads |
| `site/functions/[[path]].js` (create) | the Cloudflare adapter: `caches.default`, `env.ASSETS`, `LedgerClient`, `context.waitUntil` |
| `site/src/_routes.json` (create) | which paths invoke the function |
| `site/wrangler.toml` (create) | project name, output dir, compatibility date, `limits.cpu_ms` |
| `site/chrome-markup.mjs` (modify) | `cardTags` gains the per-file options (picture URL, twitter card, no dims) |
| `site/build.mjs` (modify) | `_routes.json` is a host file |
| `.github/workflows/test.yml` (modify) | deploy from `site/` so wrangler finds `functions/` and `wrangler.toml` |
| `site/README.md`, `docs/superpowers/reports/2026-11-02-site/deploy-note.md` (modify) | how cards work, owner steps |

Tests: `site/test/cards-rules.test.mjs`, `site/test/cards-card.test.mjs`, `site/test/cards-html.test.mjs`, `site/test/cards-handler.test.mjs`, additions to `site/test/build.test.mjs`. Ledger fixtures come from `web/test/vectors.mjs` (`FakeSource`, `smallFileLedger`, `labelHash`, `WRITER`); a test that needs a manifest `name` re-encodes the manifest transaction the way `web/test/cover.test.mjs#decoratedReader` does.

---

### Task 1: Card rules

**Files:**
- Create: `site/cards/rules.mjs`
- Test: `site/test/cards-rules.test.mjs`

**Interfaces:**
- Produces:
  - `isLinkPreviewRobot(userAgent: string | null): boolean`
  - `parseCardPath(pathname: string): { kind: 'page' | 'picture', network: 'mainnet' | 'testnet' | 'devnet', hash: string } | null` (hash uppercased; `/<net>/<hash>` and `/<net>/<hash>/` are `page`, `/og/<net>/<hash>` is `picture`)
  - `FILE_PICTURE_TYPES: string[]`, `FILE_PICTURE_MAX_BYTES = 1_000_000`
  - `isFilePictureCandidate(manifest: { type: string, size: number }): boolean`
  - `pictureMagicAgrees(type: string, bytes: Uint8Array): boolean`

- [ ] **Step 1: Write the failing tests**

```js
test('Telegram, X and the common unfurlers are robots; browsers are not', () => {
  for (const ua of ['TelegramBot (like TwitterBot)', 'Twitterbot/1.0', 'facebookexternalhit/1.1',
    'Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)', 'Slackbot-LinkExpanding 1.0',
    'WhatsApp/2.23.20.0', 'LinkedInBot/1.0']) assert.equal(isLinkPreviewRobot(ua), true, ua);
  for (const ua of [null, '', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36',
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1'])
    assert.equal(isLinkPreviewRobot(ua), false, String(ua));
});

test('card paths', () => {
  const h = 'ab'.repeat(32);
  assert.deepEqual(parseCardPath(`/mainnet/${h}`), { kind: 'page', network: 'mainnet', hash: h.toUpperCase() });
  assert.deepEqual(parseCardPath(`/testnet/${h}/`), { kind: 'page', network: 'testnet', hash: h.toUpperCase() });
  assert.deepEqual(parseCardPath(`/og/devnet/${h}`), { kind: 'picture', network: 'devnet', hash: h.toUpperCase() });
  for (const p of ['/mainnet/xyz', `/mainnet/${h}/extra`, `/21338/${h}`, `/og/mainnet/${h}/`, '/og/', '/']) assert.equal(parseCardPath(p), null, p);
});

test('a file is a picture candidate only for the four raster types up to 1 MB', () => {
  assert.equal(isFilePictureCandidate({ type: 'image/png', size: 1_000_000 }), true);
  assert.equal(isFilePictureCandidate({ type: 'image/png', size: 1_000_001 }), false);
  assert.equal(isFilePictureCandidate({ type: 'image/svg+xml', size: 300 }), false);
  assert.equal(isFilePictureCandidate({ type: 'video/mp4', size: 300 }), false);
});

test('magic bytes: PNG, JPEG, WebP, GIF87a/89a; anything else is refused', () => { /* one agreeing and one
  disagreeing byte array per type, plus pictureMagicAgrees('image/svg+xml', ...) === false */ });
```

- [ ] **Step 2: Run, expect FAIL** (`node --test site/test/cards-rules.test.mjs`: module not found)
- [ ] **Step 3: Implement `site/cards/rules.mjs`.** Robots by case-insensitive substrings `telegrambot`, `twitterbot`, `facebookexternalhit`, `discordbot`, `slackbot`, `whatsapp`, `linkedinbot`. JPEG and WebP checks reuse `coverMagicAgrees` from `web/src/cover.mjs`; PNG is `89 50 4E 47 0D 0A 1A 0A`, GIF is `GIF87a` or `GIF89a`.
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Commit** `feat(site): rules for file link cards`

### Task 2: Reading a card and its picture

**Files:**
- Create: `site/cards/card.mjs`
- Test: `site/test/cards-card.test.mjs`

**Interfaces:**
- Consumes: Task 1; `XrplFileReader` (`manifest(link)`, `readCover(record)`, `read(link)`), `XrplFileError` codes, `formatBytes` from `web/src/element/format.mjs` (if importing it drags in DOM globals under Node, copy its decimal rule into `card.mjs` instead and say so in the report).
- Produces:
  - `type Card = { status: 'file', title: string, description: string, picture: 'file' | 'cover' | 'banner' } | { status: 'missing' } | { status: 'unavailable' }`
  - `readCard(source, { network, hash }): Promise<{ card: Card, cover: { bytes: Uint8Array, type: string } | null }>` (never throws; `ManifestNotFound` → `missing`, any other error → `unavailable`)
  - `readPicture(source, { network, hash }): Promise<{ bytes: Uint8Array, type: string } | null>` (file picture when a candidate and its magic agrees, else the cover, else null; never throws)
  - `describeFile({ type, size }): string`, `cardTitle(manifest, hash): string`

- [ ] **Step 1: Write the failing tests** (fixtures: `smallFileLedger` with a real 1×1 PNG, a WebP-headed and a JPEG-headed buffer as in `web/test/cover.test.mjs`; manifest name set by re-encoding):

```js
test('a named PNG: its name, type and size, and the file itself as the picture', async () => {
  const { card, cover } = await readCard(source, { network: 'testnet', hash });
  assert.deepEqual(card, { status: 'file', title: 'logo.png', description: 'PNG image, 1.5 KB, stored in the XRP Ledger.', picture: 'file' });
  assert.equal(cover, null);
});
test('a video with a WebP cover: the cover is read now and returned', async () => { /* picture 'cover', cover.type 'image/webp' */ });
test('a cover that fails section 4.1 gives the banner', async () => { /* cover by another writer -> picture 'banner', cover null */ });
test('a PDF without a cover gives the banner and a document description', async () => { /* 'PDF document, …' */ });
test('no name: the title is xrpl-file and the first eight hash characters', async () => { /* 'xrpl-file 1A2B3C4D' */ });
test('an unknown hash is missing; a source that throws is unavailable', async () => { /* both never reject */ });
test('readPicture: a PNG manifest over non-PNG bytes falls back to the cover, then to null', async () => {});
test('readPicture never reads a file picture over 1 MB', async () => { /* manifest size 1_000_001: FakeSource.accountTransactionsCalls stays 0 for the file's chunks */ });
```

- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement.** Description is `${typeLabel}, ${formatBytes(size)}, stored in the XRP Ledger.` with `typeLabel`: `image/*` → `<SUBTYPE> image` (`svg+xml` → `SVG`, `jpeg` → `JPEG`), `video/*` → `<SUBTYPE> video`, `audio/*` → `<SUBTYPE> audio`, `application/pdf` → `PDF document`, `text/*` → `Text file`, anything else → `File`. One `XrplFileReader` per read (its history is per read, see the class comment); `maxBytes: FILE_PICTURE_MAX_BYTES` for the file picture.
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Commit** `feat(site): read a file's card and picture from the ledger`

### Task 3: The card in the page

**Files:**
- Modify: `site/chrome-markup.mjs` (`cardTags`)
- Create: `site/cards/html.mjs`
- Test: `site/test/cards-html.test.mjs`; `site/test/build.test.mjs` keeps passing unchanged

**Interfaces:**
- Consumes: Task 2 `Card`.
- Produces:
  - `cardTags({ title, description, pagePath, langFromStorage, url = undefined, image = CARD_IMAGE_DEFAULT, twitterCard = 'summary_large_image' })`: `url` given writes `og:url` with it; `image` is `{ url, width?, height? }`, width and height written only when present. The static pages' output stays byte-identical.
  - `fileCardHtml(staticHtml: string, card: Card, { origin: string, network: string, hash: string }): string`

- [ ] **Step 1: Write the failing tests**

```js
test('a file card replaces the title, the description and every og/twitter tag of the static page', () => {
  const html = fileCardHtml(builtFilePage, { status: 'file', title: 'logo.png', description: 'PNG image, 1.5 KB, stored in the XRP Ledger.', picture: 'file' },
    { origin: 'https://xrpl-file.staticbit.io', network: 'mainnet', hash: H });
  assert.match(html, /<title>logo\.png<\/title>/);
  assert.ok(html.includes(`<meta property="og:url" content="https://xrpl-file.staticbit.io/mainnet/${H}">`));
  assert.ok(html.includes(`<meta property="og:image" content="https://xrpl-file.staticbit.io/og/mainnet/${H}">`));
  assert.ok(!html.includes('og:image:width'), 'the picture is not decoded, so its size is not claimed');
  assert.ok(html.includes('<meta name="twitter:card" content="summary_large_image">'));
  assert.equal(html.match(/property="og:title"/g).length, 1);
});
test('a banner card points og:image at /og-banner.png with its size and uses summary', () => {});
test('missing and unavailable cards return the static page unchanged', () => {});
test('a hostile name is escaped and cut to 100 characters everywhere', () => { /* '"><script>' + 'x'.repeat(300) */ });
```

`builtFilePage` is `site/dist/file.html` produced by `build()` into a temp dir, as `build.test.mjs` does.

- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement.** `fileCardHtml` removes every `<meta property="og:…">` / `<meta name="twitter:…">` line, replaces `<title>` and `<meta name="description">`, and inserts `cardTags(...)` right after the description.
- [ ] **Step 4: Run, expect PASS** (`node --test site/test/*.test.mjs`, all green)
- [ ] **Step 5: Commit** `feat(site): render a file's card into the file page`

### Task 4: The handlers

**Files:**
- Create: `site/cards/handler.mjs`
- Test: `site/test/cards-handler.test.mjs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces:
  - `type Deps = { assets: { fetch(request: Request): Promise<Response> }, passThrough(request: Request): Promise<Response>, cache: { match(key: string): Promise<Response | undefined>, put(key: string, response: Response): Promise<void> }, waitUntil(promise: Promise<unknown>): void, openSource(network: string): { source: object, close(): void }, origin: string, htmlDeadlineMs?: number, pictureDeadlineMs?: number }`
  - `handleCardRequest(request: Request, deps: Deps): Promise<Response>` (GET and HEAD; any other method and any `parseCardPath` null go straight to `deps.passThrough(request)`)
  - cache keys: `${origin}/__cards/page/<network>/<hash>` and `${origin}/__cards/picture/<network>/<hash>`

- [ ] **Step 1: Write the failing tests** (fake `Deps`; `openSource` counts calls; `assets.fetch` returns the built `file.html` for `/file`, `og-banner.png` bytes for `/og-banner.png`, 404 otherwise):

```js
test('a browser gets the static file page and the ledger is never asked', async () => {
  const deps = fakeDeps({ openSource: () => { throw new Error('node down'); } });
  const response = await handleCardRequest(new Request(`${ORIGIN}/mainnet/${H}`, { headers: { 'user-agent': CHROME } }), deps);
  assert.equal(response.status, 200);
  assert.equal(await response.text(), FILE_PAGE);
  assert.equal(deps.sourceOpened, 0);
  assert.equal(deps.cacheCalls, 0);
});
test('a robot gets the file card, cached for a year, and the picture read starts in the background', async () => {});
test('a robot while the node is unreachable gets the generic card, cached for 300 s; the next miss asks again', async () => {});
test('an unknown hash: generic card cached for 3600 s', async () => {});
test('a robot past the 4 s deadline gets the generic card', async () => { /* htmlDeadlineMs: 20, a source that never answers */ });
test('/og/ serves the cached picture with its type and nosniff', async () => {});
test('/og/ joins the read the page started instead of starting a second one', async () => { /* openSource called once */ });
test('/og/ past its deadline answers the banner (max-age 300) and the read still lands in the cache', async () => {});
test('/og/ for a PNG manifest over non-PNG bytes answers the banner', async () => {});
test('malformed paths and POST go to passThrough untouched', async () => {});
test('HEAD to a card returns headers and no body', async () => {});
```

- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement.** Human branch: `deps.assets.fetch(new Request(new URL('/file', request.url), request))` before anything else. Robot branch: cache match → else `readCard` raced against the deadline → `fileCardHtml` → cache put with the lifetime from the card status (deadline counts as `unavailable`); when `card.picture === 'file'`, `deps.waitUntil(loadPicture(...))`; when `'cover'`, put the cover bytes under the picture key at once. Pictures: a module-level `Map` of in-flight `loadPicture` promises keyed by `<network>/<hash>`; `loadPicture` opens one source, calls `readPicture`, caches bytes (year) or a "no picture" marker (300 s), closes the source in `finally`, deletes its map entry. `/og/` races `loadPicture` against its deadline; on timeout or no picture it answers `deps.assets.fetch('/og-banner.png')` re-wrapped with `Cache-Control: public, max-age=300` and leaves the read running via `deps.waitUntil`. Every picture response carries `X-Content-Type-Options: nosniff`.
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Commit** `feat(site): serve file cards and their pictures to link-preview robots`

### Task 5: Cloudflare adapter, routes and deploy

**Files:**
- Create: `site/functions/[[path]].js`, `site/src/_routes.json`, `site/wrangler.toml`
- Modify: `site/build.mjs` (`HOST_FILES` gains `_routes.json`), `.github/workflows/test.yml` (`deploy-site`)
- Test: `site/test/build.test.mjs` (additions)

**Interfaces:**
- Consumes: Task 4 `handleCardRequest`, `Deps`.
- Produces: the deployed function.

- [ ] **Step 1: Write the failing build tests**

```js
test('_routes.json reaches the output and invokes the function only on card paths', async () => {
  // build the real site; JSON.parse(dist/_routes.json) deepEquals
  // { version: 1, include: ['/mainnet/*', '/testnet/*', '/devnet/*', '/og/*'], exclude: [] }
});
test('the shadowing check ignores _routes.json', async () => { /* the real-site build does not throw */ });
```

- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement.**
  - `site/wrangler.toml`: `name = "xrpl-file-site"`, `pages_build_output_dir = "./dist"`, `compatibility_date = "2026-11-03"`, `[limits] cpu_ms = 5000`.
  - `site/functions/[[path]].js`: `export const onRequest = (context) => handleCardRequest(context.request, deps)` with `assets: context.env.ASSETS`, `cache` over `caches.default` (keys as `Request` objects), `waitUntil: (p) => context.waitUntil(p)`, `openSource(network)` = `new LedgerClient(ENDPOINTS[network])` with `close()`, `origin: new URL(context.request.url).origin`. `passThrough: () => context.next()`, so a path the handler does not take keeps the static rules.
  - `deploy-site`: the deploy step gets `working-directory: site` and runs `$WRANGLER pages deploy --branch main --commit-hash "$GITHUB_SHA"` (the output dir and name come from `wrangler.toml`; the "create project" step keeps `--project-name`).
- [ ] **Step 4: Run all tests** (`node --test site/test/*.test.mjs`, `cd web && node --test test/*.test.mjs`), expect PASS.
- [ ] **Step 5: Local check with wrangler.** From `site/` after `node web/build.mjs && node site/build.mjs`: `npx --yes wrangler@4.151.1 pages dev` and

```bash
curl -s -A "TelegramBot (like TwitterBot)" http://127.0.0.1:8788/mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5 | grep -E 'og:(title|image)'
curl -sI http://127.0.0.1:8788/og/mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5
curl -s -A "Mozilla/5.0" http://127.0.0.1:8788/mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5 | grep -c '<title>xrpl-file</title>'
```

Expected: the logo's name in `og:title` and `/og/mainnet/…` in `og:image`; `200` with `content-type: image/png` (the logo, 123 KB, under the 1 MB ceiling); `1`.
- [ ] **Step 6: Commit** `feat(site): Pages Function for file link cards`

### Task 6: Documentation

**Files:**
- Modify: `site/README.md` (a "File link cards" section after the link-cards paragraph), `docs/superpowers/reports/2026-11-02-site/deploy-note.md` (owner steps and checks)

- [ ] **Step 1: Write.** README: what the function does, which paths invoke it, the cache lifetimes, how to test locally (Task 5 Step 5), and that `site/wrangler.toml` is now the project's source of truth. Deploy note: owner steps (Workers Paid before the merge, Fail open in Pages → Settings → Runtime), checks after deploy (the three `curl` lines against the domain; a Telegram send of a picture link, a video-with-cover link and a link with neither; @WebpageBot to refresh).
- [ ] **Step 2: Check** `grep -c "—"` on both files prints `0`.
- [ ] **Step 3: Commit** `docs(site): file link cards`

---

Rulings recorded here (spec silent or impractical):

- `og:image:width` / `og:image:height` are written only for the banner: decoding a picture's header to learn its size is not worth the code, and omitting them is still honest.
- "Never a second read" holds within one isolate (an in-memory map); across Cloudflare's isolates a duplicate read is possible and is bounded by the cache.
