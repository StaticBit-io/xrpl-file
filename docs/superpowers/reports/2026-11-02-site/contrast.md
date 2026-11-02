# Site contrast

WCAG 2.2 AA, computed from `site/src/css/tokens.css` and the `<xrpl-file>` palettes in the README
("Embed a file") by `node site/tools/contrast.mjs`; `site/test/contrast.test.mjs` fails when a required pair
misses its threshold or when this file is out of date. Ratios are the WCAG 2.x relative-luminance
contrast, truncated (never rounded up) to two decimals.

- Text: at least 4.5:1. Non-text UI and graphics that carry information: at least 3:1.
- Rows marked "exempt" are shown for the record and have no threshold.
- Every token the stylesheets use for a colour is in `tokens.css`; `--paper` is OKLCH L 0.945 in the light
  theme and 0.175 in the dark one, never `#fff` or `#000`.

Result: every required pair passes.

## Light theme

| Pair | Foreground | Background | Ratio | Needs | Result |
|---|---|---|---|---|---|
| Body text | `--ink` #1e2228 | `--paper` #eaedf1 | 13.62:1 | 4.5:1 | pass |
| Body text on raised surface and hover | `--ink` #1e2228 | `--paper-2` #e0e3e8 | 12.43:1 | 4.5:1 | pass |
| Muted text (captions, footer labels, inactive language) | `--ink-muted` #575b62 | `--paper` #eaedf1 | 5.81:1 | 4.5:1 | pass |
| Muted text on raised surface | `--ink-muted` #575b62 | `--paper-2` #e0e3e8 | 5.30:1 | 4.5:1 | pass |
| Links | `--accent` #0c62c4 | `--paper` #eaedf1 | 5.03:1 | 4.5:1 | pass |
| Links on raised surface | `--accent` #0c62c4 | `--paper-2` #e0e3e8 | 4.59:1 | 4.5:1 | pass |
| Text on an accent fill (selection, primary button) | `--accent-ink` #f8fafd | `--accent` #0c62c4 | 5.66:1 | 4.5:1 | pass |
| Text on an ink fill (skip link, pressed theme switch) | `--paper` #eaedf1 | `--ink` #1e2228 | 13.62:1 | 4.5:1 | pass |
| Error message | `--error` #a92321 | `--paper` #eaedf1 | 6.07:1 | 4.5:1 | pass |
| Focus ring | `--focus` #0c62c4 | `--paper` #eaedf1 | 5.03:1 | 3:1 | pass |
| Focus ring on raised surface | `--focus` #0c62c4 | `--paper-2` #e0e3e8 | 4.59:1 | 3:1 | pass |
| Control border (theme switch) | `--rule` #7d8086 | `--paper` #eaedf1 | 3.36:1 | 3:1 | pass |
| Current language underline | `--accent` #0c62c4 | `--paper` #eaedf1 | 5.03:1 | 3:1 | pass |
| Unread cell outline | `--cell` #7d8086 | `--paper` #eaedf1 | 3.36:1 | 3:1 | pass |
| Lit cell | `--cell-lit` #3a3d44 | `--paper` #eaedf1 | 9.25:1 | 3:1 | pass |
| Verified cell | `--cell-verified` #0c62c4 | `--paper` #eaedf1 | 5.03:1 | 3:1 | pass |
| Decorative hairline (exempt: carries no information) | `--rule-soft` #ced1d5 | `--paper` #eaedf1 | 1.30:1 | exempt | n/a |
| Lit cell against verified cell (state also shown in text) | `--cell-lit` #3a3d44 | `--cell-verified` #0c62c4 | 1.83:1 | exempt | n/a |

## Dark theme

| Pair | Foreground | Background | Ratio | Needs | Result |
|---|---|---|---|---|---|
| Body text | `--ink` #e5e8ed | `--paper` #0e1115 | 15.43:1 | 4.5:1 | pass |
| Body text on raised surface and hover | `--ink` #e5e8ed | `--paper-2` #171a1f | 14.25:1 | 4.5:1 | pass |
| Muted text (captions, footer labels, inactive language) | `--ink-muted` #a4a8ae | `--paper` #0e1115 | 7.93:1 | 4.5:1 | pass |
| Muted text on raised surface | `--ink-muted` #a4a8ae | `--paper-2` #171a1f | 7.32:1 | 4.5:1 | pass |
| Links | `--accent` #4da3ff | `--paper` #0e1115 | 7.22:1 | 4.5:1 | pass |
| Links on raised surface | `--accent` #4da3ff | `--paper-2` #171a1f | 6.67:1 | 4.5:1 | pass |
| Text on an accent fill (selection, primary button) | `--accent-ink` #06121f | `--accent` #4da3ff | 7.18:1 | 4.5:1 | pass |
| Text on an ink fill (skip link, pressed theme switch) | `--paper` #0e1115 | `--ink` #e5e8ed | 15.43:1 | 4.5:1 | pass |
| Error message | `--error` #f58b84 | `--paper` #0e1115 | 8.07:1 | 4.5:1 | pass |
| Focus ring | `--focus` #4da3ff | `--paper` #0e1115 | 7.22:1 | 3:1 | pass |
| Focus ring on raised surface | `--focus` #4da3ff | `--paper-2` #171a1f | 6.67:1 | 3:1 | pass |
| Control border (theme switch) | `--rule` #66696f | `--paper` #0e1115 | 3.44:1 | 3:1 | pass |
| Current language underline | `--accent` #4da3ff | `--paper` #0e1115 | 7.22:1 | 3:1 | pass |
| Unread cell outline | `--cell` #66696f | `--paper` #0e1115 | 3.44:1 | 3:1 | pass |
| Lit cell | `--cell-lit` #d5d8dd | `--paper` #0e1115 | 13.21:1 | 3:1 | pass |
| Verified cell | `--cell-verified` #4da3ff | `--paper` #0e1115 | 7.22:1 | 3:1 | pass |
| Decorative hairline (exempt: carries no information) | `--rule-soft` #2b2e33 | `--paper` #0e1115 | 1.39:1 | exempt | n/a |
| Lit cell against verified cell (state also shown in text) | `--cell-lit` #d5d8dd | `--cell-verified` #4da3ff | 1.82:1 | exempt | n/a |

## Element, theme="light"

| Pair | Foreground | Background | Ratio | Needs | Result |
|---|---|---|---|---|---|
| Names, percentages, badge | `--xrpl-file-foreground` #1b1d22 | `--xrpl-file-background` #f3f4f6 | 15.32:1 | 4.5:1 | pass |
| Sizes, progress details, notes | `--xrpl-file-muted` #5b5f6b | `--xrpl-file-background` #f3f4f6 | 5.79:1 | 4.5:1 | pass |
| Error message | `--xrpl-file-danger` #b3261e | `--xrpl-file-background` #f3f4f6 | 5.93:1 | 4.5:1 | pass |
| Text on the accent (buttons) | `--xrpl-file-on-accent` #ffffff | `--xrpl-file-accent` #0b62c4 | 5.90:1 | 4.5:1 | pass |
| Text file | `--xrpl-file-text-foreground` #1f2328 | `--xrpl-file-text-background` #ffffff | 15.79:1 | 4.5:1 | pass |
| Buttons, progress, spinner, focus ring against the box | `--xrpl-file-accent` #0b62c4 | `--xrpl-file-background` #f3f4f6 | 5.36:1 | 3:1 | pass |
| Accent against the site paper (the element is embedded with the page theme) | `--xrpl-file-accent` #0b62c4 | `site --paper (light)` #eaedf1 | 5.02:1 | 3:1 | pass |
| Element box against the site paper (a picture frame: not a control, exempt) | `--xrpl-file-background` #f3f4f6 | `site --paper (light)` #eaedf1 | 1.06:1 | exempt | n/a |

## Element, theme="dark"

| Pair | Foreground | Background | Ratio | Needs | Result |
|---|---|---|---|---|---|
| Names, percentages, badge | `--xrpl-file-foreground` #e8e8ea | `--xrpl-file-background` #14161a | 14.80:1 | 4.5:1 | pass |
| Sizes, progress details, notes | `--xrpl-file-muted` #9a9ca3 | `--xrpl-file-background` #14161a | 6.60:1 | 4.5:1 | pass |
| Error message | `--xrpl-file-danger` #ff8a8a | `--xrpl-file-background` #14161a | 7.98:1 | 4.5:1 | pass |
| Text on the accent (buttons) | `--xrpl-file-on-accent` #06121f | `--xrpl-file-accent` #4da3ff | 7.17:1 | 4.5:1 | pass |
| Text file | `--xrpl-file-text-foreground` #e8e8ea | `--xrpl-file-text-background` #1b1e23 | 13.65:1 | 4.5:1 | pass |
| Buttons, progress, spinner, focus ring against the box | `--xrpl-file-accent` #4da3ff | `--xrpl-file-background` #14161a | 6.89:1 | 3:1 | pass |
| Accent against the site paper (the element is embedded with the page theme) | `--xrpl-file-accent` #4da3ff | `site --paper (dark)` #0e1115 | 7.22:1 | 3:1 | pass |
| Element box against the site paper (a picture frame: not a control, exempt) | `--xrpl-file-background` #14161a | `site --paper (dark)` #0e1115 | 1.04:1 | exempt | n/a |

## Notes

- The element is always embedded with the page theme (`chrome.mjs` sets its `theme` attribute with the
  switch), so a light element on a dark paper, or the reverse, does not occur.
- Over a cover and on its caption the element draws on a dark overlay in both themes; those colours
  are the element's own and are verified with the element (README, "Embed a file").
- The unread cell is drawn as an outline at 3:1, so an empty mosaic reads as graph paper; the lit and
  verified cells are fills. The mosaic also states its progress in text, so no state depends on colour alone.
