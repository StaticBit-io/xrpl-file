# The Site on Its Own Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Serve `https://xrpl-file.staticbit.io` from the owner's Ubuntu server (Node + Caddy) with Pages' behaviour and the link cards, deployed from GitHub Actions, while the Pages setup stays able to take the site back.

**Architecture:** One Node process (`site/server/main.mjs`) serves the built site with Pages semantics read from `_redirects`, `_headers` and `_routes.json`, and runs `site/cards/handler.mjs` through a Node adapter with a disk cache and read caps. Caddy in front does TLS and retries. Bash scripts prepare the server from the owner's PC and switch releases on it; a forced-command SSH key lets Actions do nothing but release and roll back.

**Tech Stack:** Node 24 ESM with no npm dependencies, `node:test`; Bash (Ubuntu 24.04 on the server, Git Bash on the owner's PC); systemd; Caddy 2 from its apt repository; ufw; GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-12-site-server-design.md`

## Global Constraints

- Node 24, ESM, no npm dependencies (as `web/` and `site/`). Tests use `node:test` and `node:assert/strict`.
- Repository artifacts (code, comments, commits, docs) are English. Commits are conventional and carry no tool or AI attribution.
- `site/cards/handler.mjs` keeps its behaviour on Pages; `site/functions/[[path]].js`, `site/wrangler.toml` and `site/src/_routes.json` stay, and CI keeps bundling the function.
- The server is addressed by IP `94.183.189.150`; the domain is `xrpl-file.staticbit.io` (`SITE_ORIGIN` default `https://xrpl-file.staticbit.io`).
- Paths on the server: `/srv/xrpl-file/{releases,current,last-good,.lock}`, `/var/cache/xrpl-file`, `/usr/local/bin/xrpl-file-release`, `/usr/local/bin/xrpl-file-smoke`, unit `xrpl-file-site.service`, users `xrpl-file` and `deploy`.
- Ports: Node 127.0.0.1:8080 (service), 127.0.0.1:8081 (candidate check). Caddy `lb_try_duration 15s`.
- Limits: 4 concurrent card reads, 32 requests inside the card handler, cache 256 MiB, drain 5 s, `TimeoutStopSec=10`, release archive 200 MB and 10,000 entries, smoke check 5 s per request and 30 s after restart, keep 5 releases plus last good and the one before it.
- No secret in the repository, a log or the chat: `provision.sh` prints the `gh` commands, the owner runs them.
- Nothing is pushed, merged or run against the server without the owner's go-ahead in chat (Task 10 says where).

## Review Focus

1. **A cache directory the service cannot write (wrong owner after a manual fix).** A robot still gets a card read from the ledger, not the generic card for ever: `match` resolves `undefined`, `put` is skipped — test in Task 3.
2. **A request with a forged `Host` header.** Cached cards and `og:image` keep the configured origin — test in Task 4.
3. **A path that is both an included route and a static file (`/og-banner.png` is not, `/og/x` is).** `/og/<not a hash>` answers the static 404, `/og/mainnet/<hash>` the handler — test in Task 4.
4. **A deploy whose SSH connection drops between the link switch and the check.** The next run (deploy or rollback) first restores the last good release — test in Task 6.
5. **A release archive with a symlink (`site/dist/leak -> /etc/passwd`).** Refused before anything is unpacked — test in Task 6.

---

### Task 1: Static resolution shared by both servers, with Pages' production differences

**Files:**
- Create: `site/resolve.mjs`
- Modify: `site/serve.mjs` (import `resolveRequest` from `./resolve.mjs`, behaviour unchanged)
- Test: `site/test/resolve.test.mjs`; `site/test/serve.test.mjs` must stay green unchanged

**Interfaces:**
- Produces: `resolveRequest(root: string, pathname: string, rules: Rule[], options?: { search?: string, htmlStatus?: 307 | 308, hidden?: string[], readFile?: (path) => Promise<Buffer>, stat?: (path) => Promise<Stats> }) → Promise<{ status: 200|404, file: string|null, body: Buffer } | { status: 301|307|308, location: string } | { status: 400 } | { status: 500, error: Error }>`. Defaults: `search: ''`, `htmlStatus: 307`, `hidden: []`, `node:fs/promises`.
- Produces: `HOST_FILES = ['/_redirects', '/_headers', '/_routes.json']` exported from `site/resolve.mjs`.

- [ ] **Step 1: Write the failing tests** in `site/test/resolve.test.mjs`, on a temporary root built like `siteRoot()` in `serve.test.mjs`:
  - `'an HTML redirect keeps the query string and uses the status it is given'`: `resolveRequest(root, '/index.html', rules, { search: '?x=1', htmlStatus: 308 })` deep-equals `{ status: 308, location: '/?x=1' }`; `'/file.html'` with `search: ''` gives `{ status: 308, location: '/file' }`.
  - `'host files are hidden when asked'`: with `hidden: HOST_FILES`, `'/_redirects'` resolves to status 404 with the 404 page's body; without `hidden` it resolves 200 (the dev server's behaviour is unchanged).
  - `'a missing file is a miss and any other read error is a 500'`: a `stat` stub throwing `Object.assign(new Error('x'), { code: 'EIO' })` gives `{ status: 500 }` with `error.code === 'EIO'`; one throwing `ENOTDIR` falls through to 404.

- [ ] **Step 2: Run** `node --test site/test/resolve.test.mjs` — Expected: FAIL, `site/resolve.mjs` does not exist.

- [ ] **Step 3: Implement `site/resolve.mjs`** by moving `readIfFile`, `prettyUrl` and `resolveRequest` out of `site/serve.mjs` and adding the options. `readIfFile` returns `null` only for `ENOENT`/`ENOTDIR` and rethrows the rest; `resolveRequest` turns a rethrown error into `{ status: 500, error }`. `serve.mjs` keeps exporting `resolveRequest` (re-export) so `serve.test.mjs` passes untouched, and answers a 500 result with `500` as today.

- [ ] **Step 4: Run** `node --test site/test/resolve.test.mjs site/test/serve.test.mjs` — Expected: PASS.

- [ ] **Step 5: Commit** `refactor(site): one resolver for the dev and production servers`.

### Task 2: `_headers` and static responses with Pages' headers

**Files:**
- Create: `site/headers.mjs`, `site/server/static.mjs`
- Test: `site/test/headers.test.mjs`, `site/test/server-static.test.mjs`

**Interfaces:**
- Consumes: `resolveRequest`, `HOST_FILES` (Task 1); `parseRedirects` (`site/redirects.mjs`).
- Produces: `parseHeaders(text: string) → HeaderRule[]`; `headersFor(pathname: string, rules: HeaderRule[]) → Array<[name, value]>` (all matching blocks, in file order).
- Produces: `createStaticSite({ root: string, redirects: Rule[], headers: HeaderRule[], release: string, log?: (msg) => void }) → { respond(request: Request): Promise<Response> }`. `root` is the real path of `site/dist`.

- [ ] **Step 1: Write the failing tests.**
  - `headers.test.mjs`, `'every matching block adds its headers'`: parsed from `site/src/_headers`, `headersFor('/element/1.2.0/x.js', rules)` contains `['X-Content-Type-Options','nosniff']`, `['Access-Control-Allow-Origin','*']` and `['Cache-Control','public, max-age=31536000, immutable']`; `headersFor('/index.html', rules)` contains `['Cache-Control','no-cache']`; `headersFor('/file', rules)` has no `Cache-Control`.
  - `server-static.test.mjs`, `'responses match the live Pages headers of 2026-11-12'`: against the real built site (`builtSite()` from `site/test/support/built-site.mjs`), each row below gives the status, and the listed headers exactly (`Access-Control-Allow-Origin: *` and `X-Content-Type-Options: nosniff` on every row):

    | Path | Status | Cache-Control | Location |
    |---|---|---|---|
    | `/` | 200 | `no-cache` | |
    | `/ru/` | 200 | `no-cache` | |
    | `/file` | 200 | `public, max-age=0, must-revalidate` | |
    | `/js/route.mjs` | 200 | `public, max-age=0, must-revalidate` | |
    | `/element/<ELEMENT_VERSION>/xrpl-file-element.js` | 200 | `public, max-age=31536000, immutable` | |
    | `/favicon.svg` | 200 | `public, max-age=604800` | |
    | `/og-banner.png` | 200 | `public, max-age=604800` | |
    | `/nope` | 404 | `no-store` | |
    | `/mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5` | 200 | `public, max-age=0, must-revalidate` | |
    | `/mainnet/9B1A…0DF5/` | 200 | `public, max-age=0, must-revalidate` | |
    | `/index.html` | 308 | `no-cache` | `/` |
    | `/ru/index.html` | 308 | `no-cache` | `/ru/` |
    | `/file.html` | 308 | `no-cache` | `/file` |
    | `/_redirects` | 404 | `no-store` | |

  - `'every response names the release'`: `X-Xrpl-File-Release` equals the `release` passed in, on a 200, a 308 and a 404.
  - `'an ETag revalidates to 304'`: a `GET /file` returns an `ETag`; the same request with `If-None-Match: <etag>` returns 304 with no body; with `If-None-Match: W/<etag>` and with `*` also 304; a `POST /file` is 405 with `Allow: GET, HEAD`.
  - `'HEAD has the headers and no body'`.
  - `'a read error is a 500 and a log line, not a 404'`: a site whose `stat` throws `EIO` (inject through a `resolve` option passed to `createStaticSite`) answers 500 and calls `log` once.

- [ ] **Step 2: Run** `node --test site/test/headers.test.mjs site/test/server-static.test.mjs` — Expected: FAIL, modules missing.

- [ ] **Step 3: Implement** `parseHeaders`/`headersFor` (patterns compiled like `redirects.mjs`'s sources: `*` → `.*`, `:name` → `[^/]+`) and `createStaticSite`. Order of headers: defaults (`Access-Control-Allow-Origin: *`, then `Cache-Control: public, max-age=0, must-revalidate`, or `no-store` on a 404), then `headersFor(requestedPathname)`, each later value replacing an earlier one of the same name; then `X-Xrpl-File-Release`. Content types come from the `TYPES` map now in `serve.mjs` (move it to `site/resolve.mjs` and export it). ETag: `"<first 32 hex of sha256(body)>"`, memoised per file path for the process. `resolveRequest` is called with `{ search, htmlStatus: 308, hidden: HOST_FILES }`.

- [ ] **Step 4: Run** the two test files — Expected: PASS. Then `node --test site/test/*.test.mjs` — Expected: all pass.

- [ ] **Step 5: Commit** `feat(site): static responses with the headers Pages sends`.

### Task 3: The disk cache

**Files:**
- Create: `site/server/cache.mjs`
- Test: `site/test/server-cache.test.mjs`

**Interfaces:**
- Produces: `createDiskCache({ dir: string, maxBytes = 256 * 1024 * 1024, generation: string, isPageKey: (key) => boolean, now = Date.now, log = () => {} }) → Promise<{ match(key: string): Promise<Response|undefined>, put(key: string, response: Response): Promise<void> }>`. Page keys are stored under `<dir>/pages/<generation>/`, others under `<dir>/other/`. One file per entry: a first line of JSON `{ "status", "headers", "storedAt", "maxAge" }`, then the body.

- [ ] **Step 1: Write the failing tests** (temporary directory, `now` controlled):
  - `'an entry comes back with its status, headers and body until its max-age'`: `put('k', new Response('abc', { headers: { 'Cache-Control': 'public, max-age=300', 'X-A': '1' } }))`; at `now + 299 s` `match('k')` gives body `'abc'` and `X-A: 1`; at `now + 301 s` it gives `undefined`.
  - `'a cache that cannot be read or written is a miss, never an error'`: with `dir` pointing at a regular file, `match` resolves `undefined`, `put` resolves, and `log` was called.
  - `'a damaged entry is a miss and is deleted'`: overwrite an entry's file with `'{not json'`; `match` gives `undefined` and the file is gone.
  - `'page entries belong to their generation'`: `put` a page key in generation `a`; a cache opened with generation `b` on the same dir misses it and has removed the `pages/a` directory; a non-page key put under `a` is still a hit under `b`.
  - `'past the cap the least recently used entries go'`: `maxBytes` 3 KB, three 1 KB entries, `match` the first, `put` a fourth; the second is gone, the first and fourth stay.
  - `'a write is whole or absent'`: two concurrent `put`s of the same key with different bodies; `match` returns one of the two bodies exactly, and no `*.tmp` file remains.

- [ ] **Step 2: Run** `node --test site/test/server-cache.test.mjs` — Expected: FAIL, module missing.

- [ ] **Step 3: Implement** with `node:fs/promises`: write to `<file>.<random>.tmp`, then `rename`. An in-memory index (key file → size, last use) is built by scanning the directory at creation and updated on `match`/`put`; eviction runs after a `put` that crosses `maxBytes`, counting temporary files. `maxAge` comes from `Cache-Control` (`max-age=N`); none means do not store.

- [ ] **Step 4: Run** the test file — Expected: PASS.

- [ ] **Step 5: Commit** `feat(site): a disk cache for the card handler`.

### Task 4: The application: routes, the card adapter, limits, Node HTTP, shutdown

**Files:**
- Create: `site/server/routes.mjs`, `site/server/limits.mjs`, `site/server/node-http.mjs`, `site/server/app.mjs`
- Test: `site/test/server-app.test.mjs`

**Interfaces:**
- Consumes: `createStaticSite` (Task 2), `createDiskCache` (Task 3), `handleCardRequest` (`site/cards/handler.mjs`), `LedgerClient`, `ENDPOINTS` (`web/src/ledger.mjs`).
- Produces: `parseRoutes(text) → { include: RegExp[], exclude: RegExp[] }` (throws on a file without `include`); `routedToFunction(pathname, routes) → boolean` (exclude wins; `*` matches any characters including `/`).
- Produces: `createLimiter(max: number) → { tryAcquire(): (() => void) | null, get active(): number }` — the release function is idempotent.
- Produces: `toRequest(req: IncomingMessage, origin: string) → Request` (URL built on `origin`, never on `req.headers.host`); `writeResponse(res: ServerResponse, response: Response, method: string) → Promise<void>`.
- Produces: `createApp({ siteRoot: string, origin: string, release: string, cache, openLedger = (network) => new LedgerClient(ENDPOINTS[network]), maxReads = 4, maxCardRequests = 32, log }) → { handle(request: Request): Promise<Response>, waitUntil(p), settle(ms: number): Promise<void> }`.
- Produces: `createSiteServer(app, { port, host = '127.0.0.1', origin }) → http.Server` and `shutdown(server, app, ms = 5000) → Promise<void>` in `app.mjs`.

- [ ] **Step 1: Write the failing tests**, reusing `FakeSource` and `smallFileLedger` from `web/test/vectors.mjs` and `builtSite()` as `site/test/cards-handler.test.mjs` does, with `openLedger` returning a `FakeSource` wrapped to count opens and closes:
  - `'a robot gets the card, a person the static page'`: `GET /mainnet/<png hash>` with `User-Agent: TelegramBot` contains `og:title` with the file name; with a Chrome user agent the body equals the built `file.html`.
  - `'routes follow _routes.json, exclude first'`: `routedToFunction('/og/mainnet/x', routes)` true; `'/js/route.mjs'` false; with `exclude: ['/og/*']` added, `'/og/mainnet/x'` false. Through `handle`, `GET /og/nothing` answers the static 404.
  - `'a forged Host never reaches a card'`: `toRequest` of a message with `host: evil.example` and `url: '/mainnet/<hash>'` gives a `Request` whose URL starts with the configured origin; the cached card's `og:image` names the configured origin.
  - `'past four reads a robot gets the generic card at once'`: with four gated sources holding their reads, a fifth robot request answers the built `file.html` within 100 ms and `openLedger` was called four times; releasing the gates closes all four (`closes === 4`).
  - `'past 32 requests in the handler the static response answers'`: `maxCardRequests: 1`, one gated request in flight, a second robot request answers the static page without calling `openLedger`.
  - `'a cache that rejects is a miss, not the generic card'` (Review Focus 1): with `createDiskCache` on an unwritable `dir`, a robot request still contains the file's `og:title`.
  - `'shutdown finishes started responses and waits for background work, at most the limit'`: start a server on port 0; a robot request whose picture read is gated; call `shutdown(server, app, 200)`; the HTML response completes, `shutdown` resolves within 400 ms though the read is still gated.
  - `'HEAD writes no body'` through `writeResponse`.

- [ ] **Step 2: Run** `node --test site/test/server-app.test.mjs` — Expected: FAIL, modules missing.

- [ ] **Step 3: Implement.** `createApp` builds the handler `deps` like `site/functions/[[path]].js`: `assets`/`passThrough` → the static site; `cache`; `waitUntil` adds to a `Set` and removes on settle; `openSource(network)` takes a limiter slot or throws `new Error('card reads at capacity')`, opens `openLedger(network)` and returns `{ source, close }` where `close` releases the slot once; `origin`. `handle` sends a request to the handler only when `routedToFunction` and a card-request slot is free, and wraps it so any exception answers `site.respond(request)`. `createDiskCache` is called by `main.mjs` (Task 5) with `generation` = first 16 hex of sha256 of `file.html` and `isPageKey = (key) => key.includes('/__cards/') && key.includes('/page/')`. `shutdown`: `server.close()`, then `Promise.race([Promise.all([closed, app.settle()]), delay(ms)])`, then `server.closeAllConnections()`.

- [ ] **Step 4: Run** the test file, then `node --test site/test/*.test.mjs` — Expected: all pass.

- [ ] **Step 5: Commit** `feat(site): the server application with the card handler`.

### Task 5: Entry point, release packing, and a release that starts on its own

**Files:**
- Create: `site/server/main.mjs`, `site/server/release-files.mjs`, `site/server/pack.mjs`
- Test: `site/test/server-release.test.mjs`

**Interfaces:**
- Consumes: Tasks 2–4.
- Produces: `main.mjs` reads `PORT` (default 8080), `HOST` (`127.0.0.1`), `SITE_ORIGIN` (`https://xrpl-file.staticbit.io`), `CACHE_DIR` (`/var/cache/xrpl-file`); reads the release id from `release.json` at the release root (`{ "commit": "<40 hex>" }`, `unknown` when absent); resolves `realpath` of `../dist` once; parses the three host files and exits 1 with a message if any fails; handles `SIGTERM`/`SIGINT` with `shutdown(…, 5000)`.
- Produces: `RELEASE_PATHS: string[]` (repository-relative paths that make a release); `node site/server/pack.mjs <out.tar.gz> <commit>` writes `release.json` into a staging copy and runs system `tar -czf` over the staging directory.

- [ ] **Step 1: Write the failing test** `'an unpacked release starts and serves the site'`: build the site (`builtSite()`'s `outDir` copied to `site/dist` in a temporary checkout copy, or run `node web/build.mjs && node site/build.mjs` once in `before`), run `pack.mjs` to a temp archive with commit `'a'.repeat(40)`, extract it with `tar -xzf` into an empty directory, spawn `node site/server/main.mjs` there with `PORT=0`-free port chosen by the test and `CACHE_DIR` in temp; `GET /` answers 200 with `X-Xrpl-File-Release: aaaa…`; `GET /mainnet/<hash>` with a robot UA answers 200 HTML (generic card allowed: no network in tests — pass `SITE_LEDGER=off` which makes `openLedger` throw); then `SIGTERM` makes it exit 0 within 6 s. Also: every path in `RELEASE_PATHS` exists in the repository.

- [ ] **Step 2: Run** `node --test site/test/server-release.test.mjs` — Expected: FAIL.

- [ ] **Step 3: Implement.** `RELEASE_PATHS`: `site/dist`, `site/server`, `site/cards`, `site/resolve.mjs`, `site/headers.mjs`, `site/redirects.mjs`, `site/chrome-markup.mjs`, `site/snippet.mjs` if `chrome-markup.mjs` imports it, `site/src/js/config.mjs`, `site/src/_routes.json`, `web/src` — the executor confirms the list by following imports from `main.mjs` and records any addition in the ledger. `SITE_LEDGER=off` is the one test-only switch, read in `main.mjs` only.

- [ ] **Step 4: Run** the test — Expected: PASS. Then the whole site suite.

- [ ] **Step 5: Measure the event-loop delay locally** with `site/server/bench.mjs` (new): starts the app on the built site with a `FakeSource` holding a 1 MB PNG file, runs four concurrent `/og/` reads with an empty cache while issuing `GET /file` every 10 ms, and prints the maximum added latency of `/file`. Record the number in the ledger; the decision is made on the server in Task 10.

- [ ] **Step 6: Commit** `feat(site): the server entry point and release packing`.

### Task 6: The release and smoke scripts

**Files:**
- Create: `deploy/server/xrpl-file-release`, `deploy/server/xrpl-file-smoke` (Bash, executable bit set in git)
- Test: `deploy/server/test/release.test.mjs` (skipped when `process.platform === 'win32'`; run locally in `docker run --rm -v <repo>:/w -w /w node:24 node --test deploy/server/test/`)

**Interfaces:**
- `xrpl-file-release` reads `SSH_ORIGINAL_COMMAND`: `release <40 hex>` or `rollback`; anything else prints `refused: <request>` to stderr and exits 2. Root directory `${XRPL_FILE_ROOT:-/srv/xrpl-file}` (SSH does not pass this variable: Ubuntu's `AcceptEnv` admits only `LANG LC_*`). Calls `flock`, `tar`, `node`, `sudo -n systemctl restart xrpl-file-site`, `xrpl-file-smoke` through `PATH`.
- `xrpl-file-smoke <port> <commit>` exits 0 when all checks of the spec's "The smoke checks" pass against `127.0.0.1:<port>`, each with `curl --max-time 5`; the element path is `/element/<the one directory under site/dist/element>/xrpl-file-element.js` of the release being checked (`$XRPL_FILE_SMOKE_DIST`); the fixed hash is `9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5`. The robot user agent is `TelegramBot (like TwitterBot)`.

- [ ] **Step 1: Write the failing tests**, each in a temporary root with stub `sudo`, `systemctl`, `node` and `xrpl-file-smoke` on `PATH` (the stubs log their calls to a file; the smoke stub's verdict comes from a file the test writes per port):
  - `'a good release becomes current and last good'`: archive with `site/server/main.mjs` and `site/dist/index.html`; exit 0; `current` and `last-good` point at `releases/<…>-<commit12>`; the log shows candidate check on 8081, restart, check on 8080 in that order.
  - `'a candidate that fails its check never becomes current'`: smoke on 8081 fails; exit non-zero; `current` unchanged; no restart in the log.
  - `'a release that fails after the switch is replaced by the last good one'`: smoke on 8080 fails; exit non-zero; `current` equals the previous `last-good`; two restarts logged.
  - `'an interrupted run is repaired first'` (Review Focus 4): set `current` to a release that is not `last-good`, then run `rollback`; before anything else `current` is back on `last-good` and a restart is logged.
  - `'a bad archive is refused before unpacking'` (Review Focus 5): archives with a symlink, with `../x`, with an absolute path, and over the entry limit (use a limit override only in the test via `XRPL_FILE_MAX_ENTRIES`) each exit non-zero and leave `releases/` without a new directory.
  - `'requests other than release and rollback are refused'`: `''`, `'release abc'`, `'release <40 hex>; id'`, `'bash'` each exit 2.
  - `'two runs never interleave'`: hold the lock with `flock` in the test; a run started meanwhile waits (or refuses after 60 s; use a 1 s override `XRPL_FILE_LOCK_WAIT` in the test) and does not touch `current`.
  - `'old releases are pruned but last good and its predecessor stay'`.

- [ ] **Step 2: Run** in the Docker command above — Expected: FAIL, scripts missing.

- [ ] **Step 3: Implement** both scripts to the spec's "Releases on the server": `set -euo pipefail`; validation with `[[ $req =~ ^release\ ([0-9a-f]{40})$ ]]`; staging with `head -c $((200*1024*1024+1))` and a size check; listing with `tar -tvzf` (first column must start with `-` or `d`; names must not start with `/` or contain a `..` component); `tar -xzf --no-same-owner --no-same-permissions`; candidate started with `node site/server/main.mjs` under `PORT=8081 CACHE_DIR=$(mktemp -d)`, killed after the check; switch with `ln -sfn` on a temporary name then `mv -T`; a `trap` that restores `last-good` on `ERR`, `INT`, `TERM`, `HUP` once the switch has happened.

- [ ] **Step 4: Run** the tests in Docker — Expected: PASS. `shellcheck deploy/server/xrpl-file-release deploy/server/xrpl-file-smoke` (in the same container after `apt-get install -y shellcheck`) — Expected: no findings.

- [ ] **Step 5: Commit** `feat(deploy): release and smoke scripts for the site server`.

### Task 7: Preparing the server and cutting over

**Files:**
- Create: `deploy/server/provision.sh`, `deploy/server/setup.sh`, `deploy/server/cutover.sh`, `deploy/server/xrpl-file-site.service`, `deploy/server/Caddyfile`, `deploy/server/site.caddy`, `deploy/server/sshd-01-xrpl-file.conf`, `deploy/server/sudoers-xrpl-file-deploy`
- Test: `deploy/server/test/config.test.mjs`

**Interfaces:**
- `provision.sh <user@ip> [domain]` (Git Bash on the owner's PC) — steps 1–6 of the spec's "Preparing the server"; sends `setup.sh` and the files above over one `ssh … 'bash -s'` with the files as a tar stream on a second connection (or a heredoc bundle — the executor picks one and ledgers it); passes the deploy public key, domain and IP as arguments.
- `setup.sh <deploy-pubkey-line> <domain>` (root on the server) prints one line per step, `ok <step>` or `changed <step>`, and finally `changed: <n>`.
- `cutover.sh <user@ip> [domain]` — the spec's cut-over steps 4–5: DNS-over-HTTPS lookups at `https://cloudflare-dns.com/dns-query` and `https://dns.google/resolve`; installs `/etc/caddy/sites/<domain>.caddy` from `site.caddy`; `systemctl reload caddy`; polls `https://<domain>/` every 10 s for 15 minutes for a valid certificate and an `X-Xrpl-File-Release` header; on timeout prints `journalctl -u caddy -n 40`.
- `Caddyfile`: global `admin unix//run/caddy/admin.sock` with permissions limiting it to the `caddy` user (exact syntax from caddyserver.com/docs/caddyfile/options, checked at execution) and `import /etc/caddy/sites/*.caddy`. `site.caddy`: `<domain> { encode zstd gzip; reverse_proxy 127.0.0.1:8080 { lb_try_duration 15s; lb_try_interval 250ms } }`.

- [ ] **Step 1: Write the failing tests** in `config.test.mjs`:
  - `'the unit is sandboxed as the spec says'`: `xrpl-file-site.service` contains `User=xrpl-file`, `ProtectSystem=strict`, `ReadWritePaths=/var/cache/xrpl-file`, `NoNewPrivileges=yes`, `PrivateTmp=yes`, `ProtectHome=yes`, `CapabilityBoundingSet=`, `MemoryMax=512M`, `Restart=on-failure`, `TimeoutStopSec=10`, and `ExecStart=/usr/bin/node /srv/xrpl-file/current/site/server/main.mjs`.
  - `'sshd turns password login off before cloud-init turns it on'`: the drop-in's file name sorts before `50-cloud-init.conf`; it sets `PasswordAuthentication no`, `KbdInteractiveAuthentication no`, `PermitRootLogin prohibit-password`.
  - `'deploy may restart the site and nothing else'`: the sudoers file is exactly one rule, `deploy ALL=(root) NOPASSWD: /usr/bin/systemctl restart xrpl-file-site`.
  - `'Caddy keeps its admin API off TCP'`: `Caddyfile` contains `admin unix//` and no `localhost:2019`; `site.caddy` contains `lb_try_duration 15s` and `reverse_proxy 127.0.0.1:8080`.
  - `'setup arms the sshd fallback before it reloads sshd'`: in `setup.sh`, the `systemd-run --on-active=5min` line comes before the first `systemctl reload ssh`, and `sshd -T -C` appears before it as well.

- [ ] **Step 2: Run** `node --test deploy/server/test/config.test.mjs` — Expected: FAIL.

- [ ] **Step 3: Implement** the files to the spec's "Preparing the server" and "Cut-over". Package sources: NodeSource `node_24.x nodistro` with its keyring in `/etc/apt/keyrings`, Caddy's `dl.cloudsmith.io/public/caddy/stable` per caddyserver.com/docs/install — the executor checks both current instructions before writing them. `/run/caddy` for the admin socket comes from a unit drop-in `RuntimeDirectory=caddy` if the packaged unit lacks it (checked on the server in Task 10, ledgered). The sshd revert unit is `xrpl-file-sshd-revert`; `provision.sh` disarms it with `systemctl stop xrpl-file-sshd-revert.timer` after a fresh key login (Ubuntu 24.04's service is `ssh`).

- [ ] **Step 4: Run** `node --test deploy/server/test/config.test.mjs` — Expected: PASS. `shellcheck deploy/server/*.sh deploy/server/xrpl-file-*` in Docker — Expected: no findings.

- [ ] **Step 5: Commit** `feat(deploy): prepare the site server from the owner's PC`.

### Task 8: GitHub Actions

**Files:**
- Create: `.github/workflows/deploy-site-server.yml`
- Modify: `.github/workflows/test.yml` (the `unit` job and the deploy jobs)

**Interfaces:**
- `deploy-site-server.yml`: `on: workflow_call` and `workflow_dispatch`; `concurrency: { group: deploy-site-server, cancel-in-progress: false }`; checks `secrets.SITE_SSH_KEY`, `secrets.SITE_SSH_KNOWN_HOSTS`, `vars.SITE_HOST` and fails naming the missing ones; Node 24; `node web/build.mjs`, `node site/build.mjs`, `node site/server/pack.mjs release.tar.gz "$GITHUB_SHA"`; writes the key to a 0600 file and the known-hosts line to a file; `ssh -i key -o UserKnownHostsFile=known_hosts -o StrictHostKeyChecking=yes -o BatchMode=yes deploy@"$SITE_HOST" "release $GITHUB_SHA" < release.tar.gz`.
- `test.yml`: a `site-target` step at the start of the existing `deploy-site` path decides `cloudflare` for unset or `cloudflare`, `server` for `server`, and fails with `SITE_TARGET must be "cloudflare" or "server", got "<value>"` otherwise; `deploy-site` (Pages) runs only for `cloudflare`; a new job `deploy-site-server` calls the reusable workflow with `secrets: inherit` only for `server`. The `unit` job gains `node --test deploy/server/test/*.test.mjs` and `shellcheck deploy/server/*.sh deploy/server/xrpl-file-release deploy/server/xrpl-file-smoke`.

- [ ] **Step 1: Write the change** (workflows have no local test runner here).

- [ ] **Step 2: Validate** with `npx --yes @action-validator/cli@0.6.0 .github/workflows/test.yml` and the same for `deploy-site-server.yml` (or `actionlint` if available) — Expected: no errors. Read the `if:` conditions once more against the three cases: unset, `cloudflare`, `server`, and a typo.

- [ ] **Step 3: Commit** `ci: deploy the site to the server or to Pages by SITE_TARGET`.

### Task 9: Documentation

**Files:**
- Modify: `site/README.md` (a "Hosting" section: the two targets, the server's parts, the runbook), `docs/superpowers/reports/2026-11-02-site/deploy-note.md` (owner steps: provision, secrets, first deploy by hand, cut-over, back to Pages; "File link cards: owner steps" says Workers Paid applies only when the target is Pages and Fail open only on the free plan)

- [ ] **Step 1: Write** the sections from the spec's "Preparing the server", "Cut-over" and "Back to Pages", with the exact commands: `deploy/server/provision.sh root@94.183.189.150`, the three `gh` commands as `provision.sh` prints them, `gh workflow run deploy-site-server.yml --ref main`, the SSH tunnel `ssh -L 8080:127.0.0.1:8080 root@94.183.189.150`, `deploy/server/cutover.sh root@94.183.189.150`, `gh variable set SITE_TARGET --body server`.
- [ ] **Step 2: Check** every command and path in the new text exists in the repository (`git ls-files`), and the text has no `—`.
- [ ] **Step 3: Commit** `docs(site): hosting on the server and the way back to Pages`.

### Task 10: On the real server (owner's go-ahead required)

This task changes the owner's server. Before Step 1, ask the owner in chat and wait for a clear yes.

- [ ] **Step 1: Provision** `deploy/server/provision.sh root@94.183.189.150`. Expected: every step prints `ok`/`changed`, step 4's fresh key login succeeds, password login is refused, the deploy key's unknown request is refused. Record Caddy's packaged unit facts (`RuntimeDirectory`, reload command) in the ledger.
- [ ] **Step 2: Run it again.** Expected: `changed: 0`.
- [ ] **Step 3: Release from this PC** with the deploy key: `node site/server/pack.mjs /tmp/r.tar.gz "$(git rev-parse HEAD)"` then `ssh -i ~/.ssh/xrpl-file-deploy deploy@94.183.189.150 "release $(git rev-parse HEAD)" < /tmp/r.tar.gz`. Expected: exit 0. Through the tunnel, the three robot checks of the deploy note pass against the real ledger.
- [ ] **Step 4: A failing release.** Pack a release whose `main.mjs` exits 1 and send it. Expected: refused at the candidate check, `current` unchanged, site still answering.
- [ ] **Step 5: Measure** `bench.mjs` on the server. Expected: the added latency, recorded in the ledger. Above 100 ms, do Task 11; otherwise ledger `Task 11: not needed (<n> ms)`.
- [ ] **Step 6: Report** to the owner what is ready for cut-over; DNS, the `gh` secrets and `SITE_TARGET` stay the owner's.

### Task 11 (only if Task 10 measured more than 100 ms): Picture reads in a worker thread

**Files:**
- Create: `site/server/picture-worker.mjs`
- Modify: `site/cards/handler.mjs` (optional `deps.readPicture`, default `readPicture` from `card.mjs`), `site/server/app.mjs`
- Test: `site/test/server-worker.test.mjs`; `site/test/cards-handler.test.mjs` must stay green unchanged

**Interfaces:**
- `deps.readPicture?(at: { network, hash }) → Promise<{ picture, outcome }>`; when present, `loadPicture` calls it instead of `readPictureUnderCeiling` and does not call `openSource`.
- The worker receives `{ network, hash }`, opens its own `LedgerClient`, runs `readPicture` under the 25 s ceiling, posts `{ picture, outcome }`; a worker error or exit answers `{ picture: null, outcome: 'transient' }` and a new worker is started.

- [ ] **Step 1: Failing tests**: `'a picture read in the worker answers like one on the main thread'` (same PNG through both paths, same bytes and outcome); `'a dead worker is a transient outcome and is replaced'`; `'the read cap still counts worker reads'`.
- [ ] **Step 2–4:** run → implement → run, then the whole site suite and `bench.mjs` on the server once more. Expected: added latency ≤ 100 ms.
- [ ] **Step 5: Commit** `feat(site): picture reads off the main thread`.
