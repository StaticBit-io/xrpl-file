# xrpl-file site

The static site for `xrpl-file.staticbit.io`: plain HTML, CSS and ES modules, no framework and no
server code. It is deployed to Cloudflare Pages; moving it to another host means copying `site/dist/`
and translating two files (see [Host files](#host-files-redirects-and-headers)).

```
site/
  src/            everything that is published as is (pages, js/, css/, _redirects, _headers)
  data/           price.json, the price table (generated, committed)
  build.mjs       assembles site/dist
  serve.mjs       dev server for site/dist
  redirects.mjs   evaluator for the _redirects subset the site uses (build check and dev server)
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
| `/:network/:hash /file.html 200` | A file link path (`/testnet/<HASH>`, `/21338/<HASH>`) is answered by the one static file page, without changing the URL. The page reads the path. | `location ~ ^/(mainnet\|testnet\|devnet\|[0-9]+)/[0-9A-Fa-f]{64}$ { try_files /file.html =404; }` |
| `/:network/:hash/ /file.html 200` | The same with a trailing slash. | `.../?$` in the pattern above |

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
- `serve.mjs` uses the same first-match evaluator and no "file exists" shortcut, so the dev server
  is as strict as the documented production rule.
- `test/redirects.test.mjs` checks the shipped `_redirects` against the real folder names.

Not verified on a live deployment: that Pages really answers a pass-through 200 rule with the asset.
Check it once on the first preview deployment: open `/js/route.mjs`, `/ru/` and
`/testnet/<HASH>`; the first must be JavaScript, the second the Russian home page, the third the
file page.

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

## The hero file and its durability

The home page's chunk mosaic reads a real file from the XRP Ledger. Until mainnet, that is the
testnet fixture picture `xrpl-file://testnet/32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59`
(1600 x 1600 WebP, 178,448 bytes). A testnet reset, or public nodes without its history, breaks it:
the mosaic then shows the cells it received and a quiet message. That is accepted at launch; to
bring the hero back, re-publish the fixture and swap the constant.

1. Re-publish. The fixture tool publishes the plan in `tools/fixtures/plan.json` to the testnet
   only, with faucet-funded throwaway accounts. Its working folder (`run/fixtures`) is git-ignored
   and holds `links.json` (key to link), `accounts.json` (the throwaway keys, never printed and
   never committed) and `state/<key>/` (resume state). The hero is the `picture` entry. The source
   media (`picture.webp`) is produced by `tools/fixtures/make-media.sh`.
   - In `run/fixtures/links.json` delete the `picture` line, and delete `run/fixtures/state/picture/`
     (a finished state would be reused and give the old link back).
   - Run, from the repository root:
     ```bash
     dotnet run --project tools/XrplFile.Fixtures -- tools/fixtures/plan.json run/fixtures/media run/fixtures
     ```
     Entries that already have a link are skipped, so only `picture` is published. A stopped run is
     resumed by running the same command again.
   - The new link is the `picture` value in `run/fixtures/links.json`.
2. Swap the constant: set `HERO_LINK` in `site/src/js/config.mjs` to that link. `HERO_ASPECT` stays
   `1` while the picture is square.
3. Check the link in a browser at `/testnet/<HASH>`, then rebuild and redeploy the site.

After the first mainnet publication, `HERO_LINK` becomes the link of a real mainnet picture; the
steps above no longer apply.

## The price table

`data/price.json` is a snapshot of what publishing costs, computed by the publisher's own estimate
(`tools/XrplFile.SitePrice`) from live mainnet fees and reserves (read only). Regenerate it with:

```bash
dotnet run --project tools/XrplFile.SitePrice -- --out site/data/price.json
```

`--node <wss url>` overrides the default node (`wss://xrplcluster.com`). The file records the time
it was taken; commit it, then rebuild.
