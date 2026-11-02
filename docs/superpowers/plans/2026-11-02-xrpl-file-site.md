# xrpl-file Project Site Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the static project site for `xrpl-file.staticbit.io` in `site/`, with its home page, file page and 404 page in English and Russian. It has light and dark themes, a chunk-mosaic hero that reads a real file from the ledger, and the `<xrpl-file>` element 1.1.0 hosted under `/element/1.1.0/`.

**Architecture:**
- Plain static HTML/CSS/ES modules. `site/build.mjs` assembles `site/dist/` from `site/src/`, the element build `web/dist/1.1.0/` and `site/data/price.json`.
- The browser does all ledger reading through the repository's own reader. It is shipped as an ESM bundle beside the element, so nothing is copied twice.
- Cloudflare Pages serves `site/dist/`. Only `_redirects` and `_headers` are host-specific.

**Tech Stack:**
- Site: Node ≥ 22 (`node --test`), vanilla JS modules, CSS custom properties, Canvas 2D for the mosaic.
- Price tool: .NET 10 console app with xUnit v3 tests, run through `dotnet run`.
- Design: the hallmark skill, custom mode (source at `github.com/nutlope/hallmark`).

**Spec:** `docs/superpowers/specs/2026-11-02-xrpl-file-site-design.md`. Read it before any task. It is the authority, and this plan argues from it.

## Precondition

- The element light-theme branch (`claude/element-light-theme-…`, session "element-theme") is merged into `main` before Task 2 starts. That branch adds the `theme` attribute (`dark` | `light` | `auto`) and the `--xrpl-file-*` colour variables. This plan builds on them and does not re-implement them.
- Task 1 may start before the merge.

## Global Constraints

- No third-party scripts, no trackers, no analytics. No photographs anywhere; every picture is made of real data.
- Every number shown is real or an example computed by the real estimate. No invented figures.
- The light theme is the default, **not too bright**: no pure white ground; use a soft off-white or light grey paper with dark grey ink. The dark theme is reached through a switch.
- The first visit is light regardless of the OS colour scheme. The theme choice is remembered in `localStorage`, and every storage access is wrapped in try/catch.
- WCAG 2.2 AA in both themes: text 4.5:1, large text and non-text UI 3:1. Everything is keyboard reachable and focus is visible.
- Typography: one calm grotesque sans; monospace only for real values (hashes, ledgers, sizes, code). No serif display faces, no condensed capitals, no all-monospace text.
- English lives at `/`, Russian at `/ru/`. Both are complete; copy is written for people (skill `writing-site-copy`).
- The file page lives at `/<network>/<HASH>`: one URL for both languages, canonical lower-case network and upper-case hash.
- Site source lives in `site/`; the build output `site/dist/` is git-ignored.
- `/element/<version>/*` is served with `Access-Control-Allow-Origin: *` and `Cache-Control: public, max-age=31536000, immutable`. HTML pages are not cached long.
- The embed snippet pins element `1.1.0` with `integrity` and `crossorigin="anonymous"`.
- Hero file constant: `xrpl-file://testnet/32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59`, aspect ratio `1:1` (1600×1600 WebP, 178,448 bytes).
- Publisher download: GitHub Releases. Until a release URL is configured, the button is visible, disabled and marked "soon".
- Cloudflare Pages limits each file to 25 MB.
- Commits use conventional format, in English, with no AI attribution lines of any kind.
- Never push, deploy or change DNS without the owner's explicit command.
- Tests:
  - web: `cd web && npm test`;
  - site: `node --test site/test/*.test.mjs`;
  - .NET: `dotnet run --project <tests csproj>` (xunit.v3 on .NET 10: `dotnet test` does not work).

## Review Focus

1. **A file link pasted or opened with odd casing or spaces** (`XRPL-FILE://TestNet/abc…`, a trailing newline from a chat app) must open the canonical file page, not the "not a link" error. *Pinned in Task 4.*
2. **`localStorage` that throws** (private mode, blocked site data) must leave the site rendering in light with working switches for the current page. *Pinned in Task 4.*
3. **Download on the file page clicked before the file has loaded, or after it failed,** must neither throw an uncaught error nor show a false "Saved.". The button stays disabled until `load`, and `save()` on an errored element rejects with the element's error. *Pinned in Tasks 2 and 8.*
4. **The hero when the node lacks the file's history** (a testnet reset) must settle within a bounded time into the deliberate failure state, never spin forever. *Pinned in Task 6.*
5. **A network written as a decimal NetworkID** (`xrpl-file://21338/…`) must round-trip through the path, `/21338/<HASH>`, unchanged. *Pinned in Task 4.*

---

### Task 1: Reader hooks for chunks and verified blocks

**Files:**
- Modify: `web/src/reader.mjs` (`read()` at line 26, `block()` at line 174)
- Test: `web/test/reader-hooks.test.mjs`

**Interfaces:**
- Produces:
  - `XrplFileReader#read(link, onProgress = () => {}, hooks = {})`;
  - `hooks.onChunk?.({ index: number /* absolute chunk index */, bytes: Uint8Array })`, called once per accepted chunk, in arrival order;
  - `hooks.onBlockVerified?.({ block: number, firstChunk: number, lastChunk: number })`, called after the block hash matches.
- `onProgress` keeps its current shape `{ chunksDone, chunksTotal }`. The element is unchanged.

- [ ] **Step 1: Write failing tests.** Use the existing test helpers and vector stream (`web/src/vector-stream.mjs`, the same fixtures the reader tests use) to read a two-block vector file:
  - `onChunk receives every chunk index once with its bytes`: the set of indices equals `0..chunks-1`, and the concatenated bytes ordered by index equal the file bytes.
  - `onBlockVerified fires once per block after all its chunks`: the events equal `[{block:0, firstChunk:0, lastChunk:1023}, {block:1, firstChunk:1024, lastChunk:<last>}]`, and every `onChunk` index of a block precedes its `onBlockVerified`.
  - `a corrupt block fires no onBlockVerified for it`: given a tampered vector, `read` rejects with `BlockMismatch`, and no event names that block.
  - `read without hooks behaves as before`: the existing call signature still resolves to the same bytes.
- [ ] **Step 2: Run** `cd web && node --test test/reader-hooks.test.mjs`. Expected: FAIL (hooks never called).
- [ ] **Step 3: Implement.** Thread `hooks` from `read()` into `block()`. `block(record, entry, onChunk)` passes `{ index, bytes: decoded }` to its callback, and `read()` calls both `onProgress` and `hooks.onChunk`. Call `hooks.onBlockVerified` right after the hash comparison succeeds. Existing callers of `block()` that pass a zero-argument callback keep working.
- [ ] **Step 4: Run** `cd web && npm test`. Expected: all pass (571 plus the new tests).
- [ ] **Step 5: Commit** `feat(web): report each chunk and each verified block to the reader's caller`.

### Task 2: Element 1.1.0 — load detail, `save()`, `caption="off"`, reader bundle

**Files:**
- Modify:
  - `web/src/element/element.mjs` (`observedAttributes` line 64, `#emit` calls for `load`, `#save` line 706, `#renderCaption` ~line 656);
  - `web/build.mjs`;
  - `web/package.json` (version);
  - `README.md` ("Embed a file").
- Test: `web/test/element-api.test.mjs`, `web/test/build.test.mjs` (extend if present)

**Interfaces:**
- Consumes: the `theme` attribute from the merged light-theme branch (unchanged).
- Produces:
  - **`load` event `detail`:** `{ name: string|null, type: string, size: number, network: string, link: string }`. `link` is canonical and formatted by `formatLink`. Fired on every existing `load` emission.
  - **`element.save(): Promise<'saved'|'started'|'cancelled'>`.** Same path as the element's own Download button. It rejects with the element's `XrplFileError` when the element is in its error state, and with `XrplFileError('NotLoaded')` before the file's manifest is known.
  - **Attribute `caption`:** `"off"` hides the hover caption entirely. Any other value or absence keeps today's behaviour. Add `caption` to `observedAttributes`.
  - **Version:** `web/package.json` becomes `"version": "1.1.0"`. The build output is `web/dist/1.1.0/`.
  - **New build artifact:** `web/dist/1.1.0/xrpl-file-reader.mjs`, an ES module bundle exporting `XrplFileReader`, `LedgerClient` (whatever class `page.mjs` uses to reach nodes), `parseLink`, `formatLink` and `XrplFileError`. Its SHA-384 is added to `integrity.json` under the key `xrpl-file-reader.mjs`.

- [ ] **Step 1: Write failing tests.**
  - `load detail carries name, type, size, network and link`: for the svg fixture vector, the detail equals `{ name:'drawing.svg', type:'image/svg+xml', size:268, network:'testnet', link:'xrpl-file://testnet/<HASH>' }`.
  - `save() before load rejects NotLoaded`.
  - `save() after an error rejects with that error's code`.
  - `save() resolves 'started' on the Blob path and 'saved' on the picker path`, using the injection points `download.mjs` already has.
  - `caption="off" keeps the caption hidden after load`, and removing the attribute shows it again on hover.
  - `build emits xrpl-file-reader.mjs with its integrity and the 1.1.0 banner`: `integrity.json` has keys `xrpl-file-element.js`, `mp4box.min.js` and `xrpl-file-reader.mjs`, and the module's exports include the five names above.
- [ ] **Step 2: Run** `cd web && npm test`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement** in the files listed. Reuse the build's existing module-inlining (`NAMED_IMPORT` …) for the reader bundle, with `web/src/reader.mjs` plus the link module as the entry.
- [ ] **Step 4: Run** `cd web && npm test && node build.mjs`. Expected: all tests pass, and `web/dist/1.1.0/` holds four files plus `integrity.json`.
- [ ] **Step 5: Update the README** "Embed a file" section: add the snippet with version `1.1.0`, and describe the `caption` attribute, the `load` detail and `save()`.
- [ ] **Step 6: Commit** `feat(web): element 1.1.0 — load detail, save(), caption off, and an ESM reader bundle`.

### Task 3: Price tool and `site/data/price.json`

**Files:**
- Create:
  - `tools/XrplFile.SitePrice/XrplFile.SitePrice.csproj`, `Program.cs`, `SitePrice.cs`, `FreshAccountSource.cs`;
  - `tests/XrplFile.SitePrice.Tests/…`;
  - `site/data/price.json` (generated).
- Modify: `XrplFile.sln`, and `XrplFile.Libraries.slnf` if tools belong there by convention (follow `tools/XrplFile.Vectors`).

**Interfaces:**
- Consumes: `PublicationEstimator.EstimateAsync` / `EstimateBatchAsync` (`src/XrplFile.Core/PublicationEstimate.cs`), `ILedgerSource`, `NetworkFees(OpenLedgerFeeDrops, BaseReserveDrops, OwnerReserveDrops)` and `XrplClientLedgerSource`.
- Produces:
  - **`FreshAccountSource : ILedgerSource`.** It delegates fee reads to a real source and answers account state for a brand-new account: balance 0, owner count 0, no tickets. Every other member throws `NotSupportedException`.
  - **`SitePrice.ComputeAsync(ILedgerSource source, IReadOnlyList<long> sizes, DateTimeOffset at, CancellationToken ct) -> PriceTable`.** Choose ticketed or Batch per size exactly as `EstimateModel` chooses for a request with a payer: Batch unless the file is too small for Batch (`EstimateModel.TooSmallForBatchNote`).
  - **`price.json` schema:**
    ```json
    {"network":"mainnet","takenAt":"<ISO-8601>","node":"<wss url>",
     "fees":{"openLedgerFeeDrops":0,"baseReserveDrops":0,"ownerReserveDrops":0},
     "rows":[{"bytes":51200,"transactions":0,"feeDrops":0,"heldDrops":0,"returnedDrops":0,"method":"batch|tickets"}]}
    ```
    - `feeDrops`: the run as it goes, i.e. the estimate's total fee (`FeeDropsTotal`, or the Batch equivalent), summed over writer and payer.
    - `heldDrops`: the reserve locked during the run (base reserve plus peak ticket or owner reserve) for the throwaway writer.
    - `returnedDrops`: `heldDrops` minus the AccountDelete fee, which equals `OwnerReserveDrops`.
- Sizes: `10_240`, `51_200`, `184_320`, `1_048_576`.

- [ ] **Step 1: Write failing tests** (xUnit v3) against a fake `ILedgerSource` with fees `(12, 1_000_000, 200_000)`:
  - `SmallFileIsPricedWithTickets`, `LargeFileIsPricedWithBatch`: the methods match what `EstimateModel` would pick.
  - `FeeIsTheRunAsItGoes`: `feeDrops == transactions × 12` for the ticketed row.
  - `ReturnedIsHeldMinusOwnerReserve`.
  - `JsonMatchesSchema`: the serialised keys and order equal the schema above.
- [ ] **Step 2: Run** `dotnet run --project tests/XrplFile.SitePrice.Tests`. Expected: FAIL.
- [ ] **Step 3: Implement.** `Program` takes `--node <wss>` (default `wss://xrplcluster.com`) and `--out site/data/price.json`. It only reads (fees); it never signs or submits.
- [ ] **Step 4: Run the tests.** Expected: PASS. Then run the tool against mainnet once, and check that `site/data/price.json` exists with four rows and today's date.
- [ ] **Step 5: Commit** `feat(tools): compute the site's price table from the publisher's own estimate` (tool, tests and the generated JSON).

### Task 4: Site skeleton, build, routing and preferences

**Files:**
- Create:
  - `site/build.mjs`, `site/serve.mjs` (dev only);
  - `site/src/_redirects`, `site/src/_headers`;
  - `site/src/js/route.mjs`, `site/src/js/prefs.mjs`;
  - `site/test/route.test.mjs`, `site/test/prefs.test.mjs`, `site/test/build.test.mjs`;
  - `site/README.md`.
- Modify: `.gitignore` (add `site/dist/`).

**Interfaces:**
- Consumes: `parseLink` and `formatLink` from `web/dist/1.1.0/xrpl-file-reader.mjs`. Tests import them from `web/src/link.mjs`.
- Produces:
  - **`route.mjs`:**
    - `pathFromLink(text: string) -> string|null` trims whitespace; `null` when not a link.
    - `linkFromPath(pathname: string) -> { link: string, canonicalPath: string }|null` accepts `/<network>/<hash>` with optional trailing slash, any case.
  - **`prefs.mjs`:**
    - `getTheme(storage = safeLocalStorage()) -> 'light'|'dark'` defaults to `'light'`; `setTheme(theme, storage)`; key `xf-theme`.
    - `getLang(storage, navigatorLanguage) -> 'en'|'ru'`: stored `xf-lang`, else `'ru'` when `navigatorLanguage` starts with `ru`, else `'en'`; `setLang(lang, storage)`.
    - `equivalentPath(pathname, lang) -> string` maps `/` ↔ `/ru/` and `/x.html` ↔ `/ru/x.html`. A file page path stays unchanged.
    - `applyTheme(doc, theme)` sets `doc.documentElement.dataset.theme` and the `theme` attribute of every `xrpl-file` element.
  - **`build.mjs`:** `build({ siteDir, webDistDir, outDir, elementVersion: '1.1.0' })` copies `site/src/**` to `outDir`, `web/dist/1.1.0/*` to `outDir/element/1.1.0/`, and `site/data/price.json` to `outDir/data/`. It fails when the element version directory is missing or any file exceeds 25 MB.
  - **`_redirects`:** `/:network/:hash /file.html 200`. The more specific static paths (`/ru/*`, `/element/*`, `/data/*`, assets) must win. Check against Cloudflare's `_redirects` ordering rules and order the file accordingly.
  - **`_headers`:** the Global Constraints' cache and CORS rules for `/element/*`, and `Cache-Control: no-cache` for `/*.html` and `/`.

- [ ] **Step 1: Write failing tests.**
  - Route:
    - `pathFromLink(' XRPL-FILE://TestNet/32d9…59 \n') === '/testnet/32D9…59'` (Review Focus 1);
    - `pathFromLink('xrpl-file://21338/<HASH>') === '/21338/<HASH>'` and `linkFromPath('/21338/<hash>').link === 'xrpl-file://21338/<HASH>'` (Review Focus 5);
    - `linkFromPath('/TESTNET/<lower>/').canonicalPath === '/testnet/<UPPER>'`;
    - `linkFromPath('/ru/') === null`, `linkFromPath('/testnet/xyz') === null`.
  - Prefs:
    - `getTheme` with a storage whose `getItem` throws returns `'light'`, and `setTheme` with a throwing storage does not throw (Review Focus 2);
    - first-visit theme is `'light'` even when `matchMedia('(prefers-color-scheme: dark)')` matches;
    - `getLang(null, 'ru-RU') === 'ru'`;
    - `equivalentPath('/', 'ru') === '/ru/'`, `equivalentPath('/ru/', 'en') === '/'`, `equivalentPath('/testnet/<H>', 'ru') === '/testnet/<H>'`.
  - Build: `copies the element under /element/1.1.0 and refuses a missing version`.
- [ ] **Step 2: Run** `node --test site/test/*.test.mjs`. Expected: FAIL.
- [ ] **Step 3: Implement** the modules and `build.mjs`. `serve.mjs` serves `site/dist` on `127.0.0.1:8791` and applies the `_redirects` rewrite for `/<network>/<hash>`. It never binds to a port in use: on a clash it tries the next port.
- [ ] **Step 4: Write `site/README.md`:** build and serve commands, each `_redirects` / `_headers` rule with its nginx equivalent, how to re-publish the hero fixture and swap the constant (see the spec's durability note), and how to regenerate `price.json`.
- [ ] **Step 5: Run** the site tests and `node site/build.mjs`. Expected: PASS, and `site/dist/` is created.
- [ ] **Step 6: Commit** `feat(site): skeleton, build, file-page routing and remembered theme and language`.

### Task 5: Visual system (hallmark, custom mode)

**Files:**
- Create:
  - `site/src/css/tokens.css`, `site/src/css/base.css`, `site/src/css/site.css`;
  - `site/src/partials/` if the build inlines a shared header and footer; otherwise duplicate small header and footer markup per page and keep it in one template string in `site/build.mjs`;
  - `site/src/js/chrome.mjs` (header and footer behaviour);
  - `docs/superpowers/reports/2026-11-02-site/contrast.md`.

**Interfaces:**
- Consumes: `prefs.mjs` (`getTheme`, `setTheme`, `applyTheme`, `getLang`, `setLang`, `equivalentPath`).
- Produces:
  - **CSS custom properties** on `:root[data-theme="light"]` and `:root[data-theme="dark"]`: `--paper`, `--ink`, `--ink-muted`, `--rule`, `--accent`, `--accent-ink`, `--cell`, `--cell-lit`, `--cell-verified`. Later tasks style only with these.
  - **Header:** wordmark linking home, the language switch (links built by `equivalentPath`) and the theme switch (a button with `aria-pressed`).
  - **Footer:** repository, specification, language and theme.
  - The theme is applied before first paint by a tiny inline script in `<head>` that calls the same logic as `getTheme`, so the page does not flash.

- [ ] **Step 1: Get hallmark without installing it globally.** Clone `https://github.com/nutlope/hallmark` into `.superpowers/hallmark/`, which is already git-ignored. Read `skills/hallmark/SKILL.md` and `references/direction.md`, then follow **custom mode**. The direction is in the spec's "Look":
  - a generated picture of structure, the chunk cell as motif;
  - calm grotesque;
  - light default that is not too bright;
  - one accent.
  Use `scripts/theme.mjs scaffold --custom` for `tokens.css` and `base.css`, and keep the stamp comment it prints.
- [ ] **Step 2: Choose and record** the palette, accent and fonts in the stamp:
  - fonts self-hosted under `site/src/fonts/` with their licence files; no Google Fonts request, because there are no third-party requests;
  - light paper at OKLCH lightness between 0.93 and 0.97, never `#fff`.
- [ ] **Step 3: Compute contrast** for every text/background and UI/background pair in both themes, plus the element's `light` and `dark` palettes on the site's paper. Write the table to `contrast.md`. Every row must meet the Global Constraints' AA rule; fix tokens until it does (skill `ui-a11y-gates`).
- [ ] **Step 4: Build the header and footer** with `chrome.mjs`. Test in `site/test/chrome.test.mjs` with a minimal DOM stub (or `node:test` plus a tiny fake document):
  - `theme switch toggles data-theme and every xrpl-file theme attribute`;
  - `language links point at equivalentPath`.
- [ ] **Step 5: Run** the site tests and `node skills/hallmark/scripts/sloplint.mjs site/src` from the clone. Expected: tests pass, and the linter reports no errors. Fix any warning that is not a deliberate choice recorded in the stamp.
- [ ] **Step 6: Commit** `feat(site): visual system, header and footer with theme and language switches`.

### Task 6: The chunk mosaic

**Files:**
- Create:
  - `site/src/js/mosaic-state.mjs` (pure);
  - `site/src/js/mosaic.mjs` (canvas and reader wiring);
  - `site/test/mosaic-state.test.mjs`.

**Interfaces:**
- Consumes:
  - from Task 1: `read(link, onProgress, { onChunk, onBlockVerified })`;
  - from Task 2: `XrplFileReader`, `LedgerClient` and `parseLink` from `/element/1.1.0/xrpl-file-reader.mjs`;
  - Task 5's tokens `--cell`, `--cell-lit`, `--cell-verified`, `--paper`, `--ink-muted`.
- Produces:
  - **`createMosaic({ chunks: number, aspect: number }) -> state`.** State: `{ columns, rows, cells: Array<'empty'|'lit'|'verified'>, texture: Array<number|null>, phase: 'reading'|'developing'|'done'|'failed' }`. `columns × rows ≥ chunks`, and the grid shape follows `aspect`: the fixture gives 13 × 14 for 179 chunks at aspect 1.
  - **Pure transitions:**
    - `chunkArrived(state, { index, bytes }) -> state`: sets the cell `lit` and the texture to the mean byte value / 255;
    - `blockVerified(state, { firstChunk, lastChunk })`: sets those cells `verified`;
    - `allVerified(state) -> 'developing'`;
    - `failed(state, error)`.
  - **`mountMosaic(canvas, { link, aspect, reducedMotion, fast }) -> { done: Promise<void> }`.**
    - Reads through the reader and draws each transition.
    - On the `developing` phase it decodes the verified bytes (`createImageBitmap(new Blob([bytes], { type }))`) and turns the cells over into the picture.
    - Then it writes the caption "✓ verified · N chunks · ledgers X–Y" with a link to `pathFromLink(link)`.
  - **Timeout:** when no chunk has arrived for 20 s, or the read rejects (`NodeLacksHistory`, `NodeUnreachable`, …), the phase becomes `failed`. The cells read so far stay, and a quiet message follows, worded in Task 7's copy.
  - **Fast replay:** a `sessionStorage` key `xf-mosaic-seen` (try/catch) sets `fast: true`, which shortens animations to ≤ 300 ms total.
  - **Reduced motion:** with `reducedMotion: true`, cells change without animation and the picture appears when verified.

- [ ] **Step 1: Write failing tests** for `mosaic-state.mjs`:
  - `179 chunks at aspect 1 give 13 columns and 14 rows`;
  - `cells light in arrival order and keep their texture`;
  - `blockVerified marks exactly its range`;
  - `all cells verified moves to developing`;
  - `failed keeps the lit cells and records the error code`;
  - `a 20 s gap with no chunk fails with code 'Stalled'`, using an injected clock (Review Focus 4).
- [ ] **Step 2: Run** `node --test site/test/mosaic-state.test.mjs`. Expected: FAIL.
- [ ] **Step 3: Implement** `mosaic-state.mjs`, then `mosaic.mjs`. The canvas follows `devicePixelRatio` and resizes with its box. Colours come from the CSS custom properties, read again on a theme switch.
- [ ] **Step 4: Run** the site tests. Expected: PASS.
- [ ] **Step 5: Commit** `feat(site): the hero mosaic reads a real file chunk by chunk and develops it when verified`.

### Task 7: Home page in English and Russian

**Files:**
- Create: `site/src/index.html`, `site/src/ru/index.html`, `site/src/js/home.mjs`, `site/src/js/price.mjs`, `site/test/price.test.mjs`.

**Interfaces:**
- Consumes:
  - from Task 3: `price.json` schema;
  - from Task 4: `pathFromLink`;
  - from Task 6: `mountMosaic`;
  - Task 5's tokens, header and footer.
- Produces:
  - **`price.mjs`:** `formatXrp(drops: number, lang) -> string` (drops / 1,000,000, up to 6 decimals trimmed, locale separators); `priceRows(json, lang) -> Array<{ size, fee, held, returned }>`.
  - **Sections in the spec's order:** hero, problem, how it works, price, permanent and public, for developers, footer. The content is listed per section in the spec. Copy is written with the skill `writing-site-copy`, English first, then Russian with the same facts.
  - **"Check a link" field:** submitting it navigates to `pathFromLink(value)`. An invalid value shows an inline error and does not navigate.
  - **"Download the publisher" button:** reads `site/src/js/config.mjs` `PUBLISHER_URL` (initially `null`). When `null`, the button is disabled with "soon" ("скоро").
  - **"For developers":** the snippet uses the `integrity` from `element/1.1.0/integrity.json`, inserted at build time by `site/build.mjs` (extend it), with a Copy button. It also links to the spec, the repository and the README.
  - **"How it works" drawing:** inline SVG in the cell motif, coloured only by tokens.

- [ ] **Step 1: Write failing tests:**
  - `formatXrp(1_234_500,'en') === '1.2345'`;
  - `formatXrp(1_234_500,'ru') === '1,2345'`;
  - `priceRows keeps the JSON's order and takes fee, held and returned from it`;
  - the build test: `built index.html contains the 1.1.0 snippet with the integrity from integrity.json`.
- [ ] **Step 2: Run** the site tests. Expected: FAIL.
- [ ] **Step 3: Implement** `price.mjs`, `home.mjs`, both HTML pages and the build-time snippet insertion. State the price section's date and network from `price.json`, with sentences in the spirit of "fees, spent" and "reserve, held and returned".
- [ ] **Step 4: Run** the site tests and `node site/build.mjs`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(site): home page in English and Russian`.

### Task 8: File page and 404

**Files:**
- Create:
  - `site/src/file.html`, `site/src/404.html`;
  - `site/src/js/file-page.mjs`, `site/src/js/strings.mjs`;
  - `site/test/file-page.test.mjs`.

**Interfaces:**
- Consumes:
  - from Task 2: element `load` detail, `save()` and `caption="off"`;
  - from Task 4: `linkFromPath`, `getLang`.
- Produces:
  - **`strings.mjs`:** `t(key, lang)` over a flat table of EN/RU strings for the file and 404 pages; a missing key throws in tests.
  - **`file-page.mjs` `initFilePage(doc, location, { lang })`:**
    - for an invalid path it shows "Check a link" with an error;
    - for a non-canonical path it calls `history.replaceState` to the canonical path;
    - otherwise it renders `<xrpl-file src=… caption="off" theme=…>` full width.
  - **Under the element:** the Download button, initially disabled and enabled on `load`, which calls `element.save()` and shows the resolved status ("Saved." / "Download started."). Rejection shows the element's error text, and nothing is left uncaught (Review Focus 3).
  - **Also under it:** name, size and network from the `load` detail; the link with a Copy button; the "How this works" note from the spec, with a link home.
  - **`404.html`:** header, a short message, the "Check a link" field, a link home.

- [ ] **Step 1: Write failing tests** with a fake element (an `EventTarget` with `save()`):
  - `the Download button is disabled until load`;
  - `a rejected save shows the error and does not throw`;
  - `a lower-case path is replaced by the canonical one`;
  - `name and size come from the load detail`;
  - `every key exists in both languages`.
- [ ] **Step 2: Run** the site tests. Expected: FAIL.
- [ ] **Step 3: Implement** the pages and modules.
- [ ] **Step 4: Run** the site tests and the build. Expected: PASS.
- [ ] **Step 5: Commit** `feat(site): file page and 404`.

### Task 9: Browser acceptance

**Files:**
- Create: `docs/superpowers/reports/2026-11-02-site/report.md` and screenshots beside it.

- [ ] **Step 1: Build and serve.** Run `node site/build.mjs && node site/serve.mjs` and open it in the built-in browser.
- [ ] **Step 2: Check and record in a table.** Every row passes or names its defect:
  1. home EN and RU × light and dark × 390 px and 1280 px wide;
  2. the hero mosaic reads, verifies and develops the fixture; the caption links to its file page;
  3. the mosaic's failure state: point it at a nonexistent hash via a dev-only query override or a stub node, and check that it settles within 20 s;
  4. fast replay on a second load in the same tab; reduced motion;
  5. "Check a link" with a valid, a messy-case (Review Focus 1) and an invalid value;
  6. file page for the image, svg, text and video fixtures (links in `run/fixtures/links.json`):
     - the Download button flow;
     - Copy link;
     - a non-canonical path is canonicalised;
     - a bad hash shows the element's error and the page stays intact;
  7. `404.html` via an unknown path;
  8. the theme survives navigation, and with storage blocked the site renders light (Review Focus 2);
  9. keyboard-only pass over both pages: focus visible, switches operable;
  10. `sloplint --render` over `site/dist`;
  11. no request to any host other than the site and the XRPL node (network log).
- [ ] **Step 3: Fix and repeat.** Fix defects in their owning task's files, commit each fix (`fix(site): …`), and repeat the failed rows.
- [ ] **Step 4: Commit** the report: `docs(reports): site acceptance`.

### Task 10: Deploy to Cloudflare Pages — only on the owner's command

- [ ] **Step 1: Prepare the deploy note** for the owner, and do not deploy yet. It covers:
  - project name `xrpl-file-site`, output dir `site/dist`, build command `node web/build.mjs && node site/build.mjs`;
  - custom domain `xrpl-file.staticbit.io`;
  - whether `staticbit.io`'s DNS is on Cloudflare: check it through the Cloudflare tools if authorised, otherwise ask;
  - that a Pages deploy publishes the site publicly.
- [ ] **Step 2: Deploy when commanded.** After the owner's explicit command, deploy (Cloudflare tools or `wrangler pages deploy site/dist`), attach the domain, and verify:
  - `https://xrpl-file.staticbit.io/` and `/ru/`;
  - a fixture's file page;
  - `/element/1.1.0/xrpl-file-element.js` with `access-control-allow-origin: *` and the immutable cache header;
  - the snippet working from JSFiddle.
- [ ] **Step 3: Record** the deployed URL and the checks in the report and commit `docs(reports): site deployed`.
