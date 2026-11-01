# `<xrpl-file>` element: acceptance on testnet

Plan: `docs/superpowers/plans/2026-10-29-xrpl-file-element.md`, Task 15.
Spec: `docs/superpowers/specs/2026-10-29-xrpl-file-element-design.md`.

## Environment

| Item | Value |
|---|---|
| Browser | Claude desktop app browser pane, Chromium 152.0.7977.167 (`Claude/4.4212.0`), Windows 11 |
| Pane | Hidden for the whole run: the page ran as a background page (see "Limits of this run") |
| Node | `wss://s.altnet.rippletest.net:51233` (the built-in testnet endpoint) |
| Server | `node web/demo/serve.mjs` on `127.0.0.1:8790` |
| Script | `web/dist/1.0.0/xrpl-file-element.js`, loaded with its SRI hash from `integrity.json` |
| Commit | `213313a` (the branch head before this report) |
| Tests at that commit | web `node --test test/*.test.mjs`: 489 pass, 0 fail. .NET at `5e5a587`: Core 938, Keys 255, App.Core 821, 0 failed |

## Fixtures

Published to testnet by `tools/XrplFile.Fixtures` from the owner's two source files. The
`webm` fixture was re-published twice, after its encoding changed (see row 10).

| Key | Link |
|---|---|
| cover | `xrpl-file://testnet/17D6A6AC99B7427EB4BFAB8FAECF5553FB08148CD107EB3652197E11C9F6A095` |
| cover-b | `xrpl-file://testnet/5E52C922B68298A29F58775823E2FEA364A9FEABD857418C744DFFC2FC7B21E5` |
| picture | `xrpl-file://testnet/32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59` |
| picture-blocks | `xrpl-file://testnet/8C913F9B1F11133AD6D059F1A17E775A004F5B471A0A9DD2D96BF9159F597354` |
| text | `xrpl-file://testnet/C5E3796206EF47EEF661067D7E8FDC74E2FCEBDF839D133E2E1F364444A722EC` |
| text-large | `xrpl-file://testnet/F1C980073EDE371B8F54C5D5CB81598A9787176789E1E0E67B2D18455517AB39` |
| html | `xrpl-file://testnet/BACE4938098AE5ECD4EF82FB18B0EACAB34441B93834FD301DD6E70CE0E3F2FB` |
| svg | `xrpl-file://testnet/01327E188DFEDB1FD579E8A87D6D2839D0E002758A3D26A14F97BEA939E0DDDD` |
| mp4-start | `xrpl-file://testnet/4113C342F5C205D7B63E0A5AFC44A36A273E22BCF8BD669F293C39EADF7E4264` |
| mp4-end | `xrpl-file://testnet/59E85235890FA10B3FFA76262BA5B4C37939749F3730ED6C44C1D52A53130E46` |
| fmp4 | `xrpl-file://testnet/A089C73C7F353B4F59F17EC270B3E20EA4A59700E63C99EDFF9E3D1879E5E6BB` |
| webm | `xrpl-file://testnet/CE45643F1A200CD7DA42D9D1A0EBDBE55489D10B1256383E7D695EFFAF3BF7B5` |
| mp4-cover | `xrpl-file://testnet/47C0274B0C88211B2D945C4ECCD9E2FE9CCF158B9E45323370B3A6D939FD40EB` |
| mp4-broken-cover | `xrpl-file://testnet/340A34D57959806A1DB7AD48C68C355E16CBB137323F66306CBD65ABA49459BA` |
| hevc | `xrpl-file://testnet/33C33A0778B044EE510E7DB723A34261448F82DEB4177C24753C6B0B3D5E9BC6` |
| large | `xrpl-file://testnet/CAE00EF2575E0F95E225C31E76C9B6F44F3ACCEDEB0A5F9EA42DCA59C651448E` |

## Verdicts

| # | Check | Verdict | Evidence |
|---|---|---|---|
| 1 | Loader on `large` (eager) | Pass | "0% 1.0 of 302.4 MB · 1 of 296 blocks verified", `role="progressbar"` with `aria-valuenow`; `xrpl-file-progress` bytes rise monotonically |
| 2 | `picture`, `picture-blocks` | Pass | nothing until verified, then the picture; 16:9 before, `1600 / 1600` after; `load` once each. `picture.jpg` |
| 3 | `text` | Pass | Cyrillic intact; `<b>not bold</b>` shown as source, no `<b>` element in the shadow root |
| 4 | `text-large` | Pass (content) | "Showing the first 1 MB of 1.1 MB." and Download; saved bytes SHA-256 `6ab69731…4512` equal the source (Blob path; see 20) |
| 5 | `html`, `svg` | Pass | `html` as source text (286 characters); `svg` download view; no alert dialog on any page |
| 6 | `hevc` | Pass (plays) | this Chromium supports `hvc1` (`isTypeSupported` true): the video plays; the download-view line was therefore not exercised here |
| 7 | `mp4-start` | Pass | plays while loading; `mp4box.min.js` requested once, from `127.0.0.1:8790/dist/1.0.0/` |
| 8 | `mp4-end` | Pass | progress shows block 0, then the `moov` block (8), then blocks 1–7 |
| 9 | `fmp4` | Pass | plays; `mp4box` script never requested (only the loader module on the unbundled page) |
| 10 | `webm` | Pass (after fixes) | plays while loading; seek to 0:45 through Cues resumes and keeps playing. Failed twice before: see "Fails" |
| 11 | Seeking | Pass | `fmp4` to 0:40: 0.27 s; `mp4-end` to 0:45 outside the buffer: 4.1 s, keeps playing; `large` to 3/4: 4.7 s under "Buffering…"; back into watched time: 82 ms, no new reads. `webm` see 10 |
| 12 | `large` jump to 3/4 at 0:04 | Pass | resumed at 23:56; 12.6 MB of 302.4 MB read in total |
| 13 | Rapid scrubbing on `large` | Pass | five seeks in 2 s (5:00, 15:00, 10:00, 20:00, 7:30): resumed at 7:30; no buffered range for the intermediate targets; 0 corrupted frames |
| 14 | `mp4-cover`, `loading="click"` | Pass | cover sets 960:540 before load; poster on the video; click copy: 0 block reads and "testnet" without the mark before the click, 3 reads and "✓ verified · testnet" after |
| 15 | `mp4-broken-cover` | Pass | no poster, plays, no `error` event |
| 16 | Caption | Partial | Copy link writes the exact link and shows "Copied"; the link is the caption's `title`; on video the caption sits at the top (8 px), on images at the bottom; a touch tap toggles it on and off. Hover/focus visibility not verified: `:focus-within` matches, but the opacity transition does not advance in a hidden pane. `caption.png` shows it on hover |
| 17 | Sizing | Pass | `width="320"` → 320 px; `height="50vh"` → 384 of 768 px; a plain page CSS rule overrides both (210×140); the grid of three fills its 320 px cells |
| 18 | `loader="false"` | Pass | no loader visible at any sample until the picture appeared (it came from the page's block cache in 0.9 s) |
| 19 | Errors | Pass with a note | "This network has no built-in node. Set the node attribute." and "This is not an xrpl-file link." as written; the 64-zero hash gives `NodeLacksHistory` ("The node does not have the history this file needs."), not `ManifestNotFound` — the reader's rule: a node without full history cannot prove a transaction absent. `errors.png` |
| 20 | Download `svg` | Pass (content) | `drawing.svg`, 268 bytes, SHA-256 `34800d99…5d51` equals the source, through `saveFile`'s Blob path against testnet. The native save dialog of the picker path cannot be driven from automation. The owner checked the picker path by hand in desktop Chrome and Opera: it hung on a OneDrive-redirected Documents folder until `8148840` made it open in Downloads, and works since |
| 20a | Download in a cross-origin iframe | Pass (after a fix) | `svg` fixture in an iframe on another origin (wrapper on `localhost:8793`, element page on `127.0.0.1:8792`), three cases: A plain iframe, B `sandbox="allow-scripts allow-same-origin"`, C B plus `allow-downloads`. In all three `showSaveFilePicker` threw SecurityError ("Cross origin sub frames aren't allowed to show a file picker"), the element fell back to the Blob path, and the Blob held 268 bytes with SHA-256 `34800d99…5d51` (the source). In B the browser blocks downloads from a sandboxed frame without `allow-downloads`, yet the element said "Saved."; it now says "Download started." on the Blob path and keeps "Saved." for the picker path, where the file is written and closed. Copy link: in A, B and C `navigator.clipboard.writeText` throws NotAllowedError ("blocked because of a permissions policy"), so the element showed "Copy failed"; it now falls back to `execCommand('copy')` on a temporary textarea in its shadow root, and in all three the button said "Copied" and the system clipboard (PowerShell `Get-Clipboard`, reset to a marker before each case) held exactly the link. `allow="clipboard-write"` is not needed |
| 21 | `many.html` | Pass (after a fix) | first run: 8 WebSockets (a race in `LedgerClient.connect`, fixed in `57a045c`); after: `socketCount` 1, `tx` 9 = 8 distinct manifests + 1 cover (`picture` three times costs one) |
| 22 | Hosts | Pass | HTTP only to `127.0.0.1:8790`; one WebSocket to the built-in testnet endpoint; the build test pins that the bundle names no other host |
| 23 | `snippet.html` | Pass | the two lines as written: script with `defer`, picture shown |
| 24 | Lazy | Partial | fresh load at the top, 20 s: only the eager `large` started, nothing below the fold. Starting on approach was seen after scrolling in the active tab but could not be isolated in a hidden pane |
| 25 | Memory | Pass | `usedJSHeapSize` 70–110 MB over 270 s of `large`. The browser itself paused the muted background video in two stretches; the element never calls `pause()` |
| 26 | Before play | Pass | `large` and the plain MP4s stop after init plus the first media block (`large`: 0–10.1 s buffered); `fmp4` after one block |
| 27 | `head.html` | Pass | script in `<head>` without `defer`, link as text content, picture shown |

Rows 23 and 27 switched the element to `loading="eager"` from the console, because a hidden
pane does not run the lazy observer on load.

## Fails

**Row 10, `webm`.** Failed twice before it passed:

1. ffmpeg stores Opus block timecodes shifted by the track's `CodecDelay` (pts 61 ms is
   written as 68 ms) but interleaves by pts, so inside a Cluster an audio block at 68 ms
   precedes a video block at 67 ms. Chromium's MSE requires non-decreasing block
   timecodes within a Cluster and refused the file ("Got a block with a timecode before the
   previous block"). Fixed in `213313a`: the WebM feed re-orders blocks inside each Cluster
   after verification. A decoder rejection before the first frame now shows the download
   view with "This browser cannot play this video here." (`b6f7d4a`).
2. After the fix MSE accepts the stream (0–8 s buffered, first frame shown, `load`), then
   reports `PIPELINE_ERROR_DECODE: video decode error!`. Plain `<video>` playback of the same
   verified bytes, without MSE, fails the same way after two frames; ffmpeg decodes the
   file without errors. The owner saw the same in desktop Chrome and Opera. Cause: the
   source is full-range `yuvj420p`, and VP9 tagged full range decodes two frames in Chromium
   and then fails; every full-range variant (720p, landscape, no audio, no row-mt) failed,
   the same clip converted to TV range played. `tools/fixtures/make-media.sh` now encodes the
   WebM in TV range, and the `webm` fixture was re-published. The element shows "This
   browser could not decode this video." for such a stream, which is accurate.

**Row 21** failed before `57a045c` (8 WebSockets) and passes after it.

**Plain MP4** did not start at all before `427289d`: the loader's `delete window.MP4Box`
threw on the non-configurable global that the mp4box script declares. Found in this run, fixed,
and re-checked (rows 7, 8, 11).

## Small sizes

Added after the run, when the owner saw the download view, loader, error and caption spill out of
100–200 px boxes. The element now lays itself out by its own width (container queries): full from
240 px, compact from 120 to 239 px, icons under 120 px, and in any box under 56 px high (a width-independent addition to the owner's thresholds, so a short explicit `height` never clips). Checked on
`demo/index.html#small` at f71d341 (re-checked after the review fixes) with the svg download view, `large` (loader), a bad link (error),
`picture` (caption) and `text`, at widths 50, 100, 160 and 300 px and at 160 px with
`height: 40px`:

| Check | Verdict | Evidence |
|---|---|---|
| Nothing escapes the box | Pass | for all 25 elements, mid-load and loaded, no visible shadow-DOM node's bounding box extends past the host's (max 0 px; the first build had the 26 px icon button 1.9 px past a 50×28 box, fixed in `5e25bd0`) |
| Icon tier, 50 px | Pass | download icon only; loader: percent and bar; error: warning icon; picture: ✓ badge; text scrolls |
| Icon tier, 100 px | Pass | "SVG" badge and download icon; loader: percent and bar |
| Compact, 160 px | Pass | name with ellipsis, size, "Save"; loader: "1%", "3.1 of 302.4 MB", bar; error: "Bad link" |
| Full, 300 px | Pass | as before |
| `height: 40px` | Pass | icon tier in all five views, inside the box |
| Caption visuals on hover at small widths | Not verified | hidden pane: the opacity transition does not advance (as row 16) |

Screenshots: `small-sizes-loading.jpg` (mid-load), `small-sizes-loaded.jpg`, `small-sizes-height-40.jpg`.

## Limits of this run

- The browser pane stayed hidden, so the page ran as a background page: no rendering updates,
  no CSS transitions, no lazy start on load. Rows 16 (hover/focus visuals) and 24 (start on
  approach) need a visible pane.
- `resize_window` to the mobile preset was not used; the touch tap was driven with touch
  pointer events.
- The `showSaveFilePicker` path needs a user gesture and a native dialog; only the Blob path
  was driven, against the same handles.

## Screenshots

- `picture.jpg`: the picture fixture after verification.
- `caption.png`: the caption on hover, variant C.
- `errors.png`: the error states.
