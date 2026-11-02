# Site contrast

WCAG 2.2 AA, computed from `site/src/css/tokens.css` and the `<xrpl-file>` palettes in the README
("Embed a file") by `node site/tools/contrast.mjs`; `site/test/contrast.test.mjs` fails when a required pair
misses its threshold or when this file is out of date. Ratios are the WCAG 2.x relative-luminance
contrast, truncated (never rounded up) to two decimals.

- Text: at least 4.5:1. Non-text UI and graphics that carry information: at least 3:1.
- Rows marked "exempt" are shown for the record and have no threshold.
- Every token the stylesheets use for a colour is in `tokens.css`; `--paper` is OKLCH L 0.945 in the light
  theme and 0.135 in the dark one, never `#fff` or `#000`.

Result: every required pair passes.

## Light theme

| Pair | Foreground | Background | Ratio | Needs | Result |
|---|---|---|---|---|---|
| Body text | `--ink` #192025 | `--paper` #e9edf0 | 13.99:1 | 4.5:1 | pass |
| Body text on raised surface and hover | `--ink` #192025 | `--paper-2` #dee4e7 | 12.78:1 | 4.5:1 | pass |
| Muted text (captions, footer labels, hints) | `--ink-muted` #535c63 | `--paper` #e9edf0 | 5.79:1 | 4.5:1 | pass |
| Muted text on raised surface | `--ink-muted` #535c63 | `--paper-2` #dee4e7 | 5.29:1 | 4.5:1 | pass |
| Secondary text (body, lead, inactive language) | `--ink-2` #32393f | `--paper` #e9edf0 | 9.99:1 | 4.5:1 | pass |
| Secondary text on raised surface | `--ink-2` #32393f | `--paper-2` #dee4e7 | 9.12:1 | 4.5:1 | pass |
| Secondary text on the hovered control | `--ink-2` #32393f | `--paper-3` #d1d9dd | 8.18:1 | 4.5:1 | pass |
| Text on the hovered control | `--ink` #192025 | `--paper-3` #d1d9dd | 11.46:1 | 4.5:1 | pass |
| Accent as text (reserved for found and verified labels) | `--accent` #336f00 | `--paper` #e9edf0 | 5.27:1 | 4.5:1 | pass |
| Accent as text on raised surface | `--accent` #336f00 | `--paper-2` #dee4e7 | 4.81:1 | 4.5:1 | pass |
| Text on an accent fill (primary button) | `--accent-ink` #f7faf3 | `--accent` #336f00 | 5.85:1 | 4.5:1 | pass |
| Text on the hovered accent fill | `--accent-ink` #f7faf3 | `--accent-hi` #265c00 | 7.59:1 | 4.5:1 | pass |
| Text on an ink fill (skip link) | `--paper` #e9edf0 | `--ink` #192025 | 13.99:1 | 4.5:1 | pass |
| Error message | `--error` #a92321 | `--paper` #e9edf0 | 6.08:1 | 4.5:1 | pass |
| Error message on raised surface | `--error` #a92321 | `--paper-2` #dee4e7 | 5.55:1 | 4.5:1 | pass |
| Focus ring | `--focus` #265c00 | `--paper` #e9edf0 | 6.84:1 | 3:1 | pass |
| Focus ring on raised surface | `--focus` #265c00 | `--paper-2` #dee4e7 | 6.24:1 | 3:1 | pass |
| Focus ring on the hovered or pressed control | `--focus` #265c00 | `--paper-3` #d1d9dd | 5.60:1 | 3:1 | pass |
| Control border (theme switch, buttons, link field) | `--rule` #76828b | `--paper` #e9edf0 | 3.34:1 | 3:1 | pass |
| Control border on raised surface (link field) | `--rule` #76828b | `--paper-2` #dee4e7 | 3.05:1 | 3:1 | pass |
| Verified check mark (hero card) | `--accent` #336f00 | `--paper-2` #dee4e7 | 4.81:1 | 3:1 | pass |
| Unread cell outline | `--cell` #6d7982 | `--paper-2` #dee4e7 | 3.46:1 | 3:1 | pass |
| Lit cell | `--cell-lit` #394a56 | `--paper-2` #dee4e7 | 7.12:1 | 3:1 | pass |
| Verified cell | `--cell-verified` #336f00 | `--paper-2` #dee4e7 | 4.81:1 | 3:1 | pass |
| Decorative hairline (exempt: carries no information) | `--rule-soft` #c5ccd0 | `--paper` #e9edf0 | 1.38:1 | exempt | n/a |
| Corner ticks and section rules (exempt: decorative) | `--rule-2` #9ea6ab | `--paper` #e9edf0 | 2.10:1 | exempt | n/a |
| Lit cell against verified cell (state also shown in text) | `--cell-lit` #394a56 | `--cell-verified` #336f00 | 1.48:1 | exempt | n/a |

## Dark theme

| Pair | Foreground | Background | Ratio | Needs | Result |
|---|---|---|---|---|---|
| Body text | `--ink` #ebeff2 | `--paper` #04090c | 17.30:1 | 4.5:1 | pass |
| Body text on raised surface and hover | `--ink` #ebeff2 | `--paper-2` #091015 | 16.52:1 | 4.5:1 | pass |
| Muted text (captions, footer labels, hints) | `--ink-muted` #89949a | `--paper` #04090c | 6.45:1 | 4.5:1 | pass |
| Muted text on raised surface | `--ink-muted` #89949a | `--paper-2` #091015 | 6.16:1 | 4.5:1 | pass |
| Secondary text (body, lead, inactive language) | `--ink-2` #bdc6ca | `--paper` #04090c | 11.48:1 | 4.5:1 | pass |
| Secondary text on raised surface | `--ink-2` #bdc6ca | `--paper-2` #091015 | 10.96:1 | 4.5:1 | pass |
| Secondary text on the hovered control | `--ink-2` #bdc6ca | `--paper-3` #111a1f | 10.15:1 | 4.5:1 | pass |
| Text on the hovered control | `--ink` #ebeff2 | `--paper-3` #111a1f | 15.30:1 | 4.5:1 | pass |
| Accent as text (reserved for found and verified labels) | `--accent` #bcf053 | `--paper` #04090c | 14.99:1 | 4.5:1 | pass |
| Accent as text on raised surface | `--accent` #bcf053 | `--paper-2` #091015 | 14.31:1 | 4.5:1 | pass |
| Text on an accent fill (primary button) | `--accent-ink` #0f1703 | `--accent` #bcf053 | 13.76:1 | 4.5:1 | pass |
| Text on the hovered accent fill | `--accent-ink` #0f1703 | `--accent-hi` #ccfc76 | 15.50:1 | 4.5:1 | pass |
| Text on an ink fill (skip link) | `--paper` #04090c | `--ink` #ebeff2 | 17.30:1 | 4.5:1 | pass |
| Error message | `--error` #f47c70 | `--paper` #04090c | 7.56:1 | 4.5:1 | pass |
| Error message on raised surface | `--error` #f47c70 | `--paper-2` #091015 | 7.22:1 | 4.5:1 | pass |
| Focus ring | `--focus` #bcf053 | `--paper` #04090c | 14.99:1 | 3:1 | pass |
| Focus ring on raised surface | `--focus` #bcf053 | `--paper-2` #091015 | 14.31:1 | 3:1 | pass |
| Focus ring on the hovered or pressed control | `--focus` #bcf053 | `--paper-3` #111a1f | 13.25:1 | 3:1 | pass |
| Control border (theme switch, buttons, link field) | `--rule` #67747c | `--paper` #04090c | 4.14:1 | 3:1 | pass |
| Control border on raised surface (link field) | `--rule` #67747c | `--paper-2` #091015 | 3.95:1 | 3:1 | pass |
| Verified check mark (hero card) | `--accent` #bcf053 | `--paper-2` #091015 | 14.31:1 | 3:1 | pass |
| Unread cell outline | `--cell` #546673 | `--paper-2` #091015 | 3.20:1 | 3:1 | pass |
| Lit cell | `--cell-lit` #86a3b3 | `--paper-2` #091015 | 7.21:1 | 3:1 | pass |
| Verified cell | `--cell-verified` #bcf053 | `--paper-2` #091015 | 14.31:1 | 3:1 | pass |
| Decorative hairline (exempt: carries no information) | `--rule-soft` #20282c | `--paper` #04090c | 1.33:1 | exempt | n/a |
| Corner ticks and section rules (exempt: decorative) | `--rule-2` #30393f | `--paper` #04090c | 1.70:1 | exempt | n/a |
| Lit cell against verified cell (state also shown in text) | `--cell-lit` #86a3b3 | `--cell-verified` #bcf053 | 1.98:1 | exempt | n/a |

## Element, theme="light"

| Pair | Foreground | Background | Ratio | Needs | Result |
|---|---|---|---|---|---|
| Names, percentages, badge | `--xrpl-file-foreground` #1b1d22 | `--xrpl-file-background` #f3f4f6 | 15.32:1 | 4.5:1 | pass |
| Sizes, progress details, notes | `--xrpl-file-muted` #5b5f6b | `--xrpl-file-background` #f3f4f6 | 5.79:1 | 4.5:1 | pass |
| Error message | `--xrpl-file-danger` #b3261e | `--xrpl-file-background` #f3f4f6 | 5.93:1 | 4.5:1 | pass |
| Text on the accent (buttons) | `--xrpl-file-on-accent` #ffffff | `--xrpl-file-accent` #0b62c4 | 5.90:1 | 4.5:1 | pass |
| Text file | `--xrpl-file-text-foreground` #1f2328 | `--xrpl-file-text-background` #ffffff | 15.79:1 | 4.5:1 | pass |
| Buttons, progress, spinner, focus ring against the box | `--xrpl-file-accent` #0b62c4 | `--xrpl-file-background` #f3f4f6 | 5.36:1 | 3:1 | pass |
| Accent against the frame the element sits in (the element is embedded with the page theme) | `--xrpl-file-accent` #0b62c4 | `site --paper-2 (light)` #dee4e7 | 4.59:1 | 3:1 | pass |
| Element box against its frame (a picture frame: not a control, exempt) | `--xrpl-file-background` #f3f4f6 | `site --paper-2 (light)` #dee4e7 | 1.16:1 | exempt | n/a |
| Progress ring and bar, focus ring on the overlay over black | `--xrpl-file-accent (element default)` #4da3ff | `overlay over black` #000000 | 7.99:1 | 3:1 | pass |
| Overlay text on the overlay over black | `--xrpl-file-overlay-foreground` #dddddd | `overlay over black` #000000 | 15.46:1 | 4.5:1 | pass |
| Progress ring and bar, focus ring on the overlay over a white cover | `--xrpl-file-accent (element default)` #4da3ff | `overlay over a white cover` #474747 | 3.51:1 | 3:1 | pass |
| Overlay text on the overlay over a white cover | `--xrpl-file-overlay-foreground` #dddddd | `overlay over a white cover` #474747 | 6.79:1 | 4.5:1 | pass |
| Text on the accent button over a cover | `--xrpl-file-on-accent (element default)` #06121f | `--xrpl-file-accent (element default)` #4da3ff | 7.17:1 | 4.5:1 | pass |

## Element, theme="dark"

| Pair | Foreground | Background | Ratio | Needs | Result |
|---|---|---|---|---|---|
| Names, percentages, badge | `--xrpl-file-foreground` #e8e8ea | `--xrpl-file-background` #14161a | 14.80:1 | 4.5:1 | pass |
| Sizes, progress details, notes | `--xrpl-file-muted` #9a9ca3 | `--xrpl-file-background` #14161a | 6.60:1 | 4.5:1 | pass |
| Error message | `--xrpl-file-danger` #ff8a8a | `--xrpl-file-background` #14161a | 7.98:1 | 4.5:1 | pass |
| Text on the accent (buttons) | `--xrpl-file-on-accent` #0f1703 | `--xrpl-file-accent` #bcf053 | 13.76:1 | 4.5:1 | pass |
| Text file | `--xrpl-file-text-foreground` #e8e8ea | `--xrpl-file-text-background` #1b1e23 | 13.65:1 | 4.5:1 | pass |
| Buttons, progress, spinner, focus ring against the box | `--xrpl-file-accent` #bcf053 | `--xrpl-file-background` #14161a | 13.57:1 | 3:1 | pass |
| Accent against the frame the element sits in (the element is embedded with the page theme) | `--xrpl-file-accent` #bcf053 | `site --paper-2 (dark)` #091015 | 14.31:1 | 3:1 | pass |
| Element box against its frame (a picture frame: not a control, exempt) | `--xrpl-file-background` #14161a | `site --paper-2 (dark)` #091015 | 1.05:1 | exempt | n/a |
| Progress ring and bar, focus ring on the overlay over black | `--xrpl-file-accent (site)` #bcf053 | `overlay over black` #000000 | 15.74:1 | 3:1 | pass |
| Overlay text on the overlay over black | `--xrpl-file-overlay-foreground` #dddddd | `overlay over black` #000000 | 15.46:1 | 4.5:1 | pass |
| Progress ring and bar, focus ring on the overlay over a white cover | `--xrpl-file-accent (site)` #bcf053 | `overlay over a white cover` #474747 | 6.92:1 | 3:1 | pass |
| Overlay text on the overlay over a white cover | `--xrpl-file-overlay-foreground` #dddddd | `overlay over a white cover` #474747 | 6.79:1 | 4.5:1 | pass |
| Text on the accent button over a cover | `--xrpl-file-on-accent (site)` #0f1703 | `--xrpl-file-accent (site)` #bcf053 | 13.76:1 | 4.5:1 | pass |

## Notes

- The element is always embedded with the page theme (`chrome.mjs` sets its `theme` attribute with the
  switch), so a light element on a dark paper, or the reverse, does not occur.
- In the dark theme `base.css` replaces the element's `--xrpl-file-accent` and `--xrpl-file-on-accent`
  with the site's `--accent` and `--accent-ink`, so the element buttons carry the same signal colour as
  the page. The light theme does not: the element draws that accent on its own dark overlay as well, and
  a moss accent there falls below 3:1, so light keeps the element's own palette. The other element colours
  are the documented palette of the README.
- Over a cover and on its caption the element draws on a dark overlay (rgba(0,0,0,.72)) in both themes.
  The "overlay over black" and "overlay over a white cover" rows are its two extremes (the second is the
  overlay composited over pure white, the brightest cover possible).
- The unread cell is drawn as an outline at 3:1, so an empty mosaic reads as graph paper; the lit and
  verified cells are fills. The mosaic also states its progress in text, so no state depends on colour alone.
