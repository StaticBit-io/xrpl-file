# xrpl-file site

The static site for `xrpl-file.staticbit.io`: plain HTML, CSS and ES modules, no framework and no
server code. It is deployed to Cloudflare Pages; moving it to another host means copying `site/dist/`
and translating two files (see [Host files](#host-files-redirects-and-headers)).

```
site/
  src/            everything that is published (pages, js/, css/, fonts/, _redirects, _headers)
  data/           price.json, the price table (generated, committed)
  build.mjs       assembles site/dist
  serve.mjs       dev server for site/dist
  redirects.mjs   evaluator for the _redirects subset the site uses (build check and dev server)
  chrome-markup.mjs  build-time markup of the shared head, header and footer
  tools/          contrast.mjs: the contrast report and its check (not published)
  test/           node:test suites
  dist/           build output, git-ignored
```

## Build, serve, test

Requires Node 22 or newer. There are no npm dependencies.

```bash
cd web && node build.mjs && cd ..   # builds web/dist/1.1.0 (the element and the reader)
node site/build.mjs                  # writes site/dist
node site/serve.mjs                  # http://127.0.0.1:8791/ (next free port if taken)
node --test site/test/*.test.mjs     # site tests
```

`build.mjs` copies `site/src/**` to `dist/`, `web/dist/1.1.0/*` to `dist/element/1.1.0/` and
`site/data/price.json` to `dist/data/`. It fails, and removes `dist/`, when

- `web/dist/1.1.0` does not exist,
- any file is larger than 25 MiB (the Cloudflare Pages per-file limit), or
- a static file would be shadowed by a `_redirects` rule (next section).

`serve.mjs` binds to `127.0.0.1` only, starts at port 8791 and moves to the next port when one is
busy; it never touches a process it did not start. It applies `_redirects` with the same evaluator
the build check uses.

The site reaches the reader through one module, `src/js/xrpl.mjs`, which re-exports from
`/element/1.1.0/xrpl-file-reader.mjs`: the site and the embeddable element share one build. In
tests that specifier is redirected to `web/src` by a resolve hook
(`test/support/reader-hook.mjs`); a test imports `./support/register-reader.mjs` first and loads
site modules with a dynamic `import()` afterwards. When the element version changes, update the
specifier in `xrpl.mjs`, the constant in `build.mjs`, the hook, the script path in `src/js/cloud.mjs` and the script tag in `src/file.html`, and the prose that names the version on each home page.
The build refuses any `/element/<version>/` path in `src/` that differs from the constant (`checkElementVersion`).

## Host files: `_redirects` and `_headers`

These two files are the only host-specific part of the site. Both live in `src/` and are copied to
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
`site/tools/contrast.mjs` regenerates the AA table in `docs/superpowers/reports/2026-11-02-site/contrast.md`,
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
`docs/superpowers/reports/2026-11-02-site/contrast.md` from the tokens and the element palettes in the
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

The home page's hero is a point cloud driven by the real read of a file from the XRP Ledger. Until
mainnet, that is the transparent StaticBit logo on the testnet,
`xrpl-file://testnet/2AEB485BB0FAECA2DF4B60F196D5199042288FEB124ED8D95F4313CF06200DB4`
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
The animation stops when the read is done or has failed.

A testnet reset, or public nodes without the file's history, breaks it: the cloud then keeps the cubes
it received and shows a quiet message. That is accepted at launch; to bring the hero back, re-publish
the file and swap the constant.

1. Re-publish. The fixture tool publishes a plan to the testnet only, with faucet-funded throwaway
   accounts. Its working folder (`run/fixtures`) is git-ignored and holds `links.json` (key to link),
   `accounts.json` (the throwaway keys, never printed and never committed), `media/` (the source files,
   `staticbit-logo.png` among them) and `state/<key>/` (resume state). The hero is the `logo` entry of
   the one-entry plan `run/fixtures/plan-logo.json`:
   ```json
   [{"key":"logo","file":"staticbit-logo.png","type":"image/png","name":"staticbit-logo.png","writer":"a","cover":null}]
   ```
   - In `run/fixtures/links.json` delete the `logo` line, and delete `run/fixtures/state/logo/` (a
     finished state would be reused and give the old link back).
   - Run, from the repository root:
     ```bash
     dotnet run --project tools/XrplFile.Fixtures -- run/fixtures/plan-logo.json run/fixtures/media run/fixtures
     ```
     Entries that already have a link are skipped, so only `logo` is published. A stopped run is
     resumed by running the same command again.
   - The new link is the `logo` value in `run/fixtures/links.json`.
2. Swap the constants: set `HERO_LINK` in `site/src/js/config.mjs` to that link, and `HERO_ASPECT` to
   the picture's width / height (`1422 / 1231` for the logo).
3. Check the link in a browser at `/testnet/<HASH>`, then rebuild and redeploy the site.

After the first mainnet publication, `HERO_LINK` becomes the link of a real mainnet file; the steps
above no longer apply.

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
- `ELEMENT_PACKAGE`: the npm package name in the first embed snippet
  (`cdn.jsdelivr.net/npm/<package>@1.1.0/...`). The second snippet is the same file served from this
  site. `build.mjs` reads the constant, so the snippet follows it.

The embed snippets are inserted at build time: `<!--@element-snippet-->` and
`<!--@element-snippet-alt-->` in a page become the escaped snippets, with the `integrity` value from
`web/dist/1.1.0/integrity.json`. Both load a classic script (never `type="module"`: the element looks
for `mp4box.min.js` beside `document.currentScript`).
