# Link cards for Telegram and X

Status: design, approved for planning. Stage 3 of the site
(`2026-11-02-xrpl-file-site-design.md` names it and keeps the file-page URL compatible with it).

## Why

People share file links in Telegram and X. Today every link to `https://xrpl-file.staticbit.io/<network>/<hash>`
produces the same card: the file page's static `<title>xrpl-file</title>` and its generic
description. The person who receives it cannot tell a picture from a PDF, or one file from another,
until they open it.

A link-preview robot (`TelegramBot`, `Twitterbot`) downloads the HTML once, reads `og:*` /
`twitter:*` tags from the response and runs no JavaScript. The file page learns the file's name and
content only in the visitor's browser, so the robot sees nothing of the file. The tags have to come
from the server.

## Goals

1. A shared file link shows the **file's name**, its **type and size**, and for a picture **the
   picture itself**.
2. A file that is not a picture shows its **cover** (manifest field `cover`, format spec
   `docs/spec/xrpl-file-v1.md` section 4.1) when it has one. This covers video: the card shows the
   cover, not a player.
3. The six home pages (`/`, `/ru/`, `/de/`, `/es/`, `/pt/`, `/zh/`) get a proper site card in their
   own language.
4. Everything that reaches a card is verified against the file's hash, as the element does.

## Non-goals

- A video (or audio) player inside the card: X Player Cards, Telegram `og:video`. A later stage.
- Pulling a frame out of a video, rendering a PDF page, or any other server-side media decoding.
- Resizing, cropping or re-encoding pictures for the card. The card shows the bytes from the ledger
  or nothing.
- Cards for files on networks named by a numeric NetworkID: they have no built-in node.
- Indexing, listing or searching files. The server learns about a file only when a robot asks for it.

## Anti-goals (a failure even if it works)

- **A person waits for the server.** A visitor's request for a file page must never wait for a ledger
  read or depend on the function succeeding. Whatever happens to the card machinery, the file page
  loads as it does today.
- **An unverified picture under our domain.** A card that shows bytes a node returned without
  checking them against the hash. A broken or hostile node could put any picture under
  `xrpl-file.staticbit.io`; the project's whole promise is "only the verified file is shown".
- **An error page as a card.** Any failure (node down, file not found, too large, limit hit) gives
  the site's generic card, never a 5xx, a Cloudflare error page or an empty card.
- **Cost that scales with views.** One file is read from the ledger about once, not once per share.

## What was decided, and why

### The rule "the server never reads the ledger" is lifted for cards only

The site spec said the server never fetches, caches or indexes ledger data. Cards need the server to
read a file's manifest, and for pictures and covers its bytes. The owner chose that over a generic
card. Reading stays narrow: only what a card shows, only when a robot asks.

### Measurements (2026-11-03)

The element's reader (`web/dist/1.2.0/xrpl-file-reader.mjs`), run in Node 24 on Windows against the
built-in public nodes, `process.cpuUsage()` around `reader.read()` (Windows reports CPU time in steps of
about 15.6 ms, so small values are coarse):

| File | Size | Transactions | CPU | Wall time |
|---|---|---|---|---|
| site logo, mainnet, PNG | 123 KB | 123 | 31–47 ms | 1.3–1.6 s |
| WebP, testnet | 178 KB | 179 | 47 ms | 1.5 s |
| PNG, testnet | 826 KB | 827 | 188 ms | 3.4 s |
| binary, testnet (three files) | 1 MB | 1000 | 297–359 ms | 3.3–3.5 s |
| MP4, testnet | 4 MB | 3986 | 1172 ms | 11 s |

Manifest only: 5–27 ms. Manifest plus a 43 KB cover: about 19 ms more. One SHA-256 over a whole
4 MB file stays under the timer's step.

Conclusions the design rests on:

- The cost is about 0.3 ms of CPU per transaction (parsing each node answer and decoding its memo),
  not the hash. Dropping verification would save almost nothing; it is not dropped.
- Node counts TLS and WebSocket framing as process CPU and Workers does not, so Workers will measure
  lower, but the per-transaction parsing is JavaScript in both.
- The Workers Free plan's 10 ms CPU per request cannot read even the 123 KB logo.

### Hosting: a Pages Function on Workers Paid

The cards are a Pages Function in the existing Pages project `xrpl-file-site`, deployed by the
existing `deploy-site` job. The account runs on **Workers Paid** ($5 a month): 30 s CPU per request
by default, 10 M requests and 30 M CPU-ms a month included, then $0.30 per million requests and
$0.02 per million CPU-ms. Cloudflare bills beyond the included amount rather than cutting off; there is no hard
spending cap, so the function sets its own `limits.cpu_ms` (see below).

The card logic must not depend on Cloudflare APIs in its core: the site spec plans a move to the
owner's own server, and the same core should run under Node there. Only a thin adapter (request,
cache, assets) is Cloudflare-specific.

### Request flow

```
GET /<network>/<hash>           (the existing file-page URL; the scheme does not change)
  ├─ not a known link-preview robot ─▶ the static file page, exactly as today (no ledger read)
  └─ a link-preview robot           ─▶ cached card HTML, or: read the manifest, build the card
                                        HTML (og/twitter tags + the file page's normal body), cache it

GET /og/<network>/<hash>        (the card picture; named by og:image / twitter:image)
  ─▶ cached picture, or: join or start the read, wait up to a deadline, return the bytes;
     past the deadline or on any failure ─▶ the site's card banner
```

The picture is read ahead. The HTML step already knows from the manifest whether the card picture
is the file itself, and it reads the cover synchronously when it needs one (below). For a file
picture it starts the read in the background (`waitUntil`) and caches the result, so the robot's
request for `og:image`, which follows the HTML within a second or two, usually finds it ready.
`/og/` never starts a second read of a file it is already reading, and a read it started keeps going
in the background past its own deadline so the next request hits the cache. *(Builder check: robots
drop slow requests, and a 1 MB read takes about 3.5 s of wall time.)*

1. **A person is never held up.** For a non-robot request the function hands the request straight
   to the static assets. It does no I/O first, so its own CPU is a fraction of a millisecond and no
   limit can make it fail. Function invocations still count as requests; that is accepted.
2. **The card picture is a separate URL.** If the picture cannot be produced (too large, read fails,
   CPU limit), only the picture is lost: the robot already has the name, type and size from the HTML.
   The HTML step reads the manifest only, which is cheap.
3. **The project is set to "fail open"** (Pages → Settings → Runtime → Fail open / closed), so an
   exhausted daily request allowance serves static assets as if no function existed.
4. **Every failure gives the generic card**, never an error status to the robot.

### What a card contains

| Field | Value |
|---|---|
| `og:title`, `twitter:title` | the file's name from the manifest; the link's hash shortened when the manifest has no name |
| `og:description`, `twitter:description` | type and size, and that the file is stored in the XRP Ledger (English) |
| `og:image`, `twitter:image` | `https://xrpl-file.staticbit.io/og/<network>/<hash>` |
| `twitter:card` | `summary_large_image` when the card picture is the file or its cover, `summary` otherwise |
| `og:url` | the canonical file-page URL |
| `og:site_name` | `xrpl-file` |

The picture behind `/og/<network>/<hash>`, first match wins:

1. The file itself, when its manifest type is `image/png`, `image/jpeg`, `image/webp` or `image/gif`
   and its size is at most **1 MB** (about 0.3 s of CPU by the measurements; X accepts up to 5 MB, a
   Telegram preview is small anyway).
2. The file's cover, read and checked the way the element reads it (`XrplFileReader.readCover`:
   at most 64 KB, the section 4.1 checks, and only `image/webp` or `image/jpeg` with matching magic
   bytes, as `web/src/cover.mjs` already enforces). The reader's rules are not widened here.
3. The site's card banner (below).

SVG is never a card picture: neither platform renders it there. An SVG file falls through to its
cover or the banner.

A file picture is served only when its first bytes match its manifest type (PNG, JPEG, WebP or GIF
signature). The hash proves the bytes are the published ones, not that they are a picture, and the
`/og/` response puts a manifest-chosen `Content-Type` under our domain. On a mismatch the card falls
back to the cover or the banner. Responses carry `X-Content-Type-Options: nosniff`.

The HTML step reads the cover itself (about 19 ms more) when the card picture would be the cover, so
that `twitter:card` and `og:image:width` / `og:image:height` describe the picture the robot will
actually get, and the cover is already cached when `/og/` is asked for it. When the cover fails its
checks, the card is `summary` with the banner.

The manifest's name and type are untrusted input: they are escaped for HTML attributes and the name
is cut to a sane length before it goes into a tag.

Language of a file card: English. The file URL has no language prefix and robots do not send one.

### Home pages

Static tags written by the site build into each language's page: title and description in that
page's language, one shared card banner. No function involved.

The card banner exists: `site/src/og-banner.png`, 1200 × 630, the wordmark beside the hero's point
cloud, drawn by `site/tools/og-banner.mjs`; the home pages and the static file page already name it
(`cardTags` in `site/chrome-markup.mjs`). It is also the fallback picture of every file card.

### Caching

Content addressed by hash never changes, so successes are cached for a year (`immutable`) in
Cloudflare's cache, keyed by URL: both the card HTML and the card picture. Failures that may be
temporary (node unreachable, node lacks history, CPU limit) are cached for a few minutes only, so a
node outage does not freeze a generic card for a year. A link whose manifest is definitely not on
that network may be cached longer; the builder picks the figure.

### Nodes

The reader's built-in endpoints (`ENDPOINTS` in the reader: `mainnet` xrplcluster.com and
s2.ripple.com, `testnet`, `devnet`). Workers support outbound `new WebSocket("wss://…")`, which is what
`LedgerClient` uses. The reader's existing fallback across endpoints and its error codes apply.

### Limits the function sets on itself

- `limits.cpu_ms` well below the 30 s default (a few seconds), so that no single request can run up
  a large bill. A request that hits it gets Cloudflare's 1102; for the picture URL that means a card
  without a picture, which rule 2 already accepts.
- The 1 MB picture ceiling and the 64 KB cover ceiling above bound how much one card can read.
- Wall-clock deadlines: the HTML step gives the manifest (and cover) read a few seconds and answers
  with the generic card when it runs out; `/og/` waits a few seconds for the picture before it
  answers with the banner. Both failures are cached briefly, as above.
- `/og/` answers anyone, not only robots: a user agent is trivial to fake, so gating it buys nothing.
  The exposure is accepted because it is bounded: a made-up hash costs one manifest lookup, a real
  picture is read once per Cloudflare location and then served from cache, and pictures above
  1 MB are never read. Total cost is bounded by the number of real pictures, not by the number of
  requests. A rate limit is not part of this stage.

### If the account ever returns to the Workers Free plan

Nothing breaks for people. Robots lose cards gradually:

- 10 ms CPU per request: the picture URL fails (1102) for every real picture, so cards show without
  a picture. The HTML step (manifest only, 5–27 ms in Node) may or may not fit; when it does not,
  the robot gets an error and no card.
- 100,000 requests a day per account, shared with everything else: past it, fail-open serves the
  static file page, so robots see the generic site card until midnight UTC.

## Left to the builder

- Which robots count as link-preview robots. At least `TelegramBot` and `Twitterbot`; adding the
  other common unfurlers (Discord, Slack, WhatsApp, Facebook, LinkedIn) is the builder's call, as
  long as a person's browser never matches.
- How the function and the existing `_redirects` file-page rule fit together (`_routes.json`,
  `functions/` placement, `env.ASSETS.fetch`). The `deploy-site` job runs
  `wrangler pages deploy site/dist` from the repository root, and wrangler looks for `functions/`
  relative to its working directory; the builder adapts the job as needed.
- The exact wording of descriptions (use the `writing-site-copy` skill: no em dash, plain words).
- The `limits.cpu_ms` figure, the two deadlines and the failure cache lifetimes.
- How the card HTML is built: the static file page with tags injected into its `<head>`, or a
  minimal page; either is fine as long as a robot that follows through gets a working file page.
- Tests: the card logic against a fake ledger source, as the web tests already do for the reader;
  a check with a robot user agent against `wrangler pages dev`.

## Owner steps

- Switch the Cloudflare account to Workers Paid before the function ships.
- Set the Pages project to fail open.
- After deploy: send a file link in Telegram (and @WebpageBot to refresh a cached preview) and post
  one on X, for a picture, a video with a cover, and a file with neither.
