# The point cloud on the file page — design

Date: 2026-11-02. Status: for the owner's review.

## Why

The home page's hero draws the StaticBit logo being read from mainnet as a point cloud: ledgers are
pads of ground, each chunk is a cube standing on the pad of the ledger it was written in, and a cube
turns lime when the block that holds it has matched its hash. The file page `/<network>/<HASH>`
shows only the `<xrpl-file>` element's own loader while the file is read. The owner wants the same
cloud there, "to add interest": a visitor who opens a shared link watches their file come out of
the ledger, chunk by chunk, before it appears.

## Goals

- While `<xrpl-file>` reads the file on the file page, the cloud shows that read: a cube per chunk
  the element has accepted, on the pad of its real ledger, lime once its block is verified.
- The cloud is fed by the element's own read. The page does not read the file a second time.
- When the element shows the file (or its error, or its download view), the cloud gives way to it.
- Works for any file the element accepts, up to the 20 MB limit (~20,000 chunks of 1000 bytes),
  within the cloud's current frame budget.

## Non-goals

- No turn and no card at the end, as on the home page: on the file page the file itself is the
  ending.
- No cloud for `save()` (Download). Save keeps its own status line.
- No change to the home page's hero beyond what sharing code with the file page needs.

## Anti-goals

- A cube must never stand somewhere the read did not put it, or be lime before its block verified.
  An approximate ledger (spreading cubes over the manifest's range by count) was considered and
  rejected for this reason.
- The page must not double the traffic or the time of a read.
- The element must not hand page scripts file bytes that have not been verified.

## Decided by the owner

- **The data comes from the element (option A).** The element gains public events that report its
  read; this makes element version **1.2.0**, a new npm version that only the owner publishes.

## Design

### 1. Element 1.2.0: three new events

Dispatched on the element, not bubbling, like `xrpl-file-progress`. Each `detail` is a fresh plain
object.

| Event | When | `detail` |
|---|---|---|
| `xrpl-file-manifest` | once per load, when the manifest record is known, before any block of the content is read | `{ size, chunk, chunks, blocks, firstLedger, lastLedger }` |
| `xrpl-file-chunk` | once per chunk the element accepts while reading to show the file | `{ index, ledger }` — absolute chunk index, ledger index of the transaction that carried it |
| `xrpl-file-block` | once per block whose hash matched | `{ index, firstChunk, lastChunk }` |

Rules:

- A chunk is reported before its block verifies, so `xrpl-file-chunk` says "read", never
  "verified". No chunk bytes are in the event (anti-goal above).
- Order per block: its chunk events, then its block event. A block the element already holds
  (its recent-blocks cache, or a shared read joined late) reports all its chunks and then the
  block, so a listener sees the same sequence whichever way the block was obtained.
- Events are emitted for reads that show the file: the image read, the text preview read (up to
  its 1 MB limit), the head block read, and the video's block reads (which continue while it
  plays). Not for `save()`.
- **Once per load.** The element reports each chunk index and each block index at most once per
  load, whatever re-reads it: the image path reads block 0 for its type check and again from the
  cache, and the video re-reads blocks after a seek or a cache eviction. A per-load record of what
  was reported (a bit per chunk and per block) suppresses the repeats.
- A load that is reset (`src` change, disconnect) stops emitting for the old load.

What this takes inside the element (`web/src/element/page.mjs`): the per-block shared read and the
verified-block cache currently call chunk listeners with no arguments. They must carry
`{ index, ledger }` per chunk (the reader already delivers it, `web/src/reader.mjs` `block()`
`onChunk`), record it per in-flight read so a late listener gets the chunks already read, and keep
each cached block's chunk ledgers so a cache hit replays them. `load.mjs` (`loadWhole`), the head
read in `element.mjs` and `video.mjs` pass the payload through and dispatch the events.

Release: `web/package.json` 1.2.0, `web/integrity/1.2.0.json` recorded from the build,
`site/build.mjs` `ELEMENT_VERSION` 1.2.0 (it drives the site's own `/element/<version>/` copy and
the CDN snippet together), `cloud.mjs` `ELEMENT_SCRIPT`, `file.html`, `xrpl.mjs` and every other
`1.1.0` reference. The element's README / API docs list the three events.

### 2. The cloud on the file page

- **Place.** A canvas in `#file-view`, laid exactly over the element's box, under it. The element
  gets `loader="off"`, so while it loads its box is bare and the cloud shows through.
- **Feed.** An adapter with the same contract as `startHeroRead` (`site/src/js/hero-read.mjs`):
  `{ done, destroy, phase, state, manifest }` and the same listener callbacks, driven by the
  element's three events. It is passed to `mountCloud` through the existing `env.startRead` seam.
  It runs no stall clock: the element decides when a read has failed. Its state is kept per cube,
  not per chunk (section 3): `mosaic-state.mjs` copies its whole cell array on every event, which is
  fine for 123 chunks and not for 20,000.
- **Phases.** `reading` from mount to the element's `load`, then `done`; `failed` on the element's
  `error`. There is no `developing`: text over 1 MB and video are read only in part, so "every block
  verified" may never come before the file is shown.
- **The manifest cube** appears, lit but not lime, with `xrpl-file-manifest`. It never turns lime on
  this page: the element reports no separate verification of the manifest.
- **Timings.** Always the fast timings (`cloudTimings({ fast: true })`, or reduced motion when the
  visitor asks for it): the cloud is there while the file loads and must not lag behind the read.
  The file page neither reads nor sets the hero's `xf-mosaic-seen`.
- **Motion.** The sway stops 5 s after mount and the camera stays where it stopped. Cubes keep
  appearing and turning lime for as long as the read lasts: that is the read's progress, the kind of
  preload animation WCAG 2.2.2's understanding text treats as essential, and it ends when the file
  is shown.
- **Language.** The page switches language in place (`xf-langchange`), so `mountCloud` gains a way
  to replace its strings while it runs (label, caption and note re-worded at once).
- **Size.** The canvas follows the element's box with a `ResizeObserver` while it is visible: the box
  changes ratio when the cover or the first frame gives the file's size.
- **No reader on this page.** `cloud.mjs` stops importing `hero-read.mjs` (and through it the reader
  bundle) itself; the home page passes `startHeroRead` in. The file page loads no reader code of its
  own.
- **End.** On the element's `load` (image, text, video's first frame, or the download view) or
  `error`, the cloud fades out (instantly under reduced motion), its canvas is hidden and
  `destroy()` stops its frames, and the caption is emptied. Hidden, not only covered: a transparent picture is shown on the
  page itself (`background="transparent"`), and the cloud must not show through it.
- **No turn, no card, no callouts.** `mountCloud` gets an option that turns these off; the home
  page keeps them.
- **Status for screen readers.** With `loader="off"` the element's progress text is hidden. The
  cloud's caption (`role="status"`, polite) carries the reading line instead, and the canvas keeps
  its phase-following `aria-label`.
- **Kept from the hero.** Reduced motion (one still frame per change), the pause off-screen and in a
  hidden tab, both themes (colours from the page's tokens), no motion longer than 5 s that starts by
  itself (WCAG 2.2.2).

### 3. Scale

- Up to **240 chunks**: one cube per chunk, exactly as the hero (the logo's 123 chunks look the
  same as on the home page).
- Above 240: one cube stands for `g = ceil(chunks / 240)` consecutive chunk indices. A group's cube
  appears when the first of its chunks to arrive arrives (not necessarily the lowest index: video
  reads the tail early), stands on the pad of that chunk's ledger and stays there, and is lime when
  every chunk of the group is in a verified block. The note says "each cube is g chunks".
- Why 240: it is one pad's full tower (`COLUMNS` 2 × `MAX_ROWS` 10 × `MAX_LAYERS` 12). A file
  published in one or two ledgers can put every cube on one pad, and `layoutCloud` keeps a pad
  within twelve layers only up to that count; more would leave the frame upwards.
- Budget: a cube is 44 edge points plus up to 20 inside, so 240 cubes are ~15,000 points, under the
  ground's own count.
- Pads: unchanged (`MAX_FILE_PADS` = 10, a pad stands for a run of ledgers when there are more).
  Tower depth follows the cube count, not the chunk count.

### 4. Words

New strings in the shape of `cloudStrings` (`home-strings.mjs`), in all six languages, wherever
`test/languages.test.mjs` checks them for every active language: the canvas label per phase, the reading caption, and the note (with and
without grouping, with and without capped pads). Written with the `writing-site-copy` rules.

## Calls I made (the owner may overrule)

1. **No bytes in `xrpl-file-chunk`.** The hero scatters a cube's inside points by the chunk's bytes;
   on the file page they are scattered by a seed from `(index, ledger)`, and the note lists them
   among what is drawn, not measured. Reason: a public event would otherwise hand out unverified
   bytes.
2. **One version for the site copy and the snippet.** `ELEMENT_VERSION` moves to 1.2.0 for both, so
   the CDN snippet on the home page names 1.2.0. The order is the one already in place for 1.1.0:
   **the owner publishes 1.2.0 to npm before, or together with, the merge into `main`** that
   deploys the site. Until then the branch works in the local preview, which serves the site's own
   copy.
3. **240 cubes** as the cap: one pad's full tower, so any spread of chunks over ledgers fits the
   frame.
4. **Events once per load**, deduplicated by the element, rather than "may repeat".
5. **The sway stops after 5 s**; the cubes keep filling in as the read's progress.
6. **Fast timings always** on the file page; the manifest cube never turns lime there.

## Left to the builder

- Updating the comment in `file-page.mjs` that says the page has no `error` listener on purpose.
- Every `1.1.0` reference, including `web/demo/*`, the READMEs and the tests in `site/test` and
  `web/test`.
- Event and option names beyond those above, internal module layout, how the adapter and the
  grouping are split between `cloud-scene.mjs` and a new module.
- The fade length (under 300 ms), the exact sizing of the canvas to the element's box.

## Done when

- The owner has approved this document.
- `/mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5` in the local preview
  (`node site/serve.mjs`, port 8791) shows the cloud filling while the element reads, then the logo.
- `node --test site/test/*.test.mjs` and `cd web && node --test test/*.test.mjs` are green, with
  tests for the three events (including cache hits and late joins), the adapter, the grouping and
  the strings.
- Committed on `claude/file-page-cloud-361c50`. No push, npm publish, merge or PR without the
  owner's explicit command.
