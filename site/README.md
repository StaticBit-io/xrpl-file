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
specifier in `xrpl.mjs`, the constant in `build.mjs` and the hook.

## Host files: `_redirects` and `_headers`

These two files are the only host-specific part of the site. Both live in `src/` and are copied to
the root of `dist/`.

### `_redirects`

| Rule | Meaning | nginx |
|---|---|---|
| `/ru/:file /ru/:file 200`, and the same for `/js`, `/css`, `/data`, `/fonts`, `/img` | Static files in a two-segment folder are served as themselves. | `location ~ ^/(ru\|js\|css\|data\|fonts\|img)/ { try_files $uri =404; }` |
| `/:network/:hash /file 200` | A file link path (`/testnet/<HASH>`, `/21338/<HASH>`) is answered by the one static file page (`file.html`), without changing the URL. The page reads the path. The target is the extension-less `/file`: Pages applies its HTML handling to a rewritten target too, so a target spelled `/file.html` is answered with a redirect to `/file` and the link in the path is lost. | `location ~* ^/(mainnet|testnet|devnet|[0-9]+)/[0-9a-f]{64}/?$ { try_files /file.html =404; }` |
| `/:network/:hash/ /file 200` | The same with a trailing slash. | the `/?` in the pattern above |

The nginx pattern is stricter than Pages. Pages sends any two-segment path that no earlier rule
claims (`/foo/bar`) to the file page, which then shows its "not a link" state; nginx answers 404 for
it, because the pattern only matches a network name or NetworkID and a 64-digit hash. `~*` keeps the
match case-insensitive, as the spec requires (`/TESTNET/<hash>` is accepted and canonicalised by the page).

Language pages are plain files: `/` is `index.html`, `/ru/` is `ru/index.html`; no rule is needed.

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
| `/`, `/ru/`, `/*.html`: `Cache-Control: no-cache` | Pages are revalidated on every load. | `location = / { add_header Cache-Control "no-cache" always; }` and the same for `= /ru/` and `~ \.html$` |

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
The lime accent (moss in light) marks only what is primary or verified. The theme switch is named for
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
<html lang="en">                      <!-- or lang="ru"; add data-lang-source="storage" on a page with one URL for both languages -->
<head> ... <!--@chrome:head-->        <!-- pre-paint theme script, font preloads, site.css, chrome.mjs -->
<body> <!--@chrome:header-->          <!-- skip link (targets #main), wordmark, language and theme switches -->
  <main id="main"> ... </main>
  <!--@chrome:footer-->               <!-- repository, specification, language, theme -->
```

The language comes from `<html lang>`. On `/` and `/ru/` the language links point at the equivalent
page. The file page and `404.html` have one URL for both languages and are marked
`data-lang-source="storage"`: there `chrome.mjs` takes the language from the stored choice (then the
browser's), switches the header and footer text in place on a click, stores the choice and dispatches
`xf-langchange` (`detail.lang`) on `document`, so the page's own text can follow. The theme switch sets
`data-theme` on `<html>` and the `theme` attribute of every `<xrpl-file>` that exists at that time: a
page that creates the element later must copy `document.documentElement.dataset.theme` into it.

Contrast is computed, not eyeballed: `node site/tools/contrast.mjs` rewrites
`docs/superpowers/reports/2026-11-02-site/contrast.md` from the tokens and the element palettes in the
repository README, and `test/contrast.test.mjs` fails when a required pair misses WCAG AA or the report
is out of date.

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
points inside its cube) and the 84 bytes of the link come from the read. Which ledger holds which chunk
is not reported by the reader, so the cubes are spread evenly over as many ledger pads as the manifest's
ledger range names (at most ten); the note under the cloud says so.

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
