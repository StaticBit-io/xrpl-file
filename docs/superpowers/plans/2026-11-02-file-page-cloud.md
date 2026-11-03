# Point cloud on the file page — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The file page shows the home page's point cloud while `<xrpl-file>` reads the file, fed by three new element events (element 1.2.0).

**Architecture:** The element's shared block reads carry `{ index, ledger }` per chunk; a per-load reporter turns them into `xrpl-file-manifest` / `xrpl-file-chunk` / `xrpl-file-block`, each at most once per load. On the site, an adapter with `startHeroRead`'s contract groups chunks into at most 240 cubes and feeds the unchanged-in-spirit `mountCloud`, which gains options for the file page (no turn, sway limit, live strings).

**Tech Stack:** Plain ES modules, `node --test`, no dependencies.

**Spec:** `docs/superpowers/specs/2026-11-02-file-page-cloud-design.md`

## Global Constraints

- Element version **1.2.0**; `web/integrity/1.2.0.json` recorded from the final element build; no `web/src` change after it is recorded without re-recording.
- Event names: `xrpl-file-manifest`, `xrpl-file-chunk`, `xrpl-file-block`; not bubbling, `detail` a fresh plain object; no chunk bytes in any event.
- Cube cap **240** (`COLUMNS` 2 × `MAX_ROWS` 10 × `MAX_LAYERS` 12); above it `perCube = ceil(chunks / 240)`.
- Sway on the file page stops **5000 ms** after mount; fade-out of the cloud under 300 ms, instant under reduced motion.
- File page always uses `cloudTimings({ fast: true })` (or reduced motion); it never reads or writes `xf-mosaic-seen`.
- Six languages (en, ru, de, es, pt, zh) with identical string shapes; copy follows the `writing-site-copy` skill.
- Code, comments and commits in English, conventional commits, no AI attribution lines. No push, publish, merge or PR.
- Test commands: `node --test site/test/*.test.mjs` (repo root) and `cd web && node --test test/*.test.mjs`.

## Review Focus

- A `src` change or disconnect mid-read: no event of the old load reaches listeners after the reset (Task 2).
- A chunk count that is not a multiple of `perCube`: the last, smaller group still turns lime (Task 5).
- The element fails before its manifest (node down, wrong hash): the page hides the cloud and throws nothing (Tasks 5, 7).
- Language switched mid-read: label, caption and note re-worded at once (Task 7).
- A transparent picture: after `load` the canvas is `hidden`, not merely covered (Task 7).
- Video reads a tail block first: the group's cube stands on the ledger of the first chunk that arrived, not of the lowest index (Task 5).

---

### Task 1: Shared block reads carry each chunk's index and ledger

**Files:**
- Modify: `web/src/element/page.mjs` (`SharedRead`, `callProgress`, `LinkState.block`, `#attach`, `#execute`, `#remember`, `FileHandle.block` doc)
- Test: `web/test/element-page.test.mjs`

**Interfaces:**
- Produces: `FileHandle.block(index, { onChunk })` calls `onChunk({ index, ledger })` once per chunk of the block — for a fresh read, a late join (replaying the chunks already taken, with their real values) and a verified-cache hit (replaying the stored values). `index` is the absolute chunk index, `ledger` the ledger of the chunk's transaction. Callers that ignore the argument keep working.

- [ ] **Step 1: Write the failing tests** — `onChunk receives index and ledger for a fresh read`, `a late joiner gets the chunks already read with their ledgers`, `a cached block replays its chunks with their ledgers`. Each asserts the array of received objects deep-equals `[{ index: 0, ledger: L0 }, { index: 1, ledger: L1 }, …]` for the fake reader's chunks (reuse the fake source/reader the file already uses; its `block()` callback is the reader's `onChunk({ index, bytes, ledger })`).
- [ ] **Step 2: Run** `cd web && node --test test/element-page.test.mjs` — the three tests FAIL (listeners get `undefined`).
- [ ] **Step 3: Implement.** `SharedRead.taken: Array<{ index, ledger }>` replaces the `chunks` counter (keep a `chunks` getter if other code reads it); `emitChunk(chunk)` pushes `{ index: chunk.index, ledger: chunk.ledger }` and calls every listener with a fresh copy; `callProgress(listener, info)`. `verified` map values become `{ bytes, taken }`; `#remember(index, bytes, taken)`; a cache hit replays `taken`. Do not keep chunk bytes anywhere new.
- [ ] **Step 4: Run** the web suite — all PASS.
- [ ] **Step 5: Commit** `feat(web): shared block reads report each chunk's index and ledger`

### Task 2: Element 1.2.0 read events

**Files:**
- Create: `web/src/element/report.mjs`
- Modify: `web/src/element/element.mjs` (`#load`, `#readHead`, `#loadImage`, `#loadText`, `#loadVideo`), `web/src/element/load.mjs` (`loadWhole`), `web/src/element/video.mjs` (`constructor`, `#readBlock`)
- Modify: `README.md` (element API: the three events)
- Test: `web/test/element-report.test.mjs` (new), `web/test/element-api.test.mjs`

**Interfaces:**
- Consumes: Task 1's `onChunk({ index, ledger })`.
- Produces:
  - `createReadReport({ manifest, blockBytes, signal, emit }) -> { manifest(): void, chunk({ index, ledger }): void, block(index: number): void }`. `manifest()` emits `xrpl-file-manifest` with `{ size, chunk, chunks: ceil(size / chunk), blocks, firstLedger, lastLedger }` once; `chunk` emits `xrpl-file-chunk` `{ index, ledger }` once per index; `block` emits `xrpl-file-block` `{ index, firstChunk, lastChunk }` once per index, `firstChunk = index * blockBytes / chunk`, `lastChunk = min(chunks, firstChunk + blockBytes / chunk) - 1`. Every call is a no-op once `signal.aborted`. Repeats are suppressed with a `Uint8Array` per chunk and per block.
  - `loadWhole(handle, record, { …, onRead })` and `new VideoView({ …, onRead })`: `onRead` is a report (or null); chunks pass to `onRead.chunk(info)`, and `onRead.block(index)` is called after `handle.block` resolves.
- [ ] **Step 1: Write the failing tests** in `element-report.test.mjs`: `manifest detail has exactly the six fields`; `a chunk reported twice is emitted once`; `a block reported twice is emitted once`; `block ranges: block 0 of a 3-chunk file is 0..2, the last block is clamped to the last chunk`; `nothing is emitted after the signal aborts`.
- [ ] **Step 2: Write the failing element tests** in `element-api.test.mjs` (with the file's existing fake host): `an image load emits manifest, then each chunk once, then each block once, before load` (block 0 is read twice on this path: assert one event per chunk of block 0); `a cached block reports its chunks then the block`; `a src change mid-read emits nothing more for the old file`; `save() emits no read events`; `events do not bubble`.
- [ ] **Step 3: Run** `cd web && node --test test/element-report.test.mjs test/element-api.test.mjs` — FAIL.
- [ ] **Step 4: Implement** `report.mjs`; in `#load` create the report once the record is known (signal = the load's controller signal, `emit` = the element's `#emit`) and call `manifest()` before the first block read; pass it to `#readHead` (its `onChunk`), `loadWhole` and `VideoView`. Not to `save()`.
- [ ] **Step 5: Document** the three events in `README.md` next to `xrpl-file-progress`: when each fires, its `detail`, "at most once per load", "chunk means read, not verified".
- [ ] **Step 6: Run** the web suite — all PASS.
- [ ] **Step 7: Commit** `feat(web): xrpl-file-manifest, xrpl-file-chunk and xrpl-file-block events`

### Task 3: Version 1.2.0 everywhere

**Files:**
- Modify: `web/package.json`, `site/build.mjs` (`ELEMENT_VERSION`), `site/src/file.html`, `site/src/js/cloud.mjs` (`ELEMENT_SCRIPT`), `site/src/js/xrpl.mjs`, `site/src/index.html` and `site/src/{ru,de,es,pt,zh}/index.html` (snippet prose), `site/README.md`, `README.md`, `web/demo/head.html`, `web/demo/snippet.html`, tests in `site/test` and `web/test` that hard-code `1.1.0`
- Create: `web/integrity/1.2.0.json`

- [ ] **Step 1:** `grep -rn "1\.1\.0"` outside `docs/` and `dist/`; change every live reference to `1.2.0`. `web/integrity/1.1.0.json` stays (the released record).
- [ ] **Step 2:** `cd web && node build.mjs`, copy `web/dist/1.2.0/integrity.json` to `web/integrity/1.2.0.json`.
- [ ] **Step 3: Run** both suites — all PASS (`released-integrity.test.mjs` included).
- [ ] **Step 4: Commit** `build(web): element 1.2.0`

Any later change under `web/src` re-runs Step 2 in the same commit.

### Task 4: `mountCloud` serves both pages

**Files:**
- Modify: `site/src/js/cloud.mjs`, `site/src/js/home.mjs`
- Test: `site/test/cloud.test.mjs`, `site/test/home.test.mjs`

**Interfaces:**
- Produces, in `mountCloud(canvas, options, env)`:
  - `cloud.mjs` imports neither `hero-read.mjs` nor `xrpl.mjs`. `env.startRead` is required; `options.fast` defaults to `false`; `options.link` is a string (or absent: no callouts' link figure, no card link). `home.mjs` passes `startRead: startHeroRead` and `fast: hasSeenMosaic()`.
  - `options.turn` (default `true`): `false` never starts the turn.
  - `options.swayFor` (ms, default `Infinity`): the sway ends at `mountTime + swayFor` (or at the read's end, whichever is first).
  - Listener `onManifest({ manifest, chunks, fileChunks = chunks, perCube = 1, time })`: `chunks` sizes the layout (cubes), `fileChunks` and `perCube` go to the strings.
  - Strings: `label(phase, { chunks: fileChunks, perCube })`, `note({ ledgers, ledgersPerPad, capped, perCube })`, optional `reading({ chunks: fileChunks, perCube, firstLedger, lastLedger })` — when present, the caption shows it from `onManifest` until the phase changes; the hero's strings have none, so its caption stays empty while reading.
  - Returned object gains `setStrings(strings)`: replaces the strings and rewrites label, caption and note at once.
- [ ] **Step 1: Write the failing tests:** `cloud.mjs imports neither hero-read.mjs nor xrpl.mjs` (read the source, match `import … from`); `turn: false never turns after done`; `swayFor stops the sway at mount + swayFor` (frame requests stop once the read is idle past it, with the fake clock); `reading() fills the caption at the manifest`; `setStrings rewrites label, caption and note`; `perCube and fileChunks reach the note and the label`.
- [ ] **Step 2: Run** `node --test site/test/cloud.test.mjs site/test/home.test.mjs` — FAIL.
- [ ] **Step 3: Implement**; adapt existing cloud tests to pass `startRead: startHeroRead` explicitly.
- [ ] **Step 4: Run** the site suite — all PASS.
- [ ] **Step 5: Commit** `refactor(site): let the point cloud run without the hero's reader`

### Task 5: Adapter from the element's events to the cloud

**Files:**
- Create: `site/src/js/element-read.mjs`
- Test: `site/test/element-read.test.mjs`

**Interfaces:**
- Consumes: Task 2's events; Task 4's listener contract.
- Produces: `export const CUBE_CAP = 240;` `export function startElementRead(options, env) -> { done: Promise<void>, destroy(): void, readonly phase: string, readonly state: object, readonly manifest: object|null }`. `options.listener` as in `startHeroRead`; `env.element` the `<xrpl-file>`; `env.now` the clock. Also exported for tests: `seedBytes(index: number, ledger: number) -> Uint8Array` (32 deterministic bytes, xorshift from both values).
- Behaviour:
  - `xrpl-file-manifest` → `perCube = max(1, ceil(chunks / CUBE_CAP))`, `cubes = ceil(chunks / perCube)`; `onManifest({ manifest: detail, chunks: cubes, fileChunks: detail.chunks, perCube, time })`. A second manifest event is ignored.
  - `xrpl-file-chunk` → cube `floor(index / perCube)`; the first chunk of a cube to arrive calls `onChunk({ index: cube, ledger, bytes: seedBytes(cube, ledger), time })`; later chunks of it only count.
  - `xrpl-file-block` → adds the block's chunks to each touched cube's verified count; a cube whose count reaches its size (the last cube may be smaller) gets `onBlockVerified({ firstChunk: cube, lastChunk: cube, time })`.
  - `load` → phase `done` (`onPhase('done')`, `onDone`), `done` resolves; `error` → phase `failed` (`onPhase('failed')`, `onFailed({ error: { code } })` with `code` from `event.detail?.code ?? 'Unknown'`), `done` resolves. No stall clock; no `developing`.
  - `destroy()` removes every listener and resolves `done`.
  - Per-cube state lives in typed arrays; no per-event copy of all cells.
- [ ] **Step 1: Write the failing tests** with a plain `EventTarget` as the element: `123 chunks make 123 cubes, perCube 1`; `20000 chunks make 239 cubes of 84` (`ceil(20000/240) = 84`, `ceil(20000/84) = 239`); `a cube appears on the ledger of the first chunk to arrive` (send chunk 83 at ledger 900 before chunk 0 at ledger 100; `onChunk` once, with ledger 900); `the last smaller cube turns lime` (chunks 250, perCube 2, last cube has 2 chunks; 125 cubes — and a case with 241 chunks: perCube 2, last cube 1 chunk); `a cube is not lime until every chunk of it is verified`; `load ends in done, error in failed with its code`; `an error before the manifest fails cleanly`; `destroy removes the listeners`; `seedBytes is deterministic and differs by ledger`.
- [ ] **Step 2: Run** `node --test site/test/element-read.test.mjs` — FAIL.
- [ ] **Step 3: Implement** `element-read.mjs`.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat(site): drive the point cloud from the element's read events`

### Task 6: The file page's cloud strings

**Files:**
- Modify: `site/src/js/home-strings.mjs` (header comment; new `fileCloudStrings(lang, network)`)
- Test: `site/test/languages.test.mjs`

**Interfaces:**
- Produces: `fileCloudStrings(lang, network) -> { label(phase, { chunks, perCube }), reading({ chunks, perCube, firstLedger, lastLedger }), failed(code), note({ ledgers, ledgersPerPad, capped, perCube }) }` for every active language, falling back to English. Phases: `reading`, `done`, `failed`. The note keeps the hero's "drawn, not measured" list and adds the cube's inside points to it; with `perCube > 1` it says each cube is `perCube` chunks.
- [ ] **Step 1: Load the `writing-site-copy` skill**; draft the English set from the hero's strings (`cloudStrings`), then the five translations in the hero's own terminology per language.
- [ ] **Step 2: Write the failing tests** mirroring the `cloudStrings` checks: same shape in every active language; every label non-empty for `chunks` 1 and 123, `perCube` 1 and 84; `reading` and `note` non-empty for capped/uncapped × perCube 1/84; `failed` non-empty for the same codes as the hero.
- [ ] **Step 3: Run** `node --test site/test/languages.test.mjs` — FAIL; implement; run — PASS.
- [ ] **Step 4: Commit** `feat(site): words for the file page's point cloud in six languages`

### Task 7: Wire the cloud into the file page

**Files:**
- Modify: `site/src/file.html` (stage inside `#file-view`: `<div class="file__stage">` with `<canvas id="file-cloud" class="cloud file__cloud">`, the element appended after it; `<p id="file-cloud-caption" class="cloud-caption">` and `<p id="file-cloud-note" class="cloud-note" hidden>` after the stage)
- Modify: `site/src/css/site.css` (`.file__stage { position: relative }`, canvas absolutely over the element's box, under it; `.file__cloud.is-gone` opacity 0 with a 250 ms transition, none under `prefers-reduced-motion: reduce`)
- Modify: `site/src/js/file-page.mjs`
- Test: `site/test/file-page.test.mjs`

**Interfaces:**
- Consumes: `mountCloud` (Task 4), `startElementRead` (Task 5), `fileCloudStrings` (Task 6).
- Produces: `initFilePage` sets `loader="off"` on the element and mounts the cloud with `{ caption, note, turn: false, swayFor: 5000, fast: true, strings: fileCloudStrings(lang, network) }` and env `{ startRead: startElementRead, element }`. On the element's `load` or `error`: add `is-gone`, then after 250 ms (at once under reduced motion) set the canvas's stage layer `hidden`, empty and hide caption and note, `destroy()` the cloud. On `xf-langchange`: `cloud.setStrings(fileCloudStrings(lang, network))`. Update the comment that says the page has no `error` listener on purpose.
- [ ] **Step 1: Write the failing tests:** `the element gets loader="off"`; `the cloud is mounted before the element reads`; `load hides the canvas, empties the caption and destroys the cloud`; `error before the manifest hides the canvas without throwing`; `a language switch re-words the cloud`; `an invalid address mounts no cloud`.
- [ ] **Step 2: Run** `node --test site/test/file-page.test.mjs` — FAIL.
- [ ] **Step 3: Implement** markup, CSS and wiring.
- [ ] **Step 4: Run** the site suite — all PASS.
- [ ] **Step 5: Commit** `feat(site): the point cloud on the file page`

### Task 8: Verification in the preview

- [ ] **Step 1:** `cd web && node build.mjs && cd .. && node site/build.mjs`; start the preview (`node site/serve.mjs`, port 8791) in the background.
- [ ] **Step 2:** In the built-in browser open `/mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5`: screenshots mid-read and after load, in the dark and light themes; the console has no errors; after load the canvas is `hidden`; in Russian the caption and note are Russian.
- [ ] **Step 3:** With reduced motion emulated (or `matchMedia` overridden), the cloud draws still frames and disappears at once.
- [ ] **Step 4:** Both suites green: `node --test site/test/*.test.mjs` and `cd web && node --test test/*.test.mjs`.
- [ ] **Step 5:** Hand-over note in the shared notes folder; report to the owner.
