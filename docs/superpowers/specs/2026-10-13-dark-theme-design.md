# Dark theme and responsive layout — design

The desktop application still wears stock light Bootstrap: five outline buttons along the top for
navigation, default cards and tables, Helvetica. This document gives it the dark look of
xrpl-vanity-generator — the sibling application this team already ships — and a layout that holds
from a wide desktop window down to a phone's width, so that a later mobile build reuses it instead
of redoing it.

Mockups: the "xrpl-file dark theme" design canvas (nine artboards: four screens at 1280 px, a
locked vault and a narrow window, three phone screens at 390 px). The canvas is the visual
reference; this document is the contract.

## 1. Decisions

| # | Decision | Why |
|---|---|---|
| T1 | Bootstrap 5.3.3 stays; the theme is a layer on top of it | 21 components use Bootstrap classes 176 times. Bootstrap's own dark mode plus remapped `--bs-*` variables restyles every one of them without touching its markup, and xrpl-vanity is built the same way. Removing Bootstrap would mean rewriting every screen's markup for the same pixels |
| T2 | The palette, type and radii are xrpl-vanity's, value for value | The two applications are one family; a near-copy reads as a mistake |
| T3 | Fonts ship inside the application as woff2 files | The application must work offline; a Google Fonts link would fall back silently to system fonts on a machine without network |
| T4 | Navigation becomes a side rail on wide windows and a bottom tab bar under 720 px | The rail is xrpl-vanity's; the tab bar is where a phone's thumb is |
| T5 | History becomes its own navigation entry, out of Publish | It is a list of published links a person comes back to, not a step of publishing |
| T6 | Layout adapts by CSS breakpoints only; no screen decides its own layout in code | One markup per screen keeps C1's rule (components hold no decisions) and keeps every width testable by resizing a window |
| T7 | No user-visible text changes in this work | The texts were settled screen by screen in parts C1–C4 and in the leftover-tickets work; a restyle that also rewrites copy cannot be reviewed as either |
| T8 | A mobile build is out of scope | The application targets `net10.0-windows` today. This work makes the layout phone-ready; Android and iOS targets come as their own project |

## 2. Tokens

Defined once on `:root` in the application's stylesheet, copied from xrpl-vanity's
`wwwroot/scss/common/_var-colors.scss`:

| Token | Value | Use |
|---|---|---|
| `--bg` | `#0D0D0D` | window background |
| `--box-bg` | `#1A1A1A` | cards |
| `--chips` | `#2D2D2D` | inputs, chips, tiles |
| `--dark-gray` | `#282828` | borders, dividers |
| `--secondary-dark` | `#202222` | secondary buttons |
| `--text` | `#F9F9F9` | primary text |
| `--text-secondary` | `#A8A8A8` | labels, secondary text |
| `--mint` | `#64CF95` | primary action, active navigation, success |
| `--beige` | `#EBAF8E` | figures (counts, amounts) |
| `--negative` | `#D6776A` | errors, destructive actions |
| `--yellow` | `#D3E7AB` | warnings |
| `--radius-card` | `12px` | cards |
| `--radius-chip` | `10px` | inputs, buttons, chips |
| `--font-text` | `"Inter", "Segoe UI", system-ui, sans-serif` | body |
| `--font-mono` | `"Cascadia Code", "Consolas", monospace` | addresses, hashes, links, paths |

`color-scheme: dark` on the root. Body text 14 px; page titles 20 px semibold; card titles 12 px
upper-case with 0.08 em tracking in `--text-secondary`; figures use tabular numerals in `--beige`.

Bootstrap's variables are remapped to the tokens with `data-bs-theme="dark"` on `<html>`:
`--bs-body-bg`, `--bs-body-color`, `--bs-secondary-color`, `--bs-border-color`,
`--bs-tertiary-bg`, `--bs-primary` / `--bs-primary-rgb` (mint), `--bs-danger` (negative),
`--bs-warning` (yellow), `--bs-success` (mint), `--bs-link-color` (text-secondary, mint on hover),
`--bs-border-radius` (10 px), `--bs-font-sans-serif` and `--bs-font-monospace`. Buttons, forms,
tables, alerts and badges pick the theme up from those; anything Bootstrap does not cover is a class
in the theme layer (section 4).

## 3. Layout

| Width | Navigation | Content |
|---|---|---|
| ≥ 1100 px | 72 px rail on the left: mark, six entries (Publish, Read, History, Keys, Accounts, Settings), Lock at the bottom | Two columns where a screen has two groups (estimate: file + key | figures; run: stages | spending; read: manifest | blocks) |
| 720–1099 px | The same rail | One column; long breakdowns fold into a `<details>` ("How the 51.12 XRP is made up") |
| < 720 px | Bottom tab bar: Publish, Read, History, Keys, More; More opens Accounts and Settings; Lock moves to the page header | One column, 16 px gutters; the screen's primary action is pinned above the tab bar |

The rail and the tab bar are both rendered; CSS shows one. Every target is at least 44 × 44 px.
Wide tables scroll horizontally inside their card, never the page. Dialogs are 420 px centred on
wide windows and full width under 720 px.

## 4. The theme layer

One stylesheet, `wwwroot/app.css`, replaces the template's: tokens, Bootstrap remapping, then these
classes, named as in xrpl-vanity where xrpl-vanity has them:

- **Shell:** `.shell` (rail + content grid), `.rail`, `.nav` / `.nav.active`, `.tabbar`, `.tab`,
  `.page-head`, `.content`.
- **Surfaces:** `.card-box` and `.card-title`; `.kv` (label/value list with right-aligned
  values); `.tile` (a figure in a chip, used in the folded estimate).
- **Actions:** `.btn-primary-mint`, `.btn-secondary-dark`, `.btn-stop` (secondary that turns
  negative on hover), `.btn-icon` (icon-only, always with `aria-label`).
- **Status:** `.pill`, `.pill-active` (mint), `.pill-warn` (yellow); `.banner-yellow`,
  `.banner-negative`, `.banner-mint`.
- **Text:** `.num` (tabular figures in beige), `.mono`, `.muted`.
- **Motion:** `.breathe` on a running state's pill; every transition and animation off under
  `prefers-reduced-motion: reduce`.

Scoped `.razor.css` files are used only where a component needs layout of its own (the shell grid,
the block grid on Read); everything else uses the shared classes.

## 5. Components

- **`Shell.razor`** is rewritten: the rail and the tab bar replace the button group; Lock moves into
  the rail (and the phone header). The failure and loading states use the theme's card.
- **`ShellModel`** gains `AppTab.History`. Publish stops rendering `<History />`; the History tab
  does. `Unfinished` stays in Publish — it is part of finishing what was started there.
- **The More menu** (phone width) is the model's state, not the component's: `ShellModel` gains
  whether it is open and closes it when a tab is chosen, so the rule "choosing a tab closes More"
  is tested on Linux.
- **Every other screen** keeps its markup and gains classes: cards become `.card-box` with a
  `.card-title`, label/value blocks become `.kv`, primary actions `.btn-primary-mint`, secondary
  `.btn-secondary-dark`, destructive `.btn-danger`, figures `.num`, addresses and links `.mono`.
- **Unlock, CreateVault, VaultUnreachable, Damage** sit centred in a 420 px card, as the locked-vault
  mockup shows.

## 6. Accessibility

- Text contrast at least 4.5:1, 3:1 for text at 24 px and over and for non-text indicators. The
  token pairs in use (`--text` / `--text-secondary` / `--mint` / `--beige` / `--negative` /
  `--yellow` on `--bg`, `--box-bg` and `--chips`; `#0D0D0D` on `--mint` and `--negative`) are
  measured and the ratios recorded in the plan; a pair that fails is not used.
- Focus is visible on every control: a 2 px `--mint` outline with offset, never removed.
- Navigation is a `<nav>` with `aria-label`; the current entry carries `aria-current="page"`.
- Status never relies on colour alone: every pill and banner has words; verified blocks carry a
  check mark as well as mint.

## 7. Testing

- `ShellModel`: the History tab, the More menu opening and closing on tab choice — App.Core tests
  on Linux.
- Every App.Core and Core suite stays green; the application builds without warnings.
- The look is checked in the MauiLab VM at three window widths (1280, 900, 600 px) on the publish,
  run, read, keys and unlock screens, with screenshots kept with the plan's report.
- Contrast is checked with the `ui-a11y-gates` skill against the compiled stylesheet.

## 8. What this does not do

It does not add Android or iOS targets, a QR scanner, a light theme or a theme switch. It does not
change any text, flow or decision a screen makes. It does not touch the browser reader in `web/`.
