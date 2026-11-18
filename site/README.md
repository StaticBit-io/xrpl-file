# xrpl-file site

The site for `xrpl-file.staticbit.io`: plain HTML, CSS and ES modules, no framework. The pages are
static; the only code that runs on a server writes link cards for link-preview robots. It is hosted
on the owner's server or on Cloudflare Pages, chosen by the repository variable `SITE_TARGET` (see
[Hosting](#hosting)); both read the same [host files](#host-files-redirects-and-headers).

```
site/
  src/            everything that is published (pages, js/, css/, fonts/, _redirects, _headers)
  data/           price.json, the price table (generated, committed)
  build.mjs       assembles site/dist
  serve.mjs       dev server for site/dist
  resolve.mjs     maps a request path to a built file the way Pages does (dev and production servers)
  redirects.mjs   evaluator for the _redirects subset the site uses (build check and both servers)
  headers.mjs     evaluator for the _headers subset the site uses (production server)
  cards/          link cards for robots: rules, reading, HTML, the request handler
  server/         the production server: static files, the card handler, its disk cache, packing
  functions/      the Pages Function that runs cards/ when Pages hosts the site
  chrome-markup.mjs  build-time markup of the shared head, header and footer
  tools/          contrast.mjs: the contrast report and its check (not published)
  test/           node:test suites
  dist/           build output, git-ignored
```

## Build, serve, test

Requires Node 22 or newer. There are no npm dependencies.

```bash
cd web && node build.mjs && cd ..   # builds web/dist/1.3.0 (the element and the reader)
node site/build.mjs                  # writes site/dist
node site/serve.mjs                  # http://127.0.0.1:8791/ (next free port if taken)
node --test site/test/*.test.mjs     # site tests
```

`build.mjs` copies `site/src/**` to `dist/`, `web/dist/1.3.0/*` to `dist/element/1.3.0/` and
`site/data/price.json` to `dist/data/`. It fails, and removes `dist/`, when

- `web/dist/1.3.0` does not exist,
- any file is larger than 25 MiB (the Cloudflare Pages per-file limit), or
- a static file would be shadowed by a `_redirects` rule (next section).

`serve.mjs` binds to `127.0.0.1` only, starts at port 8791 and moves to the next port when one is
busy; it never touches a process it did not start. It applies `_redirects` with the same evaluator
the build check uses.

The site reaches the reader through one module, `src/js/xrpl.mjs`, which re-exports from
`/element/1.3.0/xrpl-file-reader.mjs`: the site and the embeddable element share one build. In
tests that specifier is redirected to `web/src` by a resolve hook
(`test/support/reader-hook.mjs`); a test imports `./support/register-reader.mjs` first and loads
site modules with a dynamic `import()` afterwards. When the element version changes, update the
specifier in `xrpl.mjs`, the constant in `build.mjs`, the hook, the script path in `src/js/cloud.mjs` and the script tag in `src/file.html`, and the prose that names the version on each home page.
The build refuses any `/element/<version>/` path in `src/` that differs from the constant (`checkElementVersion`).

## Host files: `_redirects` and `_headers`

These two files are the host-specific part of the static site (the link-card function adds
`_routes.json`, `functions/` and `wrangler.toml`, see [File link cards](#file-link-cards)). Both live in `src/` and are copied to
the root of `dist/`.

### `_redirects`

| Rule | Meaning | nginx |
|---|---|---|
| `/ru/:file /ru/:file 200`, and the same for `/de`, `/es`, `/pt`, `/zh`, `/js`, `/css`, `/data`, `/fonts`, `/img` | Static files in a two-segment folder are served as themselves. All five language folders are listed (see [Languages](#languages)). | `location ~ ^/(ru\|de\|es\|pt\|zh\|js\|css\|data\|fonts\|img)/ { try_files $uri =404; }` |
| `/:network/:hash /file 200` | A file link path (`/testnet/<HASH>`, `/21338/<HASH>`) is answered by the one static file page (`file.html`), without changing the URL. The page reads the path. The target is the extension-less `/file`: Pages applies its HTML handling to a rewritten target too, so a target spelled `/file.html` is answered with a redirect to `/file` and the link in the path is lost. | `location ~* "^/(mainnet\|testnet\|devnet\|[0-9]+)/[0-9a-f]{64}/?$" { try_files /file.html =404; }` |
| `/:network/:hash/ /file 200` | The same with a trailing slash. | the `/?` in the pattern above |

The nginx pattern is stricter than Pages. Pages sends any two-segment path that no earlier rule
claims (`/foo/bar`) to the file page, which then shows its "not a link" state; nginx answers 404 for
it, because the pattern only matches a network name or NetworkID and a 64-digit hash. `~*` keeps the
match case-insensitive, as the spec requires (`/TESTNET/<hash>` is accepted and canonicalised by the page).

Language pages are plain files: `/` is `index.html`, `/ru/` is `ru/index.html`, `/de/` is `de/index.html` and so on; no rule is needed for the home pages themselves.

Canonical casing (`/testnet/<UPPER-HASH>`) is not done by the host: the file page script moves a
non-canonical URL to `linkFromPath(location.pathname).canonicalPath` (see `src/js/route.mjs`).

#### Why the rule order matters (Cloudflare Pages)

A `:placeholder` matches exactly one path segment. So `/:network/:hash` also matches every
two-segment static URL: `/ru/index.html`, `/js/route.mjs`, `/css/site.css`, `/data/price.json`,
`/fonts/x.woff2`. What Cloudflare documents
([Pages, Redirects](https://developers.cloudflare.com/pages/configuration/redirects/)):

- rules are evaluated top-down and only the first matching rule applies;
- "Redirects are always followed, regardless of whether or not an asset matches the incoming request";
- for a 200 (proxy) rule, the target is shown and is not matched against the rules again.

The page does not say that an existing static file beats a 200 rule, so the site does not rely on
it. Each two-segment static folder gets a pass-through rule above the file-page rule: a 200 rule
whose target is the request path itself serves the asset (this follows from the first-match and
no-re-matching rules above) and, being first, stops the file-page rule from seeing the request.
`/element/<version>/...` and `/` are not two-segment paths, so the file-page rule never sees them.

Three safeguards keep this honest:

- `build.mjs` fails when any built file's URL (and, for an `index.html`, its directory URL) is
  sent somewhere else by `_redirects`. Adding a new two-segment folder without its pass-through rule
  therefore fails the build instead of breaking in production.
- `build.mjs` also fails when a 200 rule's destination ends in `.html`, `/index` or `/index.html`
  (see the file-page row above).
- `serve.mjs` uses the same first-match evaluator and no "file exists" shortcut, and emulates Pages'
  HTML handling: `/x.html` and `/x/index.html` answer with a redirect to `/x` and `/x/`, and a rewrite
  to `/file` is resolved to `file.html` and served as 200 at the original URL.
- `test/redirects.test.mjs` checks the shipped `_redirects` against the real folder names.

Not verified on a live deployment: that Pages really answers a pass-through 200 rule with the asset.
Check it once on the first preview deployment: open `/js/route.mjs`, `/ru/` and
`/testnet/<HASH>`; the first must be JavaScript, the second the Russian home page, the third the
file page. `/testnet/<HASH>` must answer 200 with no `Location` header (for example
`curl -sI https://<preview>/testnet/<HASH>`); a 3xx there means the rewrite is being redirected and
the link is lost.

Pages also treats a project without a top-level `404.html` as a single-page application and answers
every unknown path with `index.html`. The site needs a real `404.html`.

### `_headers`

| Block | Meaning | nginx |
|---|---|---|
| `/element/*`: `Access-Control-Allow-Origin: *`, `Cache-Control: public, max-age=31536000, immutable` | The element build is versioned in its path and never changes. Embedding sites fetch `mp4box.min.js` from it cross-origin. | `location /element/ { add_header Access-Control-Allow-Origin "*" always; add_header Cache-Control "public, max-age=31536000, immutable" always; }` |
| `/`, `/ru/`, `/de/`, `/es/`, `/pt/`, `/zh/`, `/*.html`: `Cache-Control: no-cache` | Pages are revalidated on every load. | `location = / { add_header Cache-Control "no-cache" always; }` and the same for `= /ru/` and the other language homes, and `~ \.html$` |
| `/examples`, `/ru/examples`, `/de/examples`, `/es/examples`, `/pt/examples`, `/zh/examples`: `Cache-Control: no-cache` | The examples pages are pretty URLs, so neither `/*.html` nor a language home matches them. | `location ~ ^/((ru\|de\|es\|pt\|zh)/)?examples$ { add_header Cache-Control "no-cache" always; }` |
| `/favicon.svg`, `/favicon.ico`, `/apple-touch-icon.png`: `Cache-Control: public, max-age=604800` | The icons keep their names when they change, so they cannot be immutable; a week spares the request every page makes for them. | `location ~ ^/(favicon\.(svg\|ico)\|apple-touch-icon\.png)$ { add_header Cache-Control "public, max-age=604800" always; }` |

The `/*` block sets `X-Content-Type-Options: nosniff` for every path: the site's files are served with
their real types, so a browser has no reason to guess. nginx: `add_header X-Content-Type-Options "nosniff" always;`
in the server block (repeated in each `location` that has its own `add_header`).

Pages joins the values of a header that several matching blocks set with a comma
([Pages, Headers](https://developers.cloudflare.com/pages/configuration/headers/)), so the blocks above
do not overlap. Pages also applies redirects before headers: a request that a `_redirects` rule
rewrites (the file page) gets Pages' default headers, not the ones above. In
nginx, an `add_header` in a `location` replaces the ones inherited from the server block; repeat
every header in each location that needs it.

## Examples page

Six pages, one per language: `/examples`, `/ru/examples`, `/de/examples`, `/es/examples`, `/pt/examples`,
`/zh/examples` (sources `src/examples.html` and `src/<lang>/examples.html`). There is no trailing-slash
form: `/ru/examples/` is taken by the file-page rule `/:network/:hash/` in `_redirects` and shows the
file page's "not a link" state. `_redirects` needs no rule of its own, `_headers` has one entry per URL
(see [`_headers`](#_headers)). The pages are fixed-language, so the build writes their canonical link
and the alternates like the home pages'. They are reached from the hero (`#examples-link`, same id in
every home) and from the footer of every page; the footer link is `data-examples-link` on the file page
and the 404, where `chrome.mjs` repoints it at the language in use.

The cards are not in the page sources: `<!--@examples-->` is replaced by `renderExamples(lang)`
(`examples-markup.mjs`) from the list in `src/js/examples.mjs` and the words in
`src/js/examples-strings.mjs`. `src/js/examples-page.mjs` makes the elements follow the site theme.

To add or replace an example:

1. Add or change its entry in `EXAMPLES` (`key`, `link`, `kind`, `bytes` and, for a picture or a video, `width` and `height`).
2. Add the same `key` to `EXAMPLE_STRINGS` in all six languages: a title and one line.
3. `node --test site/test/*.test.mjs`: the tests fail on a language without the key.

How the owner's own files get onto mainnet and into this list: [`tools/examples/README.md`](../tools/examples/README.md).

## Hosting

| `SITE_TARGET` | Where a push to `main` deploys | Job |
|---|---|---|
| unset or `cloudflare` | Cloudflare Pages, with the link cards as a Pages Function | `.github/workflows/deploy-site-pages.yml` |
| `server` | the owner's server, `94.183.189.150` | `.github/workflows/deploy-site-server.yml` |

Any other value fails the deploy with a message naming the variable. The design is in
`docs/design/2026-11-12-site-server-design.md`.

### The server

```
internet ──443/80──> Caddy ──127.0.0.1:8080──> node site/server/main.mjs
```

Caddy holds the TLS certificate and retries for up to 15 s while the site restarts; Node is the whole
site. It answers like Pages, including Pages' default headers (`Access-Control-Allow-Origin: *`,
`Referrer-Policy`, `Cache-Control: public, max-age=0, must-revalidate`, `no-store` on the 404 page),
308 redirects that keep the query string, content-hash ETags, and 404 for the host files themselves.
`site/test/server-static.test.mjs` holds a table of what the live Pages site answered for a set of
paths, and the server must answer the same.

| Part | Role |
|---|---|
| `server/static.mjs` | the built site with Pages' semantics and headers |
| `server/app.mjs` | routes `_routes.json` paths to `cards/handler.mjs`; at most 4 ledger reads and 32 card requests at once, past which a robot gets the static answer; 5 s drain on `SIGTERM` |
| `server/cache.mjs` | the card cache in `/var/cache/xrpl-file`, 256 MiB; card pages are kept per build of `file.html` |
| `server/main.mjs` | the entry point; `PORT`, `HOST`, `SITE_ORIGIN`, `CACHE_DIR` from the environment |
| `server/pack.mjs` | packs a release: `node site/server/pack.mjs <out.tar.gz> <commit>` |
| `deploy/server/` | `provision.sh` and `setup.sh` (prepare the server), `cutover.sh`, the release and smoke scripts, the unit, Caddy and sshd files |

To run the production server locally after both builds:

```bash
PORT=8090 CACHE_DIR=/tmp/xf-cache node site/server/main.mjs
```

### Releases

The deploy key in GitHub can do one thing on the server: run `/usr/local/bin/xrpl-file-release`
(`release <commit>` with the archive on standard input, or `rollback`). The script starts the new
release as the unit `xrpl-file-candidate@<release>` (the site's user and sandbox, port 8081, two
minutes at most; release code never runs as `deploy`) and runs `xrpl-file-smoke` against it before
visitors see it, then switches the `current` link, restarts the service and checks again. A release
that fails after the switch is replaced by the last good one, also when the deploy's connection has
dropped; a deploy cut off half-way is repaired by the next run. The five newest releases stay in
`/srv/xrpl-file/releases`.

To roll back by hand (a second rollback goes further back: a release rolled back from is never a
rollback target again):

```bash
ssh -i ~/.ssh/xrpl-file-deploy deploy@94.183.189.150 rollback
```

### Moving the site to the server

Merges into `main` pause from step 2 until step 6, so the server and Pages serve the same commit.

1. Prepare the server from the owner's PC (Git Bash), then run the three `gh` commands it prints:

   ```bash
   deploy/server/provision.sh root@94.183.189.150
   ```

   It needs the server's ed25519 host key in `~/.ssh/known_hosts` and key login as root. Running it
   again reports `changed: 0`. If an SSH change ever locks the owner out, the hosting provider's web
   console is the way back in; the script also undoes its own `sshd` change after five minutes unless
   a fresh key login succeeds.
2. Put a release on the server and look at it through a tunnel:

   ```bash
   gh workflow run deploy-site-server.yml --ref main
   ssh -L 8080:127.0.0.1:8080 root@94.183.189.150
   ```

   Then open `http://127.0.0.1:8080/` and try the three `curl` lines from "File link cards" with port
   8080.
3. In Cloudflare DNS, delete the `xrpl-file` CNAME record of the Pages project and add an A record
   `xrpl-file` → `94.183.189.150`, proxy off, TTL 60 s. Then remove the custom domain from the Pages
   project (Cloudflare documents this order for detaching a custom domain).
4. Install the Caddy site once DNS points at the server; the script waits for public resolvers, then
   for HTTPS with a valid certificate:

   ```bash
   deploy/server/cutover.sh root@94.183.189.150
   ```

5. If the certificate does not arrive within 15 minutes, the script stops and shows Caddy's log: fix
   the cause, or go back to Pages (below).
6. Point the deploys at the server, deploy the current `main` once more, and resume merges:

   ```bash
   gh variable set SITE_TARGET --body server
   gh workflow run deploy-site-server.yml --ref main
   ```

### Back to Pages

Bring Pages up to date with the current `main` and point the deploys back at it:

```bash
gh variable set SITE_TARGET --body cloudflare
gh workflow run deploy-site-pages.yml --ref main
```

Once that run is green, delete the A record and add `xrpl-file.staticbit.io` as a custom domain of the
Pages project again, which creates the proxied CNAME. Until Pages shows the domain as active, visitors may get errors, so do it at a quiet hour, and
leave the server running for a day for visitors whose resolvers still hold the A record. The link
cards then need Workers Paid ("File link cards" above).

## Visual system and the shared header and footer

`src/css/tokens.css` holds every colour, face, size and space the pages use, `base.css` the element
defaults, `site.css` the header, footer and pages (its first lines are the design stamp: hallmark's
Vantage theme, catalogue mode). A page links nothing itself: the head marker below brings `site.css`,
which imports the other two. A page's own CSS styles only with the tokens. Colours are on
`:root[data-theme="dark"]` (also the default without the attribute: the first visit is dark whatever
the OS prefers) and `:root[data-theme="light"]`: `--paper`, `--paper-2`, `--paper-3`, `--ink`,
`--ink-2`, `--ink-muted`, `--rule`, `--rule-2`, `--rule-soft`, `--accent`, `--accent-hi`,
`--accent-ink`, `--focus`, `--error` and the drawings' `--cell`, `--cell-lit`, `--cell-verified`, plus the hero cloud's `--point-lo`, `--point-mid`, `--point-hi`.
The lime accent (moss in light) marks only what is primary or verified. The embedded `<xrpl-file>` takes the
page accent for its frame in both themes (moss in light) and `--overlay-accent` (a lime) for its dark
overlay, through `--xrpl-file-accent` and `--xrpl-file-overlay-accent` in `base.css`. The theme switch is named for
the theme it turns on ("Light theme") and is pressed while that theme is on; the choice is kept in
`localStorage` under `xf-theme`, and a blocked storage leaves the page dark with a working switch.
Text is Geologica for everything, self-hosted in `src/fonts/` (variable woff2, latin and cyrillic
subsets, the OFL licence and the download sources with checksums in `SOURCES.txt`; no request leaves
the site). Chinese falls back to the system CJK faces named in `--font-body`. A monospace is only for
real values (`code`, `.value`) and is the visitor's own system face. Geologica has no check mark and
no thin space: draw a check mark (SVG) instead of typing U+2713, and use margins, not U+2009.
`site/tools/contrast.mjs` regenerates the AA table in `docs/design/site-contrast.md`,
and `site/test/contrast.test.mjs` fails when a pair misses its threshold.

A page uses the shared chrome by putting three markers in its source; `build.mjs` replaces them:

```html
<html lang="en">                      <!-- the page's own language (zh-Hans for Chinese); add data-lang-source="storage" on a page with one URL for every language -->
<head> ... <!--@chrome:head-->        <!-- canonical and hreflang alternates, pre-paint theme script, font preloads, site.css, chrome.mjs -->
<body> <!--@chrome:header-->          <!-- skip link (targets #main), wordmark, language menu, theme switch -->
  <main id="main"> ... </main>
  <!--@chrome:footer-->               <!-- repository, specification, the languages as links, theme -->
```

The language comes from `<html lang>`. The header's language control is a native disclosure (`<details>`)
of plain links, one per active language in its own name, and the footer lists the same links; on a
fixed-language page they point at the equivalent page and work without script. `chrome.mjs` adds Escape
and click-outside closing. The file page and `404.html` have one URL for every language and are marked
`data-lang-source="storage"`: there `chrome.mjs` takes the language from the stored choice (then the
browser's), switches the header and footer text in place on a click, stores the choice and dispatches
`xf-langchange` (`detail.lang`) on `document`, so the page's own text can follow. The theme switch sets
`data-theme` on `<html>` and the `theme` attribute of every `<xrpl-file>` that exists at that time: a
page that creates the element later must copy `document.documentElement.dataset.theme` into it.

Contrast is computed, not eyeballed: `node site/tools/contrast.mjs` rewrites
`docs/design/site-contrast.md` from the tokens and the element palettes in the
repository README, and `test/contrast.test.mjs` fails when a required pair misses WCAG AA or the report
is out of date.

The icons are the header's mark on the desktop app's dark tile. `node site/tools/favicon.mjs` writes
`src/favicon.svg`, `src/favicon.ico` (16, 32 and 48 px) and `src/apple-touch-icon.png` (180 px) from
`MARK_CELLS` in `chrome-markup.mjs`, with its own rasteriser and no image library;
`test/favicon.test.mjs` fails when a committed file differs from what it writes. The tile is dark in both
colour schemes, like the app's icon; a media query in the svg could not reach the .ico or the .png. In
the favicon an empty cell is a filled dark square rather than an outline: at 16 px the outline is under a
pixel wide, its hole closes, and the empty cells look like the lit ones. The 180 px touch icon keeps the
outlines. `renderHead` puts the three links (`ICON_LINKS`) on every page.

Link cards (Telegram, X and other previews) come from Open Graph tags that `renderHead` writes from the
page's own `<title>` and `<meta name="description">` (`cardTags` in `chrome-markup.mjs`), so a page
states its card once; a page without both gets no card tags. Every card shows `src/og-banner.png`,
1200 × 630: the wordmark beside the hero's point cloud, photographed from the real scene reading the
real hero file. Redraw it after changing the hero file, the mark or the palette:

```bash
node web/build.mjs && node site/build.mjs && node site/tools/og-banner.mjs
```

The tool serves `site/dist`, renders it in headless Chrome or Edge (`CHROME` overrides the path) and
waits for the read to verify before the shot, so it needs the network. The result is not byte-stable,
so `test/og-banner.test.mjs` checks only the size. Telegram and X cache a card by URL: give a new
banner a new file name, or refresh a Telegram preview through @WebpageBot.

### File link cards

A link to one file, `/<network>/<HASH>`, gets a card of its own: the file's name, its type and size
("PNG image, 123.0 KB, stored in the XRP Ledger."), and a picture. A Pages Function builds it, only
for link-preview robots (Telegram, X, Facebook, Discord, Slack, WhatsApp's link fetcher, LinkedIn,
by user agent).
A person's browser gets the static file page with no ledger read and no cache lookup in front of it.
The design is in `docs/design/2026-11-03-link-cards-design.md`.

| Part | Role |
|---|---|
| `src/_routes.json` | the function runs only on `/mainnet/*`, `/testnet/*`, `/devnet/*` and `/og/*`; every other path is static and costs no invocation |
| `functions/[[path]].js` | the Cloudflare adapter: `env.ASSETS`, `caches.default`, `context.waitUntil`, a `LedgerClient` per read |
| `cards/rules.mjs` | robot user agents, card paths, which files can be their own picture, magic bytes |
| `cards/card.mjs` | reads the manifest, the cover and the picture through the element's `XrplFileReader`, so every byte is verified against the hash |
| `cards/html.mjs` | writes the card into the static file page's `<head>`; the rest of the page is unchanged |
| `cards/handler.mjs` | the requests: deadlines, cache lifetimes, one picture read at a time per file |
| `wrangler.toml` | project name, output directory, compatibility date and `limits.cpu_ms = 5000` |

On the owner's server the same `cards/handler.mjs` runs inside `server/app.mjs` (see
[Hosting](#hosting)), with a disk cache in place of the Cache API and the lifetimes below unchanged.

The card picture is `/og/<network>/<HASH>`: the file itself when it is a PNG, JPEG, WebP or GIF of at
most 1 MB whose first bytes match its type, otherwise its cover (section 4.1 of the format), otherwise
the banner, which a card without a picture names directly. The robot's page request starts the
picture read in the background, so the picture request that follows usually finds it cached.

| Result | How long the Cache API keeps it |
|---|---|
| a card of a file; a picture; verified bytes that are not a picture; a file with no picture to give | a year (`immutable`) |
| no manifest with that hash on the network; a cover the reader refused (it does not say whether the cover failed its checks or the node failed) | an hour |
| a node failure, a deadline (4 s for the page, 5 s for the picture) or a picture read past its 25 s ceiling | five minutes |

The robot itself gets the card HTML with `Cache-Control: no-cache` and `Vary: User-Agent`: the
same URL is a person's page, so nothing downstream may keep the robot's copy. The cache keys carry a
version (`/__cards/v1/…`); bump `KEY_VERSION` in `cards/handler.mjs` when the card output changes,
or cached cards keep their old HTML for a year. A picture read that the platform cancels never
settles, so a read older than 30 s is no longer joined by later requests.

Every failure answers the generic card or the banner, never an error status. Pages applies no
`_redirects` rule on a path the function owns, so the function serves the file page itself for
every two-segment path it does not take as a card (`/mainnet/xyz` gets the page's "not a link"
state, as the rule gave it). NetworkID paths (`/21338/<HASH>`) are outside `_routes.json` and keep
the rule.

`wrangler.toml` is the project's source of truth: the settings it names can no longer be edited in
the dashboard. Its compatibility date is the newest the pinned wrangler's runtime accepts; raise it
together with the wrangler version.

`serve.mjs` does not run the function. To see a card locally, from `site/` after both builds:

```bash
npx --yes wrangler@4.151.1 pages dev
curl -s -A "TelegramBot (like TwitterBot)" http://127.0.0.1:8788/mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5 | grep -E 'og:(title|image)'
curl -sI http://127.0.0.1:8788/og/mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5
curl -s -A "Mozilla/5.0" http://127.0.0.1:8788/mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5 | grep -c '<title>xrpl-file</title>'
```

The first prints the logo's name in `og:title` and `/og/mainnet/…` in `og:image`, the second `200`
with `content-type: image/png`, the third `1`.

## Languages

The site is built for six languages. A language is a data change: its words, its tags and its home page.

| Code | Home | `<html lang>` and hreflang | Number and date locale | Shipped |
|---|---|---|---|---|
| `en` | `/` | `en` | `en-GB` | yes, written by hand |
| `ru` | `/ru/` | `ru` | `ru-RU` | yes, written by hand |
| `de` | `/de/` | `de` | `de-DE` | yes, translated from English, awaiting a native speaker's review |
| `es` | `/es/` | `es` | `es-ES` | yes, translated from English, awaiting a native speaker's review |
| `pt` | `/pt/` | `pt` | `pt-BR` | yes, translated from English, awaiting a native speaker's review |
| `zh` | `/zh/` | `zh-Hans` | `zh-Hans-CN` | yes, translated from English, awaiting a native speaker's review |

Translated from English, awaiting a native speaker's review: de, es, pt, zh. They stay marked here until a
native speaker has read them. The same holds for the element's own strings in `web/src/element/strings.mjs`. English and Russian are written by hand. The locales are chosen for
readers, not for a region: English is `en-GB` for its "2 November 2026" date, Portuguese is Brazilian, Spanish uses a decimal comma
(`es-ES`), and Chinese is Simplified for mainland China, which is why its tag carries the script.

### The active list

`src/js/languages.mjs` holds `LANGS` (every language the site is built for, in menu order) and
`ACTIVE_LANGS` (the ones it ships). Only an active language gets a menu entry, an `hreflang` alternate
and a page in the build, and only an active language is chosen by `getLang` (the stored choice, else
the first language of `navigator.languages` whose primary subtag is active: `zh-TW` is Chinese,
`pt-BR` Portuguese, `es-419` Spanish; else English). Tests loop over `ACTIVE_LANGS`, so flipping the
list is what turns the checks on for a new language.

First visit: the inline script in `<head>` (`prePaintScript` in `chrome-markup.mjs`, written from
`ACTIVE_LANGS`) moves a visitor from exactly `/` to their language's home with `location.replace`, keeping
the query and the fragment, when the stored choice (else the first matching browser language) is not
English. It never acts on `/ru/` or any other path, for a visitor who arrives from this site (so the
one who picks English in the menu with storage blocked does not bounce back), or when the stored choice
is English. The "arrives from this site" test reads `document.referrer`, so it relies on the browser sending a
same-origin `Referer`: a `Referrer-Policy: no-referrer` header (or meta tag) would empty it and the visitor who
just chose English with storage blocked would be redirected back. Keep the policy at the browser default
(`strict-origin-when-cross-origin`) or looser. A test pins its choice against `getLang`. The build refuses a `src/<lang>/` folder for a
language that is not active, and an active language without its `src/<lang>/index.html`.

### Adding a language

Do all of these, then add the code to `ACTIVE_LANGS`:

1. `src/js/chrome-strings.mjs`: a `STRINGS.<lang>` table with the same keys as `en`.
2. `src/js/strings.mjs`: a `TABLES.<lang>` table (the file page and the 404) with the same keys, and
   the same `{placeholders}` in each string.
3. `src/js/home-strings.mjs`: `CHECK_LINK_STRINGS`, `COPY_STRINGS`, `PRICE_STRINGS`, `NETWORK_NAMES` and
   `NETWORK_SHORT` entries, and a `CLOUD_STRINGS.<lang>` function with the same shape as `en`. Counts
   use `plural(lang, n, forms)`, which asks `Intl.PluralRules`; give the forms the language has.
4. `src/js/price.mjs`: `SIZE_UNITS.<lang>` (B, KB, MB as the language writes them).
5. `src/<lang>/index.html`: copy the English page, translate its text, and set `<html lang>` to the tag
   in the table. Keep every `id`, the three chrome markers and the two snippet markers; write no
   `canonical`, `alternate` or `hreflang` link, because the build writes them. Translate the
   description, the four drawings' `<title>`s, the `aria-label`s and the button labels too.
6. `src/_redirects` and `src/_headers` already list all five non-English folders; the build check
   fails if a built page is not covered.
7. `node --test site/test/*.test.mjs` and `node site/build.mjs`. The tests check that every table has
   the same keys in every active language, that each home page has the English page's ids and its own
   `<html lang>`, that the menu and the alternates list the language, and that formats come from the
   language's locale.

Fonts: Geologica's Latin subset covers German, Spanish and Portuguese. Chinese uses the system CJK
faces named in `--font-body`.

### hreflang and canonical

Every fixed-language page (the home pages) gets, from `chrome-markup.mjs` at build time, a canonical
link to itself, one `rel="alternate"` per active language and an `x-default` that points at the English
page. The file page (`/<network>/<HASH>`) and the 404 have one URL for every language and render in the
visitor's stored language, so they declare no alternates and no canonical: an alternate must name a
page that is in one language, and these are not. The language menu on them switches the text in place
and leaves the URL alone.

### Status

German, Spanish, Portuguese and Chinese are in `ACTIVE_LANGS` and ship, translated from English and
awaiting a native speaker's review: de, es, pt, zh. A reviewer should start with the terms for ledger,
chunk, block, manifest, reserve and fee: German keeps "Ledger" and "Chunk", Spanish and Portuguese keep
"ledger" and say fragmento, and Chinese says 账本, 分片, 数据块, 清单 and 储备金.

## The hero file and its durability

The home page's hero is a point cloud driven by the real read of a file from the XRP Ledger: the
transparent StaticBit logo, published on mainnet from the desktop app,
`xrpl-file://mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5`
(`staticbit-logo.png`, image/png, 1422 x 1231, 122,958 bytes, 123 chunks, 1 block). Each chunk the
reader accepts becomes a cube, the block's cubes turn lime when its hash matches, and when everything is
verified a small `<xrpl-file>` card shows the file beside the cloud. The card reads the file a second
time through the element (the file is small, and the element's own checks are the ones the file page
shows).

What is real and what is drawn: the chunk count, the ledger range, each chunk's bytes (they place the
points inside its cube), the ledger of every chunk's transaction (the reader reports it with the chunk,
and the cube stands on the pad of that ledger) and the 84 bytes of the link come from the read. With
more ledgers than ten, a pad stands for a run of neighbouring ledgers and the note under the cloud
says how many. Drawn: a cube's place within its pad (slots fill in arrival order), the relief of the
ground and the gap before the manifest's cube (the reader does not report the manifest's own ledger).
The sway stops when the read is done or has failed. A verified read then turns the scene once round,
eased in and out over `TURN_MS` (4.5 s, under the 5 s past which WCAG 2.2.2 asks for a pause control),
and it stops on the angle the sway left; a failed read does not turn. The two figures fade out over the
first 0.4 s and back in over the last, following their cubes meanwhile. There is no turn with reduced
motion, nor when the cloud is off screen or the tab is hidden at that moment; leaving the screen, hiding
the tab, or a pointer or key on the cloud ends a running turn on its last frame. After that no frame is
requested.

On mainnet the file stays readable as long as the element's nodes keep full history
(`wss://xrplcluster.com`, then `wss://s2.ripple.com`). A node that answers without the file's
ledgers breaks the read: the cloud then keeps the cubes it received and shows a quiet message.

To show another file:

1. Publish it on mainnet with the desktop app and copy its link.
2. Swap the constants: set `HERO_LINK` in `site/src/js/config.mjs` to that link, and `HERO_ASPECT` to
   the picture's width / height (`1422 / 1231` for the logo). The test in `site/test/home.test.mjs`
   pins the link, so update it with the constant.
3. Check the link in a browser at `/mainnet/<HASH>`, then rebuild and redeploy the site.

The logo was first published on the testnet
(`xrpl-file://testnet/2AEB485BB0FAECA2DF4B60F196D5199042288FEB124ED8D95F4313CF06200DB4`) by the
fixture tool (`tools/XrplFile.Fixtures`, plan `run/fixtures/plan-logo.json`). That copy is no longer
the hero and is not kept up: a testnet reset removes it.

## The price table

`data/price.json` is a snapshot of what publishing costs, computed by the publisher's own estimate
(`tools/XrplFile.SitePrice`) from live mainnet fees and reserves (read only). Regenerate it with:

```bash
dotnet run --project tools/XrplFile.SitePrice -- --out site/data/price.json
```

`--node <wss url>` overrides the default node (`wss://xrplcluster.com`). The file records the time
it was taken; commit it, then rebuild.

The home page fetches `/data/price.json` in the visitor's browser and draws the table from it; if the
fetch fails the section keeps a plain sentence instead. It shows the fees (spent) and the reserve
(held and returned) as they are in the file, with the date in UTC. Nothing on the page multiplies a
transaction count by a fee: the file's own fee column is what is shown.

## Home page settings

`site/src/js/config.mjs` holds the values the owner changes by hand:

- `HERO_LINK`, `HERO_ASPECT`: the hero file (see above).
- `PUBLISHER_URL`: the publisher's GitHub Releases URL. While it is `null` the "Download the
  publisher" button is disabled and marked "soon"; set the URL and the button becomes a link.
- `ELEMENT_PACKAGE`: the npm package name in the embed snippet
  (`cdn.jsdelivr.net/npm/<package>@1.3.0/...`). `build.mjs` reads the constant, so the snippet follows
  it. The site serves its own copy under `/element/` for its pages, but does not offer it in the
  snippet: a CDN outlives this site.

The embed snippet is inserted at build time: `<!--@element-snippet-->` in a page becomes the escaped
snippet, with the `integrity` value from `web/dist/1.3.0/integrity.json`. It loads a classic script (never `type="module"`: the element looks
for `mp4box.min.js` beside `document.currentScript`).
