# `<xrpl-file>`: an embeddable block that shows a stored file in place

Approved in conversation with the owner on 2026-10-29. Decisions taken without the owner are
marked **(builder's call)** so they are easy to challenge.

## Why

A file published with xrpl-file is reachable by its `xrpl-file://` link, but today the only
ways to look at it are the desktop app and a developer script. The owner wants to drop a file
into any web page and have visitors see it there and then:

```html
<xrpl-file src="xrpl-file://testnet/1ED3EA0D7798DA15C850EB6FC5171AA972027F030EA7AEF4EA56BB35C525A13D"></xrpl-file>
```

Who embeds it, in the owner's order:

1. the owner's own project site (planned at `xrpl-file.staticbit.io`, Cloudflare Pages first,
   the owner's own server later), for any visitor;
2. acquaintances, who paste the block into their own sites, possibly several blocks with
   different links on one page, or one link repeated;
3. Telegram and X posts. These platforms do not run third-party scripts, so posts get a link to
   a viewer page that the messenger renders as a card. That is a later project (see "Order of
   work"), not this one; this design only has to make it possible.

## Order of work

Three projects, each standing on the one before. Only the first is designed here.

1. **This document**: the format addition for covers, and the `<xrpl-file>` element with its
   build.
2. The project site on Cloudflare Pages: a static project page and a viewer page per link
   (for example `/f/testnet/<hash>`) built on the element; it also serves the element's script
   for other sites while the repository is private. Designed with the `hallmark` design skill
   (github.com/nutlope/hallmark).
3. Link cards for Telegram and X: a Cloudflare function in front of the viewer page that gives
   the messenger's crawler the file's name, type, size and, when the manifest names one, its
   cover. Crawlers do not run JavaScript, so this is the only server-side piece.

Follow-ups outside this document, recorded so they are not lost:

- **The video platform** (`E:\GIT\XRPL\xrpl-video-platform`, its own repository) writes the
  same `cover` field into its video manifests once section 1 lands in `docs/spec/xrpl-file-v1.md`.
  The owner's rule: one style across both projects. It already writes the cover file itself
  (its spec `2026-10-28-ledger-covers-design.md`); only the pointer moves into the manifest.
  Its own `cm` field in `vp/report` stays for what is already on the ledger.
- **The desktop app** chooses a cover when publishing and repackages MP4 into fragmented MP4 for
  streaming at publish time, reusing the platform's `remux.mjs` inside its WebView. The owner
  accepts that a published video may be repackaged rather than byte-identical to the original.

## Goals

- One script and one tag show a file on any page: images, text and video, with a loader while
  it loads.
- Video starts playing while it is still loading and never needs the whole file in memory.
- Nothing is shown that has not been verified against the manifest's Merkle root.
- Covers written by the desktop app and by the video platform are found the same way, from the
  manifest, by every reader.
- Sites keep control of size and placement with ordinary HTML and CSS.

## Non-goals

- The project site, the viewer page and the link cards (projects 2 and 3).
- Writing covers or repackaging video in the desktop app.
- Audio playback **(builder's call)**: audio types fall back to the download view in this
  version.
- PDF, office documents, archives: shown as a file with a download button.
- Localization. All text in the element is English (owner's decision).
- Publishing an npm package. The build produces a versioned file; where it is hosted is
  project 2.

## Anti-goals (failure even if it works)

- Showing unverified bytes. Text and images appear only after every block verified. Video
  only appends blocks that verified.
- Executing anything from a file. HTML, SVG, XML and Markdown are never rendered as markup. SVG
  is not shown as an image either: SVG can carry script and external references. These types
  fall back to the download view, or for text types to plain text.
- Requests to any host other than the XRPL nodes the element is configured with and the origin
  that served the script.
- One page with ten blocks opening ten connections to the same node.
- A broken or missing cover breaking the file it decorates.

## 1. Format addition: `cover` in the manifest

A change to `docs/spec/xrpl-file-v1.md`. The version stays `1`: the field is optional, and a
reader that predates it ignores it.

### 1.1 Unknown members

Section 4 gains a rule that the spec does not state today. A reader MUST ignore manifest members
it does not recognise. A writer MUST NOT emit members other than those this specification
defines. Both reference readers and the video platform's reader are expected to ignore extra
members already (`System.Text.Json` does by default; the web reader picks named members). Tests
in both reference implementations pin it.

### 1.2 The field

| Field | Required | Meaning |
|---|---|---|
| `cover` | no | hash of the manifest transaction of another xrpl-file file: a picture that stands for this one, 64 hex characters |

Order of fields: `v`, `type`, `size`, `chunk`, `root`, `ledgers`, `blocks`, `lists`, `cover`,
`name`. The 995-byte budget is unchanged. `cover` takes 75 bytes, and name shortening runs after
it, as today.

A writer that sets `cover` MUST first publish the cover as a complete xrpl-file file:

- written by the same account as the file it decorates;
- with the cover's manifest validated in a ledger strictly below the decorated file's
  `ledgers[0]`, so the cover's chunks never fall inside the file's chunk range;
- with `type` `image/webp` or `image/jpeg`, `size` at most 65 536 bytes, and therefore one
  block and `lists` = 0.

These match the video platform's covers exactly, which is the point: one cover format for both
projects. A cover for a video is 960×540 there. The format does not fix dimensions, and the
desktop app follows the platform's 960×540 when it writes covers.

### 1.3 Reading it

A reader that wants the cover fetches the manifest named by `cover` and reads it as an ordinary
file, plus these checks:

- the cover manifest's `Account` equals the decorated manifest's `Account`;
- the cover manifest's ledger is below the decorated manifest's `ledgers[0]`;
- `type`, `size` and `blocks` within the limits above;
- the bytes start with the magic of the declared type: `RIFF....WEBP` for WebP, `FF D8` for
  JPEG.

A cover that fails any check, or cannot be read, is **ignored**: the reader carries on as if
`cover` were absent. It is never an error of the decorated file. A reader MUST NOT follow a
cover's own `cover` field.

### 1.4 Reference implementations

- **Web reader (`web/src/manifest.mjs`).** `decodeManifest` returns `cover` (string or null);
  a value that is not 64 hex characters reads as null, not as a corrupt manifest. Add a
  `readCover(manifestRecord)` (or an equivalent on `XrplFileReader`) that applies 1.3 and
  returns the verified bytes and type, or null.
- **.NET reader (`XrplFile.Core`).** Same: the manifest model exposes the optional cover hash,
  a malformed value reads as absent. Reading the cover bytes in .NET is only needed for the
  later desktop work; this project adds the parsing and the unknown-member tests.
- **Test vectors (`tests/vectors`, `web/test/vectors.mjs`).** Add a vector with `cover` and one
  with an unrecognised extra member. Both decode to the same file as the vector without them.

## 2. The element

### 2.1 Placement and attributes

`<xrpl-file>` is a custom element with a shadow root, so a site's CSS cannot
break its insides. It is `display: block` and behaves like any block element. A site sizes it
with ordinary CSS: classes, `style`, its parent's grid or flex.

| Attribute | Values | Default | Meaning |
|---|---|---|---|
| `src` | an `xrpl-file://` link | — | the file to show; changing it starts over |
| (text content) | an `xrpl-file://` link | — | used when `src` is absent, so the owner's original form `<xrpl-file>xrpl-file://…</xrpl-file>` works |
| `width`, `height` | a number (CSS pixels, as on `<img>`) or any CSS length: `100%`, `50vh`, `auto` | width 100% of the parent, height from the content's proportions | presentational defaults for people who can paste HTML but not CSS. The site's own CSS overrides them, as it does for `<img>` |
| `loading` | `lazy`, `eager`, `click` | `lazy` | when loading starts (2.3) |
| `loader` | `true`, `false` | `true` | `false` keeps the reserved space empty until content is ready, for sites that draw their own placeholder |
| `node` | a `wss://` URL | the built-in endpoints for the link's network | read from this node instead **(builder's call)**. `ws://` is accepted only for `localhost` and `127.0.0.1`, for testing. A link to a network with no built-in endpoints (a numeric network ID) and no `node` shows "This network has no built-in node. Set the node attribute." and fires `error` |
| `theme` | `dark`, `light`, `auto` | `dark` | the colour palette; `auto` follows the visitor's `prefers-color-scheme` (added in 1.1.0) |
| `background` | `transparent` | the theme's box | `transparent` drops the box and the empty-box scrim, so a picture sits on the page itself; it wins over `--xrpl-file-background`. Text, video letterbox, cover backing, overlay and caption keep their backing. Text colours still follow `theme`, so the embedder picks the theme that matches its page (added in 1.1.0) |
| `caption` | `off` | shown | `off` removes the hover caption (added in 1.1.0) |

Styling hooks for the inside:

- CSS custom properties: `--xrpl-file-accent` (progress and buttons), `--xrpl-file-radius`,
  `--xrpl-file-background`, `--xrpl-file-foreground`;
- `::part(loader)`, `::part(caption)`, `::part(message)`.

Events for site scripts, dispatched on the element: `xrpl-file-progress` (`detail`: bytes
done, bytes total, blocks verified, blocks total), `load` (content shown), `error` (`detail`:
the error name from spec section 8 and the English message shown).

### 2.2 Size without jumps

Until proportions are known, the element reserves 16:9 at its width. When the manifest names a
cover, it takes the cover's proportions as soon as the cover is read. Once the image or video
header gives the real size, it adjusts once. An explicit `height` (attribute or CSS) wins over
all of this, and content is fitted inside with `object-fit: contain`.

### 2.3 When loading starts (`loading`)

- **`lazy`** (default): when the element comes near the viewport (`IntersectionObserver`, with
  a margin of one viewport height **(builder's call)**).
- **`eager`**: at once.
- **`click`**: only the manifest and the cover are read up front (one transaction plus at most
  64 KB). The element shows the name, size, cover if any, and a play or open button. Content
  loads on click.

Once loading has started:

- **Images and text** download completely, then show. They need every block verified first.
- **Video** keeps about 30 seconds buffered ahead of the playhead **(builder's call, the
  platform player's value)** and reads further only while playing. A 1.6 GB video that a
  visitor scrolls past costs a few megabytes, not the whole file.
- No autoplay. The visitor presses play.

### 2.4 What it shows, by type

The manifest's `type` decides the view, and the file's leading bytes must agree with it. A
mismatch shows the download view, never a guess.

| Type | View |
|---|---|
| `image/png`, `image/jpeg`, `image/gif`, `image/webp`, `image/avif` | `<img>` from a blob URL once every block verified |
| `video/mp4`, `video/webm` (and `video/quicktime` holding H.264) | video player (section 3) |
| `text/plain`, `text/csv`, `text/markdown`, `application/json`, `text/html`, `text/xml`, other `text/*` | text in a monospace, scrollable box, set with `textContent`, decoded as UTF-8 with replacement characters. Markup is shown as source, never rendered. Above 1 MB only the first 1 MB is shown, with a note and the download button **(builder's call)** |
| `image/svg+xml` and everything else | download view: name, type, size, network, Download button |

### 2.5 States (as on the approved mockup)

The mockup the owner approved is `2026-10-29-xrpl-file-element-mockup.html` beside this
document. It shows all six states and the three caption variants; the owner chose C.

1. **Loading**: spinner, percent, bytes done of total, blocks verified of total, a thin
   progress bar at the bottom.
2. **Video**: cover if the manifest has one (otherwise the first frame once available), play
   button. Once playing, the browser's native `<video controls>` take over: seeking, volume,
   fullscreen, picture-in-picture, keyboard and accessibility come with them, and their
   buffered indicator is the "loaded" part of the mockup's bar **(builder's call)**. No custom
   player. The element's own thin bar shows only before playback starts and while the next
   block is being read and verified (a "buffering" overlay).
3. **Image**.
4. **Text**.
5. **Download view**.
6. **Error**: one short English sentence, with the spec's error name in the `error` event. For
   example "This file did not pass verification. Block 12 does not match the file's fingerprint.
   Nothing was shown." or "The node does not have the history this file needs."

**Caption on hover (owner's choice C).** The file name, size and network, a verified mark, and
the `xrpl-file://` link with a copy action. It appears on hover and keyboard focus, and on touch
devices toggles with a tap. On a video it does not cover the player's own controls.

### 2.6 Downloads

The Download button saves the verified bytes under the manifest's `name`, or
`xrpl-file.<ext>` when the manifest has none.

- **Chromium** (`showSaveFilePicker`): writes block by block as each verifies, so size is not
  bounded by memory.
- **Elsewhere**: assembles a Blob in memory up to 2 GiB **(builder's call)**. Above that it
  says the file is too large to save in this browser.

A download never writes a block that has not verified. If one fails, the partial file is
discarded where the API allows, and the error state shows.

### 2.7 Many blocks on one page

All elements on a page share one connection per node set and one history cache keyed by
writer account and ledger range. Several elements with the same link share one read. Pages
with several different links queue their reads over the shared connection. Visible elements
go first, the order between them is the builder's.

Where this lives **(builder's call)**: a page-level layer in the element's own code, not a
change to the reader. Today `ForwardHistory` lives inside one `XrplFileReader` with one cursor
and mirrors the .NET reader rule for rule; this project does not change it or the .NET side,
beyond the additive cover parsing of section 1. The page layer:

- holds one `LedgerClient` per node set behind a reference count, closing it when the last
  element is removed (`LedgerClient.close()` is final, so elements never call it directly);
- reads blocks by their block-list entry (`XrplFileReader.block(record, entry)` already takes
  one), so a seek or a cover read never fights a forward cursor;
- keeps the reader-cache rule of the streaming spec (S26): it answers from a ledger range only
  when it knows that range is complete for that account.

## 3. Video

Playback uses Media Source Extensions: `ManagedMediaSource` where present (iOS Safari),
otherwise `MediaSource`, as the video platform's `streamer.mjs` does. A block is appended only
after its Merkle hash matches the block list. A block that fails stops reading. What is already
buffered keeps playing, and the error state shows when playback reaches the gap.

How bytes reach the source buffer depends on the container, detected from the leading bytes:

- **Fragmented MP4** (`moov` then `moof`/`mdat`; the video platform writes these, with `sidx`
  after `moov`): appended as-is. With a `sidx`, seeking reads from the block that holds the
  target fragment. Without one, seeking works only within what is buffered.
- **Plain MP4** (`moov` and one `mdat`): fragmented in the visitor's browser with mp4box.js,
  the same version the video platform pins (0.5.4), loaded on demand from the same origin as
  the element's script. The element can read any block first, so it reads the block holding
  `moov` wherever it sits. A `moov` at the end of the file does not mean downloading the whole
  file. Seeking maps the target time to a byte offset through `moov`'s sample tables and reads
  from that block.
- **WebM**: appended as-is. When the file has `Cues` (ffmpeg writes them, usually at the end),
  the element reads the block holding them the same way it finds `moov`, and seeks through
  them. Without `Cues`, seeking works only within what is buffered.

Seeking anywhere is a first-class case, not an edge case: the owner expects to jump straight
to the middle of a video. A seek outside the buffer drops the read-ahead queue, finds the block
holding the keyframe at or before the target, reads it (and the next blocks concurrently), and
playback resumes from that keyframe. The visitor waits about one block read, a few seconds on
testnet, under the buffering overlay. Seeking back into a part already watched replays from the
buffer while it is still held, and re-reads otherwise.
- **Anything `MediaSource.isTypeSupported` refuses** (HEVC on browsers without it, for
  example): the download view, with a line saying this browser cannot play it here.

**Read speed against bitrate.** One block is 1024 transactions, about 1 MB, paged from
`account_tx` 400 records at a time. The desktop app measured about 7 MB read back in 16 s on
testnet (about 0.45 MB/s on one sequential read). A Full HD video runs 0.6–1 MB/s, so one
sequential reader can fall behind. The element therefore reads up to three blocks ahead
concurrently, each walking from its own entry's `l` **(builder's call, to be tuned against
testnet)**. When playback still catches up with the reads, the native player waits and the
element shows its buffering overlay. There is no failure threshold: a slow node means pauses,
not an error. The owner is told this is the expected behaviour for high-bitrate video on
public nodes.

Memory: the element keeps only verified block indices and what the source buffer holds. On
`QuotaExceededError` it removes buffered ranges further than about two minutes from the
playhead and re-reads them if the visitor comes back.

## 4. Build and distribution

- Source stays in `web/src` as plain ES modules. The element lives beside the reader
  (`web/src/element/` or similar, builder's choice), reusing `XrplFileReader`, `LedgerClient`
  and `tree`.
- The build writes, under `web/dist/<version>/`:
  - `xrpl-file-element.js`: one classic script that defines the element when loaded, usable
    as `<script src="…/xrpl-file-element.js" defer>` with no module support needed;
  - `mp4box.min.js` beside it, loaded only when a plain MP4 is played;
  - a file with the SHA-384 integrity hashes of both, for the `integrity` attribute.
- The version is in the path, so a site that embeds `…/1.0.0/…` never changes under it. No
  build tool beyond Node, which the repository already uses for tests. The bundler is the
  builder's choice; a small script in the style of the existing fiddle bundler is enough.
- A demo page `web/demo/index.html` shows every state against testnet links that the owner has
  already published, and a page with many blocks.
- The README gets a short "Embed a file" section with the two-line snippet.
- Sites that set a Content Security Policy need `connect-src` for the node URLs and `media-src
  blob:` / `img-src blob:`. The README says so.

## 5. Testing

- **Node (`node --test`, existing setup)**: manifest `cover` parsing and the 1.3 checks against
  fakes; attribute parsing; type-to-view decisions including magic-byte mismatches; container
  detection; `moov` location in plain MP4; sample-table offset lookup for seeking; download
  assembly refusing unverified blocks; the shared client queue.
- **.NET (xUnit v3, existing projects)**: `cover` parsing, malformed `cover` read as absent,
  unknown members ignored; vectors.
- **Browser**: Media Source Extensions, `IntersectionObserver` and the shadow DOM exist only in
  a browser. The builder checks the demo page in a real browser (the built-in browser pane is
  available) against testnet: each state, a fragmented MP4 and a plain MP4 with `moov` at the
  end playing while loading, seeking in both, a page with ten blocks opening one connection. A
  headless browser test harness is the builder's call; nothing in the repository provides one
  today.
- **Fixtures on testnet.** No manifest on the ledger carries `cover` yet, and only the .NET
  library writes. The .NET writer therefore gains an optional cover hash for the manifest it
  writes (no UI), enough to publish a cover file and then a file naming it. Fixtures are
  published to testnet only, with faucet-funded throwaway accounts.

  The owner supplied two source files and asked for the rest to be generated from them:
  `C:\Users\Evril\Downloads\en-back.mp4` (9.2 MB, H.264, plain MP4 with `moov` at the start)
  and a 1600×1600 WebP picture (178 KB). The fixture set, built with ffmpeg and kept out of the
  repository (only their links go into the demo page):

  | Fixture | Made from | Tests |
  |---|---|---|
  | picture, WebP 178 KB | as supplied | image view, multi-block image |
  | cover, WebP 960×540, ≤ 64 KB | the picture, centre-cropped | cover path |
  | text, a few KB of UTF-8 including Cyrillic | written by hand | text view |
  | text, a little over 1 MB | generated | the 1 MB cut-off |
  | HTML source and an SVG | written by hand | shown as source / download view, never executed |
  | MP4, `moov` at start | `en-back.mp4` as supplied | plain MP4 fragmented on the fly |
  | MP4, `moov` at end | `en-back.mp4` remuxed with `-c copy` | reading the tail block first |
  | fragmented MP4 with `sidx` | `en-back.mp4` through the video platform's `remux.mjs` (or ffmpeg's fragmenting flags if that is impractical) | as-is append, seeking |
  | WebM (VP9/Opus) | `en-back.mp4` transcoded | as-is append |
  | MP4 with `cover` | the `moov`-at-end MP4, published after the cover, its manifest naming it | poster, proportions before load |
  | MP4 with a broken `cover` (cover of the wrong account) | the same MP4 again | cover ignored, video unaffected |
  | HEVC MP4 | `en-back.mp4` transcoded to H.265 | download view on browsers without HEVC |
  | a large MP4, several hundred MB, `moov` at end | `en-back.mp4` looped | read speed, buffering overlay, seeking far ahead |

## Left to the builder

- File layout under `web/src`, the bundler script, internal module boundaries.
- Visual detail of the loader and caption, within the approved mockup (dark background,
  accent colour, thin bar).
- The read queue's priorities between visible elements.
- Exact English wording of messages, short and plain.
- Whether `readCover` lives on `XrplFileReader` or beside it.
