# Deploy note: xrpl-file.staticbit.io (Task 10, step 1)

Status: prepared, **not deployed**. The package `xrpl-file-element@1.1.0` is published (2026-11-02).
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
4. Deploy the site (Cloudflare Pages below). The site's alternative URL under `/element/1.1.0/`
   works as soon as the site is up; the main snippet works only after step 3.
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
| Build output directory | `site/dist` |
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
  locally, then `npx wrangler@4.151.1 pages deploy site/dist --project-name xrpl-file-site --branch main`.
- `_redirects` and `_headers` are in `site/src` and are copied into `site/dist`; `site/README.md`
  explains each rule and its nginx equivalent.

## DNS

- `staticbit.io` is on Cloudflare DNS: its nameservers are `athena.ns.cloudflare.com` and
  `salvador.ns.cloudflare.com` (checked 2026-11-02 with a public DNS lookup).
- `xrpl-file.staticbit.io` does not exist yet. Attaching the custom domain in the Pages project
  creates the proxied `CNAME xrpl-file → xrpl-file-site.pages.dev` record in the same account; no
  manual DNS edit is needed if the zone and the Pages project are in one Cloudflare account.
- The Cloudflare tools in this session were not authorised, so the account itself was not checked.

## Checks after the deploy (from the plan)

1. `https://xrpl-file.staticbit.io/` and `/ru/`, `/de/`, `/es/`, `/pt/`, `/zh/` answer 200.
2. `curl -sI https://xrpl-file.staticbit.io/testnet/2AEB485BB0FAECA2DF4B60F196D5199042288FEB124ED8D95F4313CF06200DB4`
   answers 200 with no `Location` header (the file page is rewritten, not redirected).
3. `curl -sI https://xrpl-file.staticbit.io/js/route.mjs` answers 200 with a JavaScript type (the
   pass-through rules win over the file-page rule).
4. `curl -sI https://xrpl-file.staticbit.io/element/1.1.0/xrpl-file-element.js` shows
   `access-control-allow-origin: *` and `cache-control: public, max-age=31536000, immutable`.
5. `/nope` answers 404 with the site's 404 page.
6. The snippet's alternative URL (`https://xrpl-file.staticbit.io/element/1.1.0/…`) works from a
   page on another origin (for example JSFiddle) with its `integrity` attribute.

## npm package (step 3 of the order above)

1. Rebuild with the chosen package name: `node web/build.mjs && node web/pack.mjs && node site/build.mjs`.
2. Publish: `npm publish web/npm/1.1.0 --access public` (logged in as the owner's npm account).
3. Verify `https://cdn.jsdelivr.net/npm/<package>@1.1.0/xrpl-file-element.js`: served as
   JavaScript, `access-control-allow-origin: *`, and its SHA-384 equals the `integrity` in the
   site's snippet.

Deploying the site before this step leaves its main snippet (jsDelivr) answering 404; the alternative
URL on the site works as soon as the site is deployed.
