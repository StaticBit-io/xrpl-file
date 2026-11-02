# `<xrpl-file>` element: light theme and public colours

The element was dark-only: four public custom properties, every other colour hard-coded for a dark
box. It now has a `theme` attribute (`dark`, the default and the original look; `light`; `auto`) and
every colour it paints is a public `--xrpl-file-*` property. This report records the palette, the
contrast of every pair as rendered, and what was checked in a browser.

## How it works

- `web/src/element/template.mjs`: each theme is a palette of private `--t-*` properties on `.frame`.
  The public properties are read over it (`--accent: var(--xrpl-file-accent, var(--t-accent))`), so a
  value the site sets wins in every theme. `.frame[data-theme="light"]` and, inside
  `@media (prefers-color-scheme: light)`, `.frame[data-theme="auto"]` swap in the light palette; the
  same text is written into both rules, so they cannot drift (a test compares them).
- `web/src/element/attributes.mjs`: `parseTheme` trims and lower-cases the attribute; anything other
  than `dark`, `light` or `auto` is `dark`. `element.mjs` observes `theme` and writes the result to
  `data-theme` on `.frame`. A theme change only restyles; it does not start a new read.
- Media surfaces sit on a picture or a video, not on the page, and keep the dark palette in every
  theme: the caption, the loader and the failure message over a video, and the click-to-open view over
  a cover. They re-declare the role properties (`--fg`, `--muted`, `--track`, `--danger`, `--success`,
  `--accent`, `--on-accent`) to the dark values, still overridable by the site. The view over a cover is
  marked by a `covered` class set in `#renderIdle`, because that view needs a dark backdrop
  (`--xrpl-file-media-background`) for the dimmed cover to stay dark on a light frame.
- `color-scheme` follows the theme, so the text view's scrollbar matches it.

## Public properties

The full list with both palettes is in the README, section "Embed a file". In short:

| Follows the theme | Same in both themes |
|---|---|
| `--xrpl-file-accent`, `--xrpl-file-on-accent`, `--xrpl-file-background`, `--xrpl-file-foreground`, `--xrpl-file-muted`, `--xrpl-file-track`, `--xrpl-file-danger`, `--xrpl-file-text-background`, `--xrpl-file-text-foreground`, `--xrpl-file-text-bar` | `--xrpl-file-media-background`, `--xrpl-file-overlay`, `--xrpl-file-overlay-foreground`, `--xrpl-file-overlay-border`, `--xrpl-file-overlay-track`, `--xrpl-file-overlay-success`, `--xrpl-file-overlay-danger`, `--xrpl-file-scrim`, `--xrpl-file-radius` |

The four original properties keep their names and meaning. A test fails when a colour literal appears
in the styles outside a palette entry or the default of a public property, and another when a public
property is missing from the README.

## Changes to the dark look

`dark` is the default and keeps the original look, with these deliberate exceptions:

| What | Before | After | Why |
|---|---|---|---|
| Text view | always light (`#f7f7f5` / `#222`, bar `#e6e6e1` / `#444`) | follows the theme: dark `#1b1e23` / `#e8e8ea`, bar `#262a30` | the task: the text view follows the theme |
| "Copy failed" and a failed video | `#ff8a8a` | `#ffa0a0` | `#ff8a8a` on the caption over a white picture is 4.07:1, under AA |
| Focus ring of the accent buttons in the icon tier | accent ring drawn inside an accent button: 1:1, invisible | the on-accent colour, 7.18:1 dark, 5.90:1 light | the ring was not visible at all |
| Focus ring of the play button in the icon tier | accent inside the dark disc | the overlay foreground | in the light theme the accent on the disc is 1.64:1 |
| Spinner and progress track | ring `.15`, bar `.12` white | one `--xrpl-file-track`, `.15` white | one property for one role |
| Play button | `rgba(0,0,0,.6)` disc, `#fff` glyph | `--xrpl-file-overlay` (`.72`) and `--xrpl-file-overlay-foreground` (`#ddd`) | one overlay colour; the glyph stays 13.6:1 or more |
| Type badge border (icon tier) | `rgba(255,255,255,.35)` | `--xrpl-file-muted` | it is a frame-surface edge and follows the theme |
| Click-to-open view over a cover | cover dimmed over `#14161a`, name `#e8e8ea`, size `#9a9ca3` | dimmed over `#000`, name and size `#ddd` | the same backdrop and text in both themes; by hand, the size line over a white cover came to about 3:1 before (not measured on the old build) |

The scrim without a cover stays in the dark theme (`--t-scrim`) and is `none` in the light one.

## Contrast

Measured on render, not from the tokens: `web/demo/themes-harness.html` draws every view from the
real `TEMPLATE` and `STYLES` with fixed content, and `web/demo/themes-measure.js` reads each visible
text's computed colour and composes everything painted under it at that point (`elementsFromPoint`,
backgrounds, gradients, the dimmed cover and the picture's own pixels). Pictures under the caption
were the worst cases: pure white and pure black, plus a mid-tone photo for the cover. Sizes 320×180,
200×150, 100×100 and 300×48, so all three tiers and the height rule. 690 rows per run; `auto` was
run under `prefers-color-scheme: light` and `dark` and matched the light and dark rows pair for pair.

Thresholds: 4.5:1 for text (none of it is large text), 3:1 for non-text UI (WCAG 2.2 SC 1.4.11):
the spinner arc and the progress fill against the track and the background, the focus ring against
what it is drawn on, the type badge border.

### Lowest ratio per theme

| Theme | Text | Non-text UI |
|---|---|---|
| dark | 4.74:1, "Copy failed" `#ffa0a0` on the caption over a white picture `#474747` | 3.52:1, focus ring `#4da3ff` around the copy button on the caption `#474747` |
| light | 4.74:1, the same caption pair (the caption is dark in both themes) | 3.52:1, the same caption pair |
| light, the frame itself | 5.79:1, muted `#5b5f6b` on `#f3f4f6` | 3.71:1, accent `#0b62c4` against the track `#cccdcf` |
| dark, the frame itself | 6.61:1, muted `#9a9ca3` on `#14161a` | 4.41:1, accent `#4da3ff` against the track `#37393c` |

No required pair fails.

### Required pairs

Each row is the lowest ratio of that pair across sizes and states.

| Theme | Pair | Foreground | Background | Ratio |
|---|---|---|---|---|
| dark | names, percentage, badge | `#e8e8ea` | `#14161a` | 14.80 |
| dark | sizes, progress details, note, status | `#9a9ca3` | `#14161a` | 6.61 |
| dark | error message | `#ff8a8a` | `#14161a` | 7.98 |
| dark | Download / Open / Save | `#06121f` | `#4da3ff` | 7.18 |
| dark | text file | `#e8e8ea` | `#1b1e23` | 13.66 |
| dark | text bar note and status | `#e8e8ea` | `#262a30` | 11.78 |
| dark | progress fill, spinner arc vs track | `#4da3ff` | `#37393c` | 4.41 |
| dark | progress fill vs background, focus ring outside | `#4da3ff` | `#14161a` | 6.90 |
| dark | focus ring on the text bar | `#4da3ff` | `#262a30` | 5.49 |
| dark | focus ring inside an accent button (icon tier) | `#06121f` | `#4da3ff` | 7.18 |
| dark | type badge border | `#9a9ca3` | `#14161a` | 6.61 |
| light | names, percentage, badge | `#1b1d22` | `#f3f4f6` | 15.32 |
| light | sizes, progress details, note, status | `#5b5f6b` | `#f3f4f6` | 5.79 |
| light | error message | `#b3261e` | `#f3f4f6` | 5.94 |
| light | Download / Open / Save | `#ffffff` | `#0b62c4` | 5.90 |
| light | text file | `#1f2328` | `#ffffff` | 15.80 |
| light | text bar note and status | `#1f2328` | `#e6e8eb` | 12.87 |
| light | progress fill, spinner arc vs track | `#0b62c4` | `#cccdcf` | 3.71 |
| light | progress fill vs background, focus ring outside | `#0b62c4` | `#f3f4f6` | 5.36 |
| light | focus ring on the text bar | `#0b62c4` | `#e6e8eb` | 4.81 |
| light | focus ring inside an accent button (icon tier) | `#ffffff` | `#0b62c4` | 5.90 |
| light | focus ring inside the play button (icon tier) | `#dddddd` | `#444445` | 7.14 |
| light | type badge border | `#5b5f6b` | `#f3f4f6` | 5.79 |
| both | caption name, "Copy link", over a white picture | `#dddddd` | `#474747` | 6.80 |
| both | verified mark, network, "Copied", over a white picture | `#6fd08c` | `#474747` | 4.87 |
| both | "Copy failed", over a white picture | `#ffa0a0` | `#474747` | 4.74 |
| both | focus ring around the copy button, over a white picture | `#4da3ff` | `#474747` | 3.52 |
| both | name over a white cover (lowest point of the scrim) | `#dddddd` | `#525252` | 5.72 |
| both | play glyph | `#dddddd` | `#444445` and darker | 7.14 |
| both | video loader text, failed video text | `#dddddd`, `#ffa0a0` | `#000000` | 15.46, 10.79 |
| both | video progress fill and spinner arc vs track | `#4da3ff` | `#262626` | 5.75 |
| both | focus ring around the play / open button over a cover | `#4da3ff` | `#3c3c3c` | 4.18 |

### Informative, not required

- **Track against the background**: dark 1.56:1, light 1.45:1, over a video 1.39:1. The progress
  information is the fill, which meets 3:1 against both the track and the background, and the
  percentage is also text; the bar spans the full width of the box, so its extent needs no outline.
  Both together at 3:1 would need the accent at 9:1 against the background, which the unchanged dark
  accent (6.90:1) cannot give.
- **Button fills and borders against what surrounds them**: the play disc on a dark frame (1.05:1),
  the copy button's border on a white picture (2.92:1), the copy disc in the icon tier on a black
  picture (1:1). Each button is identified by its label or glyph, which is measured above against the
  button's own fill, so 1.4.11 does not ask for the edge.

## Browser check

Claude desktop app browser pane (Chromium), Windows 11, pane hidden for most of the run; the server
was `node web/demo/serve.mjs` on `127.0.0.1:8790`, the element loaded from `web/dist/1.0.0/` with its
SRI hash. The live pages read testnet fixtures from `run/fixtures/links.json`.

Checked:

- Live, `web/demo/themes.html`, every combination of page (light, dark) and theme (dark, light, and
  auto under emulated `prefers-color-scheme: dark` and `light`): video, picture with the hover caption,
  text view with its bar, download view, error, click-to-open over a cover and without one, and the
  small sizes (200 px compact, 100 px icon, 300×48 under the height rule).
- Live loader mid-read in the light theme and in the dark theme, and the "Buffering…" overlay over a
  video in the dark theme.
- `auto` follows a change of `prefers-color-scheme` while the page is open, without a reload.
- A theme change keeps the element in its view (no new read); `theme="sepia"` and no attribute give
  `dark`, `" LIGHT "` gives `light`; a site's `--xrpl-file-accent` and `--xrpl-file-background` on the
  element win over the light theme and the theme comes back when they are removed.
- Keyboard focus with Tab: the caption copy button (accent ring outside), the download button in the
  light theme (`#0b62c4` ring, offset 2 px) and the icon-tier download button (white ring inside,
  offset -2 px), each matching `:focus-visible`.
- The harness: every view at 320×180, 200×150, 100×100 and 300×48 in both themes, on a light and on
  a dark page; the measured table above.

Not checked:

- Firefox and Safari, and engines without container units or `color-scheme`.
- Forced colours (Windows high contrast).
- A light-theme "Buffering…" overlay on a live video; it is measured in the harness over a black
  video backdrop, the same surface.
- The failed-copy state live (it needs the clipboard to refuse); measured in the harness.
- Screenshots were taken with the pane hidden and are downscaled to 800 px wide.

Seen in passing, not part of this change: at 300×48 the text view shows only its bar, because the bar
wraps to two lines and leaves no height for the text. It is the same in both themes and was so before.

## Screenshots

| File | What |
|---|---|
| `harness-dark-theme.jpg`, `harness-light-theme.jpg` | every view at 260×160, light page |
| `harness-on-dark-page.jpg` | both themes on a dark page |
| `harness-small-sizes-dark.jpg`, `harness-small-sizes-light.jpg` | 200×130, 100×100 and 300×48 |
| `live-light-page-light-theme.jpg` (+ `-small`) | live, light page, `theme="light"`, caption on hover |
| `live-dark-page-light-theme.jpg` (+ `-small`) | live, dark page, `theme="light"` |
| `live-light-page-dark-theme.jpg` (+ `-small`) | live, light page, the default theme |
| `live-dark-page-dark-theme.jpg` (+ `-small`) | live, dark page, the default theme |
| `live-auto-prefers-dark-on-light-page.jpg` | `theme="auto"`, visitor prefers dark |
| `live-auto-prefers-light-on-dark-page.jpg` | `theme="auto"`, visitor prefers light |
| `live-light-loader.jpg` | the loader mid-read, light theme (an earlier layout of the demo page) |
| `live-dark-video-buffering.jpg` | the loader mid-read and "Buffering…" over a video, dark theme |
| `live-light-focus-ring.jpg` | keyboard focus on the download button, light theme |

## Tests

`cd web && npm test`: 575 pass, 0 fail (571 before; new: theme parsing and fallback, no colour
outside a palette or a public default, the light palette under both selectors with the same
properties as the dark one, every public property documented in the README).
