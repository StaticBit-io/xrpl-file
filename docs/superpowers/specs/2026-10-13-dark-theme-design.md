# Dark theme and responsive layout — design

The desktop application still wears stock light Bootstrap: five outline buttons along the top for
navigation, default cards and tables, Helvetica. This document gives it the dark look of
xrpl-vanity-generator — the sibling application this team already ships — and a layout that holds
from a wide desktop window down to a phone's width, so that a later mobile build reuses it instead
of redoing it.

Visual reference: the "xrpl-file dark theme" design canvas,
https://claude.ai/artifact/6R75SmVJGMivc1anMkWwNo, version 10 (2026-10-13). Its artboards map to
section 5's matrix: Main = Publish/estimate at 1280 px, Run = Publish/running at 1280 px, Read and
Keys at 1280 px, Unlock = locked vault, Compact = Publish/estimate at 820 px, and three phone
artboards at 390 px. The canvas shows the look; this document is the contract, and where they
differ this document wins. The artboards draw richer screens than the application has — a list of
stages and a "spent so far" card while publishing, a grid of verified blocks while reading, amounts
in XRP, a two-column estimate, a folded breakdown. None of that is built here: it is new content
and new text (T7), and the screens keep the content they have today, in one column (section 5).

## 1. Decisions

| # | Decision | Why |
|---|---|---|
| T1 | Bootstrap 5.3.3 stays as the precompiled file; the theme is a stylesheet on top of it that remaps Bootstrap's root variables AND overrides each component variant the application uses | 21 components use Bootstrap classes 176 times. Bootstrap's compiled CSS gives each variant its own local variables (`.btn-primary` declares `--bs-btn-bg` and friends from Sass), so root variables alone leave stock colours behind. Overriding the variants in place keeps every screen's class names and restyles them all |
| T2 | The palette, type and radii are xrpl-vanity's, value for value, plus one new token for control boundaries (section 2) | The two applications are one family; a near-copy reads as a mistake. The new token exists because the reference's border colour is too faint to identify an input (WCAG 1.4.11) |
| T3 | Inter (400, 500, 600) and Cascadia Code (400) ship inside the application as woff2 files, copied with their OFL licence texts from xrpl-vanity's `wwwroot/fonts` | The application must work offline; a web font link would fall back silently to system fonts |
| T4 | Navigation becomes a side rail on wide windows and a bottom tab bar under 720 px | The rail is xrpl-vanity's; the tab bar is where a phone's thumb is |
| T5 | History becomes its own navigation entry, out of Publish | It is a list of published links a person comes back to, not a step of publishing |
| T6 | Layout adapts by CSS breakpoints only; no component decides its layout in code. Presentational markup changes — wrappers, grouping, moving an element within its own screen, swapping a class — are allowed; a decision, a text or a binding moving out of a model is not | C1's rule is that components hold no decisions, not that their markup is frozen; two columns and a pinned action cannot be made from class names alone |
| T7 | No existing user-visible text changes. The only new texts are the navigation labels "History" and "More" and the accessible names listed in section 4 | The texts were settled screen by screen in parts C1–C4 and the leftover-tickets work; a restyle that also rewrites copy cannot be reviewed as either |
| T8 | A mobile build is out of scope | The application targets `net10.0-windows` today. This work makes the layout phone-ready; Android and iOS targets come as their own project |

## 2. Tokens

Defined once on `:root` in `wwwroot/app.css`, values from xrpl-vanity's
`wwwroot/scss/common/_var-colors.scss`:

| Token | Value | Use |
|---|---|---|
| `--bg` | `#0D0D0D` | window background |
| `--box-bg` | `#1A1A1A` | cards |
| `--chips` | `#2D2D2D` | input fill, chips, tiles |
| `--dark-gray` | `#282828` | decorative borders and dividers only |
| `--control-line` | `#6B6B6B` | **new**: the boundary of every input, select, checkbox, switch and outline button |
| `--secondary-dark` | `#202222` | secondary buttons |
| `--text` | `#F9F9F9` | primary text |
| `--text-secondary` | `#A8A8A8` | labels, secondary text |
| `--mint` | `#64CF95` | primary action, active navigation, success, focus ring |
| `--beige` | `#EBAF8E` | figures (counts, amounts) |
| `--negative` | `#D6776A` | errors, destructive actions |
| `--yellow` | `#D3E7AB` | warnings |
| `--radius-card` | `12px` | cards |
| `--radius-chip` | `10px` | inputs, buttons, chips |
| `--font-text` | `"Inter", "Segoe UI", system-ui, sans-serif` | body |
| `--font-mono` | `"Cascadia Code", "Consolas", monospace` | addresses, hashes, links, paths |

`color-scheme: dark` and `data-bs-theme="dark"` on `<html>` in `wwwroot/index.html`. Body text
14 px; page titles 20 px weight 600; section titles 12 px upper-case, 0.08 em tracking, in
`--text-secondary`. `@font-face` declarations name the four shipped files; no weight is synthesised.

**Contrast, measured** (WCAG relative luminance; ≥ 4.5:1 for text, ≥ 3:1 for non-text):

| Foreground | on `--bg` | on `--box-bg` | on `--chips` |
|---|---|---|---|
| `--text` | 18.46 | 16.53 | 13.08 |
| `--text-secondary` | 8.17 | 7.32 | 5.79 |
| `--mint` | 10.09 | 9.03 | 7.15 |
| `--beige` | 10.23 | 9.16 | 7.25 |
| `--negative` | 6.21 | 5.56 | **4.40 — not used for text** |
| `--yellow` | 14.61 | 13.08 | 10.35 |
| `--control-line` (non-text) | 3.65 | 3.27 | — |

`#0D0D0D` text on `--mint` is 10.09 and on `--negative` 6.21. Rule: error text never sits on
`--chips`; an error inside an input, chip or tile is written in `--text` and carries the error
icon, and validation messages sit below the field on the card.

**Bootstrap root remapping** (each with its `-rgb` twin where Bootstrap has one):
`--bs-body-bg` (`--bg`), `--bs-body-color` (`--text`), `--bs-secondary-color` and
`--bs-tertiary-color` (`--text-secondary`), `--bs-secondary-bg` (`--chips`), `--bs-tertiary-bg`
(`--box-bg`), `--bs-border-color` (`--dark-gray`), `--bs-primary`, `--bs-success` (`--mint`),
`--bs-danger` (`--negative`), `--bs-warning` (`--yellow`), `--bs-link-color` and
`--bs-link-color-rgb` (`--text-secondary`), `--bs-link-hover-color` and its `-rgb` (`--mint`),
`--bs-border-radius` (`--radius-chip`), `--bs-font-sans-serif` (`--font-text`),
`--bs-font-monospace` (`--font-mono`), `--bs-focus-ring-color` (`--mint` at 100%), and the
`*-text-emphasis`, `*-bg-subtle` and `*-border-subtle` variables of primary, success, danger,
warning, info and secondary so that subtle backgrounds follow the palette.

**Component overrides** — every variant the application uses, with default, hover, active, focus
and disabled states (disabled = 45% opacity, as xrpl-vanity):

| Variant (uses) | Look |
|---|---|
| `.btn-primary` (17), `.btn-success` (1) | solid `--mint`, `#0D0D0D` text, weight 600 |
| `.btn-outline-primary` (15) | transparent, `--mint` text and border; hover fills `rgba(100,207,149,.10)` |
| `.btn-outline-secondary` (23) | `--secondary-dark` fill, `--text`, `--control-line` border; hover border `--text-secondary` |
| `.btn-danger` (2) | solid `--negative`, `#0D0D0D` text |
| `.btn-outline-danger` (6) | transparent, `--negative` text and border |
| `.btn-outline-warning` (1) | transparent, `--yellow` text and border |
| `.btn-link` (9) | `--text-secondary`, `--mint` on hover, underline on focus |
| `.btn` sizes | 44 px minimum height; `.btn-sm` 36 px with a 44 px hit area |
| `.alert-warning` (14), `-danger` (12), `-success` (8), `-info` (5), `-secondary` (5) | xrpl-vanity banners: yellow, negative, mint tint at 12–14% with the matching text colour; info and secondary on `--chips` with `--text` |
| `.form-control`, `.form-select` (20) | `--chips` fill, `--control-line` border, `--text`; focus border `--mint` |
| `.form-check-input`, `.form-switch` | `--control-line` border, checked `--mint` |
| `.table` (7) | transparent cells, `--dark-gray` row rules, header in section-title style |
| `.list-group`, `.list-group-item`, `-action` | `--box-bg` items, `--dark-gray` rules, hover `--chips` |
| `.nav-tabs`, `.nav-link` | `--text-secondary`; active `--mint` with a 2 px `--mint` underline |
| `.progress`, `.progress-bar` | `--chips` track, `--mint` bar, 20 px (the publishing bar carries its percentage as text, `#0D0D0D` on mint), fully rounded; the native `<progress>` on Read gets `accent-color: var(--mint)` |
| `.badge`, `.text-bg-danger` | pill shape; danger as `--negative` fill with `#0D0D0D` text |
| `.text-danger` (2) | `--negative`, only on `--bg` or `--box-bg` |

## 3. Layout

| Width | Navigation | Content |
|---|---|---|
| ≥ 1100 px | 72 px rail on the left: mark, six entries (Publish, Read, History, Keys, Accounts, Settings), Lock at the bottom | one column, at most 920 px wide, left-aligned, 26 px padding |
| 720–1099 px | the same rail | one column filling the content region, 22 px padding |
| < 720 px | bottom tab bar: Publish, Read, History, Keys, More; More opens Accounts and Settings; a 56 px header bar holds the mark and Lock | one column, 16 px gutters; the screen's final action row, where section 5 names one, sits in an action bar above the tab bar |

**Scrolling.** The window never scrolls; the content region does. The shell is a full-height grid
(rail | content) or column (header bar | content | tab bar); the tab bar is a row of that column,
not `position: fixed`. The action bar is the last element of the page inside the content region,
`position: sticky; bottom: 0` with an opaque background, so it stays in view while the page scrolls
and nothing can end up beneath it — it is the page's last element. Long
content wraps: addresses, hashes, links and paths use `overflow-wrap: anywhere`; every grid and flex
child gets `min-width: 0`. Only tables scroll horizontally, inside their own card. Under 720 px the
tab bar's bottom padding includes `env(safe-area-inset-bottom)`.

**Hiding the other navigation.** Both the rail and the tab bar are rendered; the inactive one is
`display: none`, so it is out of layout, tab order and the accessibility tree. Element ids are
unique across both. A focused control hidden by a resize loses focus to the content region, which
is focusable (`tabindex="-1"`).

**Dialogs and cards.** Dialogs are `min(420px, 100% - 32px)` wide, at most `100% - 32px` tall,
and scroll inside. The vault screens' card is `min(420px, 100% - 32px)` wide and centred.

**Targets.** Project standard (stricter than WCAG 2.2's 24 × 24): every navigation entry, button
and form control has a hit area of at least 44 × 44 CSS px. Inline links in running text are
exempt.

## 4. The theme layer and the shell

`wwwroot/app.css` replaces the template's stylesheet: `@font-face`, tokens, Bootstrap remapping,
component overrides, then the application's own classes. To keep clear of Bootstrap's names
(`.nav`, `.card-title`), the application's own classes carry an `xf-` prefix:

- **Shell:** `.xf-shell`, `.xf-rail`, `.xf-rail-item`, `.xf-tabbar`, `.xf-tab`, `.xf-topbar`
  (the header bar under 720 px), `.xf-content`, `.xf-actionbar`, `.xf-more` (the More panel).
- **Pages:** `.xf-page` (a screen's root), `.xf-title` (its heading).
- **Surfaces:** `.xf-card` and `.xf-section-title`; `.xf-kv-table` (a table of label/value rows,
  values right-aligned).
- **Text:** `.xf-num` (tabular numerals; colour inherited), `.xf-figure` (`.xf-num` in `--beige`
  for a standalone count or amount), `.xf-mono`, `.xf-muted`.
- **Vault screens:** `.xf-vault` (full-height centring around their card).
- **Motion:** colour and border transitions of 180 ms; every transition and animation is off under
  `prefers-reduced-motion: reduce`.

All of it lives in `app.css`; no `.razor.css` file is added, so there is one place a rule can come
from.

**`Shell.razor`** is rewritten: the rail and the tab bar replace the button group. Lock is a
button in the rail and, under 720 px, in the header bar. Its accessible name is "Lock the vault".
The failure and loading states use `.xf-card`.

**`ShellModel`** gains:

- `AppTab.History`. Publish stops rendering `<History />`; the History tab does. `Unfinished`
  stays in Publish — it belongs to finishing what was started there.
- `MoreOpen` with `ToggleMore()` and `CloseMore()`; `Show(tab)` closes it. When the current tab is
  Accounts or Settings, the More tab shows as current (`IsMoreCurrent`), so a phone user always
  sees where they are.

**The More control** is a `<button>` with `aria-expanded` and `aria-controls` pointing at the panel.
The panel holds two ordinary navigation buttons (Accounts, Settings). Escape and a click outside
close it, returning focus to the More button. A resize to 720 px or wider closes it.
Navigation carries `aria-label="Main"`; the current entry carries `aria-current="page"`.

## 5. Screens

Every screen keeps its content and stays one column. The action row pinned under 720 px is the
screen's existing last row of buttons, moved into `.xf-actionbar`:

| Screen / state | Pinned under 720 px |
|---|---|
| Publish — picker | the Estimate button |
| Publish — estimate | Back and Publish |
| Publish — running (Run) | Cancel (`.btn-outline-secondary`) |
| Publish — finished (Run) | Dismiss |
| Read — link entry | nothing: its buttons sit between the fields they act on |
| Read — reading | Cancel |
| Read — finished | Dismiss |
| Unfinished runs, History, Keys and its sub-screens, Accounts and its sub-screens, Settings, Change password | nothing |
| Create vault, Unlock, Vault unreachable, Damage | nothing: centred card, its own buttons inside |

The mechanical changes on every screen, so that two implementers make the same ones:

| Today | Becomes |
|---|---|
| a screen's root `<div class="container py-3">` (or `py-4`) | `<div class="xf-page">` |
| its `<h1 class="h3">` | `<h1 class="xf-title">` |
| each self-contained block — a `table`, a `list-group`, a group of label/value lines, a form's set of fields | wrapped in `<section class="xf-card">` |
| a `table` of label/value rows (`<th>` + `<td>` per row) | keeps its markup, gains `.xf-kv-table` (labels in `--text-secondary`, values right-aligned with `.xf-num`) |
| an address, hash, link, path or node URL in `<code>` or plain text | `<code class="xf-mono">` or `<span class="xf-mono">` |
| the last row of action buttons on a screen the table above names | moved into `<div class="xf-actionbar">` at the end of the screen |

`.btn-danger` stays for irreversible actions (delete a key, close an account); a run's Cancel is
not one.

## 6. Accessibility

- Text contrast and non-text contrast as measured in section 2; a pair outside that table is
  measured before it is used.
- Focus is visible on every control: a 2 px `--mint` outline at 2 px offset, never removed, never
  clipped by an `overflow` container.
- Status never relies on colour alone: every pill and banner has words; verified blocks on Read
  carry a check mark as well as mint.
- Section 4 covers the navigation's semantics and the More control.

## 7. Testing

- **Linux, App.Core:** the theme's token values and every contrast figure of section 2, read from
  `wwwroot/app.css` itself; the History tab; More opening, toggling
  and closing on `Show`, on `CloseMore` and on a tab change; `IsMoreCurrent` for Accounts and
  Settings.
- Every App.Core and Core suite stays green; the application builds without warnings.
- **MauiLab VM**, screenshots in the plan's report folder: every row of section 5's matrix at
  1280 px; Publish/estimate, Run, Read, Keys, History and Unlock at 900 px and at 600 px; the More
  panel open at 600 px; keyboard-only pass over the shell (Tab order, focus ring, Escape on More);
  Windows text size 150% at 1280 px.
- **Contrast:** the `ui-a11y-gates` skill against the compiled stylesheet, plus a rendered check of
  hover, disabled and banner states in the VM.

## 8. What this does not do

It does not add Android or iOS targets, a QR scanner, a light theme or a theme switch. It does not
change any existing text, flow or decision a screen makes. It does not touch the browser reader in
`web/`.
