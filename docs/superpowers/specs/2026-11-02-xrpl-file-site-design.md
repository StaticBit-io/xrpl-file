# xrpl-file project site — design

Date: 2026-11-02. Status: for the owner's review.

## Why

xrpl-file stores a file inside the XRP Ledger itself — chunks in transaction memos, a manifest
that fingerprints them — and reads it back verified, from a link such as
`xrpl-file://testnet/<HASH>`. The project needs a public home at `xrpl-file.staticbit.io` for two
reasons:

1. **Links have to open somewhere.** People will share file links in Telegram, X and chats. A
   visitor who taps one must land on a page that shows the file, not on an explanation of a URL
   scheme their browser cannot open.
2. **The project has to explain itself** to the people it is for.

The primary audience is **projects that keep token metadata off the ledger** — NFT, MPT, DID and
credential issuers whose `URI` points at IPFS, their own server or S3. Their pain is that the link
dies: pinning stops being paid for, a server is shut down, and the token is left without its
picture. The argument the site makes: with xrpl-file the metadata lives in the same ledger as the
token, lives as long as it does, and is verified against its fingerprint without trusting anyone's
server. A link is 84 bytes and fits an NFT `URI` (256 bytes). Everyone else is a secondary
audience; the first screen is enough for them.

## Goals

- A visitor arriving from a shared link sees the file at once, can download it, and can learn in
  two sentences what xrpl-file is.
- A visitor arriving at the home page understands what the project is, how it works, what it
  costs, and where the developer material is.
- Anyone can paste an `xrpl-file://` link and see what it points to.
- The site hosts the `<xrpl-file>` web component under a stable, versioned URL, so a site owner
  can embed it with a two-line snippet.
- English and Russian, both complete.
- Light and dark themes with a switch.

## Non-goals

- No accounts, sign-in or personal area.
- No uploading or publishing through the site. Publishing is done by the local publisher
  (the desktop app); the site only reads.
- No comments, likes, feeds of "related files" or other social features.
- No server-side knowledge of files: the server never fetches, caches or indexes ledger data.
  The visitor's browser reads the ledger.
- Telegram/X link cards (Open Graph per file) are **stage 3**, a separate design. This design only
  keeps the file-page URL compatible with them (see "File page").

## Anti-goals (would make it a failure even if it works)

- Looking like a generic crypto site: gradients, glowing coins, stock photos of servers or
  "blockchain" illustrations.
- Photographs at all. Every picture on the site is made of real data.
- Decorative fake data. Every number shown (sizes, chunk counts, ledgers, prices) is real or
  explicitly labelled as an example computed by the real estimate.
- An animation that pretends: the hero mosaic shows only what was actually read and verified.
- Hiding that ledger data is public and permanent.

## Pages

### Home — `/` (English), `/ru/` (Russian)

Order of sections:

1. **Hero.** Headline and one or two lines about the project on one side; the **chunk mosaic** (see
   "Signature") on the other. Below or beside it: a field "Check a link" (paste an
   `xrpl-file://…` link → goes to its file page) and the publisher download button.
2. **The problem.** Off-ledger metadata links die: IPFS without pinning, closed servers.
3. **How it works.** A file is cut into 1000-byte chunks, each written as a memo of a transaction;
   1024 chunks form a block whose hash goes into a block list; the manifest transaction holds the
   root fingerprint; the link is the manifest's hash. A reader assembles the file itself and checks
   every block. Three or four steps, drawn in the same cell motif as the mosaic.
4. **Price.** What it is made of — account reserve and transaction fees, about one transaction per
   kilobyte — and one or two worked examples ("a 50 KB picture costs about X XRP on mainnet"). The
   figures come from the publisher's own estimate (`app/XrplFile.App.Core/EstimateModel.cs` and the
   cost model behind it) with the network's current reserve and base fee, stated with the date they
   were taken. No figure is invented.
   *(Builder's call, settled in review:)* "Cost" means the fees, which are spent. The reserve the
   publisher locks for the run is shown separately as money that is held during publication and
   returned when the throwaway account is deleted, minus what the network keeps for that deletion.
   The figures are produced by a small .NET tool in this repository that runs the publisher's real
   estimate for a few file sizes and writes `site/data/price.json` with the network values and the
   date. The site reads that file. The tool is re-run by hand when reserves or fees change. No cost
   model is ported to JavaScript.
5. **Permanent and public.** What is written cannot be deleted, and everyone can read it.
6. **For developers.** Links to the specification (`docs/spec/xrpl-file-v1.md` on GitHub), the
   repository, the .NET library and the browser reader; the embed snippet (script + element) with a
   copy button; a short note on attributes (`width`, `height`, `loading`, `theme`, `node`) linking
   to the README.
7. **Footer.** Repository, specification, language switch, theme switch.

### File page — `/<network>/<HASH>`

Example: `/testnet/32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59`.

- The path is derived from the link mechanically: `xrpl-file://<network>/<hash>` ↔
  `/<network>/<hash>`. Network is case-insensitive (canonical lower case), the hash
  case-insensitive (canonical upper case); non-canonical paths redirect to the canonical one. A
  path that is not a valid link shows the home page's "Check a link" field with an error.
- One static page serves every file (a rewrite rule maps `/<network>/<hash>` to it); the page reads
  the path and renders `<xrpl-file src="xrpl-file://<network>/<hash>">`.
- On the page:
  - the file itself, large, full content width, through the element (loader, "✓ verified");
  - a prominent **Download** button under it, even when the file is displayed;
  - name, size, network, and the `xrpl-file://` link with a copy button;
  - a two- or three-line "How this works" note (the file is written in the XRP Ledger in parts;
    your browser assembled it and checked it against its fingerprint; no server was involved) with
    a link to the home page;
  - the site header with the link to the home page.
- Errors (not found, node unreachable, corrupt) are shown by the element; the page stays the same.
- *(Builder's call, settled in review:)* the page's name, size, link and Download button come from
  the element, not from a second read. The element gains a small public API, backward compatible:
  its `load` event carries `detail` with `name`, `type`, `size`, `network` and `link`, and it gets a
  `save()` method that runs the same verified download as its own button. It also gets a way to hide
  its hover caption (for example an attribute `caption="off"`), which the file page uses so the copy
  and download controls are not duplicated. This ships as a new element version (see "Hosting").
- Language: one page for both languages; its own strings follow the language switch (default from
  the browser), the URL does not change. Stage 3 will make the same URL return Open Graph tags to
  link-preview bots, so the URL scheme must not change.

### Not found — `404`

Plain page in the site's look, link home, "Check a link" field.

## Signature: the chunk mosaic

The hero shows a real xrpl-file picture being read from the ledger, drawn as a mosaic of its chunks.
It must be honest: an image cannot be decoded from part of its bytes, and the element's rule is to
show only verified content.

1. **Reading.** One cell per chunk (1000 bytes). A cell lights up when its chunk actually arrives
   from the ledger, in the order it arrives. Inside the cell, a texture derived from that chunk's own
   bytes (e.g. brightness or tone from their values) — the visitor sees real data, not a placeholder.
2. **Verifying.** When a block's hash matches, its cells are marked verified — visible as a wave
   across the mosaic.
3. **Developing.** When every block is verified, the cells turn over and together form the picture
   itself; under the mosaic: "✓ verified · N chunks · ledgers X–Y" and a link to that file's page.

Details:
- A 180 KB picture is about 180 cells: coarse but legible. The manifest carries no dimensions, so
  the picture's aspect ratio is a config constant next to its link (the fixture is square, so about
  13 × 14 cells).
- *(Builder's call, settled in review:)* no persistent cache. Every page load reads the file from
  the ledger again; there is no second storage layer for verified bytes. A visitor who has already
  seen the animation in this browser session (`sessionStorage` flag, wrapped in try/catch) gets it
  played fast. `prefers-reduced-motion`: the cells fill without motion and the picture appears
  when verified.
- If the read fails (node down, ledger history unavailable), the mosaic shows the cells it got and a
  quiet message; the rest of the page is unaffected.
- **The file.** Until mainnet: the testnet fixture picture
  `xrpl-file://testnet/32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59`
  (1600×1600 WebP, 178,448 bytes). After the first mainnet publication: a real NFT picture on
  mainnet. The link is one constant in the site's config.
- **The owner's decision on durability:** the site launches with the testnet fixture and accepts
  that a testnet reset (or nodes without its history) breaks the hero until the fixture is
  re-published and the constant updated. The hero's failure state (cells read so far plus a quiet
  message) is therefore a real launch path, not an edge case, and must look deliberate.
  `site/README.md` documents how to re-publish the fixture and swap the link.
- **Reader change it needs.** `XrplFileReader.block()` reports chunk arrival through
  `onChunk()` without arguments (`web/src/reader.mjs`). The mosaic needs the chunk index and its
  bytes, and block verification events. Extend the callback in a backward-compatible way (e.g.
  `onChunk({ index, bytes })`, plus a per-block "verified" notification); the element keeps
  working unchanged. The mosaic uses the same reader and the same verification as the element — it
  is not a second implementation.

The cell is the site's recurring motif: the "How it works" drawing, section dividers and small
accents reuse the grid of cells.

## Look

- Built with the **hallmark** skill (`github.com/nutlope/hallmark`) in its **custom** mode: no
  catalogue theme. Fourteen catalogue themes were shown and rejected; what the owner kept as
  "not bad" (Furrow, Cobalt, Vantage) shares one trait — a generated picture of structure instead
  of a photograph, set in a calm grotesque. That is the direction.
- Typography: a calm grotesque sans for everything; a monospace only for real values (hashes,
  ledgers, sizes, code). No serif display faces, no condensed capitals, no all-monospace text.
- **Light theme is the default, not too bright**: no pure white ground — a soft off-white or light
  grey paper with dark grey ink. **Dark theme** via a switch. The choice is remembered per browser
  (local storage, wrapped in try/catch); first visit is light regardless of the OS setting.
- One accent colour, used sparingly. The `<xrpl-file>` element follows the site's theme through its
  `theme` attribute (`light`/`dark`) or its CSS variables.
- WCAG 2.2 AA in both themes (text 4.5:1, large text and UI 3:1); keyboard reachable; visible focus.

## Languages

- English at `/`, Russian at `/ru/`; a switch in the header keeps the visitor on the equivalent page.
- Both languages complete; no machine-translated filler. Copy written for people, not about
  internals (use the `writing-site-copy` skill).
- The file page is one URL for both languages (see above).

## Publisher download

The desktop publisher is a Windows MAUI app and is larger than Cloudflare Pages' 25 MB per-file
limit, so it is distributed through **GitHub Releases** of this repository. That needs the
repository public and a first release; until then the "Download the publisher" button is visible but
disabled, marked "soon". Switching it on is a one-line config change (the release URL).

## Hosting and build

- Source in `site/` of this repository. Plain static HTML/CSS/JS — no framework, no server
  rendering — so moving from Cloudflare Pages to the owner's own server later is copying a folder.
- Deployed to **Cloudflare Pages** at `xrpl-file.staticbit.io`.
- Only two host-specific files: rewrites/redirects (`_redirects`) and headers (`_headers`). Their
  nginx equivalents are documented next to them in `site/README.md`.
- The `<xrpl-file>` build (`web/dist/<version>/`) is published under
  `/element/<version>/xrpl-file-element.js` (+ `mp4box.min.js`, licence, `integrity.json`).
  `/element/*` is served with `Access-Control-Allow-Origin: *` (mp4box is fetched cross-origin
  from embedding sites) and long-lived immutable caching; HTML pages are not cached long.
- **The owner's decision on the embed URL (2026-11-02):** the snippet that people copy to their
  sites points at **jsDelivr serving an npm package** of the element, not at this domain — an
  embed must not die with `staticbit.io`. Example:
  `<script src="https://cdn.jsdelivr.net/npm/<package>@1.1.0/xrpl-file-element.js" integrity="sha384-…" crossorigin="anonymous" defer></script>`.
  The script is a classic script (never `type="module"`: the element finds `mp4box.min.js` beside
  its own `document.currentScript`). The site keeps its own copy under `/element/<version>/` for
  its pages and as a documented alternative; both URLs carry the same file, so the same
  `integrity`. The package name is the owner's choice (free on npm: `xrpl-file-element`,
  `@staticbit/xrpl-file-element`); the package holds exactly `web/dist/<version>/` plus a
  `package.json`, `README.md` and licence. Publishing to npm is irreversible per version and done
  only on the owner's command; until then the site shows the jsDelivr snippet with a note that the
  package is not published yet, or falls back to the site's own URL — whichever the owner picks at
  publish time.
- The site itself loads the element and the reader from the same build — never a second copy.
- A build step copies `web/dist/<version>/` into the site output; the site's own pages are not
  minified beyond what keeps them readable.
- No third-party scripts, no trackers, no analytics.

## Dependencies and order

- The element's light theme (`theme` attribute and colour variables) is being built in a separate
  branch (`claude/element-light-theme-…`). *(Builder's call, settled in review:)* the site's
  implementation starts after that branch is merged into `main`; the element API additions above
  (load detail, `save()`, caption off) are made on top of it, and together they ship as element
  version **1.1.0**, the version the site's snippet pins. The site never publishes 1.0.0 as its
  snippet.
- The `format` manifest member points at `https://github.com/StaticBit-io/xrpl-file`; the
  repository must be public before the first mainnet publication — independent of the site, but the
  site's "For developers" links assume it.

## Left to the builder

- The exact palette, accent and fonts within the rules under "Look".
- Section layouts, spacing, the "How it works" drawing, the cell styling and the mosaic's timing.
- Copy in both languages, within the content listed per section.
- The exact shape of the reader callback extension, as long as it is backward compatible.
- Tests: unit tests for link ↔ path mapping and canonicalisation, the mosaic's state machine and
  the theme/language persistence; a browser check of both pages in both themes and both languages
  at phone and desktop widths.

## Amendment 2026-11-02: Vantage, dark by default, six languages, the StaticBit logo

The owner compared three sketches of this site in hallmark catalogue themes (`run/sketches/vantage`,
`furrow`, `cobalt`, git-ignored) against the custom build and chose **Vantage**. This amendment
overrides the sections it names; everything else stands.

- **Look (overrides "Look").** hallmark's **Vantage** theme replaces custom mode: blue-black ground,
  pale steel ink, Geologica, hairline frames with corner ticks, one lime signal. The sketch in
  `run/sketches/vantage/` (`site.css`, `notes.md`) is the reference for tokens and treatment.
- **Default theme is dark (overrides "Light theme is the default").** First visit is dark regardless
  of the OS setting; the switch offers light. The light variant follows the sketch's notes
  ("survey sheet in daylight": cool pale grey ground, never white; the cloud inverts; lime deepens
  to moss for contrast). WCAG 2.2 AA in both.
- **Hero (overrides "Signature: the chunk mosaic").** Vantage's own signature: a point cloud seen at
  an oblique angle — ledgers as pads of ground, the hero file's chunks as towers of point cubes (one
  cube per 1000-byte chunk, its inside scattered by that chunk's real bytes), the manifest as one cube
  a few ledgers later, a sweep, two callouts ("N chunks"; "84 bytes, its hash is the link").
  *Builder's call:* it stays honest the way the mosaic was — the cubes are driven by the real read
  through the reader hooks (a cube lights when its chunk actually arrives; a block's cubes turn lime
  when the block is verified; the sweep marks verification), so the mosaic's state machine
  (`mosaic-state.mjs`) is reused under a new renderer. When everything is verified, the file itself
  appears beside the cloud (a small `<xrpl-file>` card with "✓ verified") linking to its file page.
  Failure and reduced-motion behaviour as before.
- **Hero file (overrides the fixture constant).** The StaticBit logo, published to testnet as a new
  fixture by the owner's file; `HERO_LINK` and `HERO_ASPECT` change to it. Until it is published the
  current fixture stays.
- **Languages (overrides "Languages").** Six: English `/`, Russian `/ru/`, German `/de/`, Spanish
  `/es/`, Portuguese `/pt/`, Chinese (Simplified) `/zh/`, chosen from a dropdown in the header listing
  each language in its own name (English, Русский, Deutsch, Español, Português, 中文). First visit:
  the browser's language if it is one of the six, else English; the choice is remembered. Every page
  and every string exists in all six. English and Russian are written by hand; German, Spanish,
  Portuguese and Chinese are translated from English and marked in `site/README.md` as awaiting a
  native speaker's review. `<html lang>` and `hreflang` alternates on every page.
