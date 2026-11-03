# Deploy note: xrpl-file.staticbit.io (Task 10, step 1)

Status: **deployed** 2026-11-03 from `main` @ `e462eed` with element `1.2.0` (published the same
day), custom domain attached. The steps below were written for `1.1.0`; the order holds for every
element version.
Every step below publishes something publicly; a merge into `main` is one of them, since it deploys
the site, so `main` moves only on the owner's explicit command.

## What a deploy does

- A Cloudflare Pages deploy makes the site public at `https://xrpl-file-site.pages.dev` immediately,
  and at `https://xrpl-file.staticbit.io` once the custom domain is attached.
- `npm publish` makes the element package public and permanent for that version: a published
  version cannot be replaced, only deprecated.

## Order of operations

The site and the package depend on each other in one direction: the site's main snippet loads the
element from jsDelivr, which serves the npm package. A site deployed before the package exists shows
a snippet that answers 404.

1. **Owner decision: the package name.** `xrpl-file-element` (default in `site/src/js/config.mjs`)
   or `@staticbit/xrpl-file-element`. Change `ELEMENT_PACKAGE` if needed.
2. Rebuild and run the tests: `node web/build.mjs && node web/pack.mjs && node site/build.mjs`, then
   `cd web && npm test` and `node --test site/test/*.test.mjs`. Version `1.1.0` has not been released,
   and the package README cannot be corrected after publishing, so read `web/npm/1.1.0/README.md` once.
3. **Owner decision: publish the package first (or in the same sitting as the deploy), never
   after it.** `npm publish web/npm/1.1.0 --access public` is permanent for that version. Verify
   the jsDelivr URL as described under "npm package" below before step 4.
4. Deploy the site (Cloudflare Pages below). The snippet loads the element from jsDelivr, so it works
   only after step 3; the site's own copy under `/element/1.1.0/` serves its pages.
5. Attach the custom domain (DNS below), then run the checks after the deploy.
6. Once `1.1.0` is released, `web/integrity/1.1.0.json` is its permanent record: a later change to
   `web/src` needs a new version in `web/package.json`, or the web test fails.

## Cloudflare Pages

Work lands on `dev`; a merge into `main` deploys the site. The `deploy-site` job in
`.github/workflows/test.yml` runs on every push to `main` after the `unit` job passes (it runs the
web and site suites and both builds), builds `site/dist` and uploads it with
`wrangler pages deploy` (direct upload; the Pages project is not connected to Git). Its first run
creates the project.

| Setting | Value |
|---|---|
| Project name | `xrpl-file-site` |
| Production branch | `main` |
| Deployed by | the `deploy-site` job, wrangler `4.151.1` |
| Build output directory | `site/dist` (named in `site/wrangler.toml`; the job deploys from `site/`) |
| Function | `site/functions/[[path]].js`, file link cards, on `/mainnet/*`, `/testnet/*`, `/devnet/*`, `/og/*` |
| Custom domain | `xrpl-file.staticbit.io` |

The job needs two repository secrets, set once by the owner:

| Secret | Value |
|---|---|
| `CLOUDFLARE_API_TOKEN` | an API token of the account that holds the `staticbit.io` zone, with the account permission **Cloudflare Pages: Edit** |
| `CLOUDFLARE_ACCOUNT_ID` | that account's ID (the dashboard's account home, or the URL `dash.cloudflare.com/<id>`) |

Without them the job fails on its first step and names the missing secrets.

Notes:

- `web/build.mjs` refuses to overwrite an existing `web/dist/<version>` with different content. A
  clean runner starts without `web/dist`, so no `--force` is needed there.
- A manual deploy, for example while CI is unavailable: `node web/build.mjs && node site/build.mjs`
  locally, then from `site/`: `npx wrangler@4.151.1 pages deploy --branch main` (the project
  name, the output directory and the function come from `site/wrangler.toml` and `site/functions/`).
- `_redirects` and `_headers` are in `site/src` and are copied into `site/dist`; `site/README.md`
  explains each rule and its nginx equivalent.

## File link cards: owner steps

The function reads the ledger for robots only (`site/README.md`, "File link cards"). Before the merge
into `main` that first ships it:

1. Switch the Cloudflare account to **Workers Paid**. On the Free plan the function has 10 ms of CPU
   per request, which reads a manifest at best and never a picture; people are not affected either way.
2. In the Pages project, **Settings → Runtime → Fail open / closed: Fail open**, so a spent daily
   request allowance serves the static site as if there were no function.

Checks after that deploy:

1. The three `curl` lines from `site/README.md` against `https://xrpl-file.staticbit.io`: the
   logo's name in `og:title`, `/og/mainnet/…` answering `200` with `content-type: image/png`, and `1`
   for a browser's user agent.
2. In Telegram, send a link to a picture, a video with a cover and a file with neither; the first two
   show their picture, the third the banner, all with the file's name, type and size. A link sent
   before the deploy keeps its old preview: refresh it through @WebpageBot.
3. Post one of the links on X.

## DNS

- `staticbit.io` is on Cloudflare DNS: its nameservers are `athena.ns.cloudflare.com` and
  `salvador.ns.cloudflare.com` (checked 2026-11-02 with a public DNS lookup).
- `xrpl-file.staticbit.io` does not exist yet. Attaching the custom domain in the Pages project
  creates the proxied `CNAME xrpl-file → xrpl-file-site.pages.dev` record in the same account; no
  manual DNS edit is needed if the zone and the Pages project are in one Cloudflare account.
- The Cloudflare tools in this session were not authorised, so the account itself was not checked.

## Checks after the deploy (from the plan)

1. `https://xrpl-file.staticbit.io/` and `/ru/`, `/de/`, `/es/`, `/pt/`, `/zh/` answer 200.
2. `curl -sI https://xrpl-file.staticbit.io/mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5`
   answers 200 with no `Location` header (the file page is rewritten, not redirected).
3. `curl -sI https://xrpl-file.staticbit.io/js/route.mjs` answers 200 with a JavaScript type (the
   pass-through rules win over the file-page rule).
4. `curl -sI https://xrpl-file.staticbit.io/element/<version>/xrpl-file-element.js` shows
   `access-control-allow-origin: *` and `cache-control: public, max-age=31536000, immutable`. The
   site holds only the version in `web/package.json`; older versions answer 404 there and stay on
   jsDelivr.
5. `/nope` answers 404 with the site's 404 page.
6. The snippet (jsDelivr) works from a page on another origin (for example JSFiddle) with its
   `integrity` attribute.
7. `curl -s https://xrpl-file.staticbit.io/ | grep -c email-protection` prints `0`. The
   `staticbit.io` zone has Email Address Obfuscation on, which turns `xrpl-file-element@<version>/…`
   in the snippet into `[email protected]` unless `site/build.mjs` fences it with `email_off`
   comments. Only the custom domain shows this; `pages.dev` is outside the zone.

## npm package (step 3 of the order above)

1. Rebuild with the chosen package name: `node web/build.mjs && node web/pack.mjs && node site/build.mjs`.
2. Publish: `npm publish web/npm/1.1.0 --access public` (logged in as the owner's npm account).
3. Verify `https://cdn.jsdelivr.net/npm/<package>@1.1.0/xrpl-file-element.js`: served as
   JavaScript, `access-control-allow-origin: *`, and its SHA-384 equals the `integrity` in the
   site's snippet.

Deploying the site before this step leaves its main snippet (jsDelivr) answering 404; the alternative
URL on the site works as soon as the site is deployed.
