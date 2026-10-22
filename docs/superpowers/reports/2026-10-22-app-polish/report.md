# App polish: width, small files, repeat runs, icon, title

Five defects found while testing the desktop application, fixed on branch
`claude/app-polish-2363b1` and checked in the MauiLab VM on 2026-10-22 against testnet
(`wss://s.altnet.rippletest.net:51233`). Screenshots are named `NN-screen-WIDTH.png`; every screen
was taken at 1900, 1280 and 600 px through CDP `Emulation.setDeviceMetricsOverride`.

## 1. Pages use the window width; tables fit their card

**What was wrong.** Every page was capped at 920 px (dark-theme spec, section 3). On a 1900 px window
the Keys (926 px of natural width), Accounts (1,145) and History (1,227) tables were wider than their
866 px card and scrolled sideways. Addresses kept on one line from 720 px (`94d34d0`) made it worse
at medium widths: Keys still scrolled at 900 px.

**What changed** (`app.css`, three heading rows, spec section 3 amended):

- The page grows to 1600 px. A card whose content is a data table, and the heading row above it
  (`xf-page-head`), take that width. Prose, forms, alerts and key/value tables keep the 920 px measure.
- Addresses in table cells stay on one line only from 1100 px; below, they wrap again.

**Measured** (`page-ok` = neither the page nor the content region scrolls sideways; then each table
card's `scrollWidth/clientWidth`): every one of the 80 screen × width checks is `page-ok`, and no
Blazor error bar appeared. Every table fits its card at 1900 and 1280 px, and a separate sweep found the
same at 1100, 1000 and 900 px. Keys needs 684 px of columns, about an 840 px window: at 800 px it
scrolls inside its card (684/667). In the main pass the only tables that scroll inside their card
are at 600 px: Keys (684/551) and Unfinished runs (589/551), as section 3 allows below 720 px.

Screens: `01`–`06` Keys, Add key (three tabs), Export, Reveal; `07`–`10` Accounts, New account,
Funding, Close account; `11`–`12` Settings, Change password; `13` History; `14` Publish with
Unfinished runs; `15`–`16` Estimate; `17`–`19` Run; `20`–`25` Read; `26`–`27` Unlock, Create vault.

## 2. A file too small for Batch publishes without a payer

**What was wrong.** A one-chunk file with a payer chosen gave a Batch estimate of 0 batches and 2 solo
transactions: the payer paid nothing, yet the screen showed its whole block and the run recorded it.

**What changed.** `PublishPolicy.TooSmallForBatch` (one chunk ⇔ 0 batches, pinned against the
library's own estimate) and `PublishPolicy.Effective` drop the payer; `EstimateModel` estimates and
starts that request and says "Too small for Batch — the writer pays its few transactions itself."
The Batch spec, section 6.2, carries the rule as a 2026-10-22 amendment.

**Checked on testnet.** `tiny3.json` (558 bytes, payer `batch-payer` selected): the writer-only
estimate with the note (`15-estimate-too-small-for-batch-*`), published in 14 s as
`xrpl-file://testnet/B50BBC17E47F54AFB1C73163E50904F73C2682A06C1A5EA0B1E61D8DC54B3F72`. Its History
record has no `Payer`, and no Batch trace was written. A 100 KB file still takes the Batch path
(`16-estimate-batch-*`: 103 chunks, 13 batches), published in 22 s as
`xrpl-file://testnet/3957BE190BEB8F041555688FF570562737B3BEBBCADAEC346E35866153CA7D10`.

## 3. Repeat reads and publications

**Root cause.** Every screen disposed its `CancellationTokenSource` in `Dispose`. A lifecycle method
still awaiting when its screen was closed read `_cts.Token` when it resumed, and a disposed source
throws `ObjectDisposedException`. BlazorWebView treats an exception escaping a component as fatal:
it shows its error bar and stops handling events. The window then stays on whatever it last drew,
a finished read or publication included, and nothing works until the application restarts. The
exception reached no log: the application's logger never saw it.

Reproduced in the VM, before and after, with the same steps: Accounts → Fund on a network-`1`
account whose node does not answer → Back at once → wait 60 s → choose Settings, then Read. Before
the fix (`fc70b69`): `ObjectDisposedException` at `Funding.OnInitializedAsync`, the error bar, and
neither click changed the screen (`00-before-fix-frozen-after-funding.png`). After: no error, both
clicks navigate. The same shape exists in Publish's first refresh (key list, then the payer), Accounts'
check, Settings' device-unlock switch and KeyList's delete: any of them can freeze the window when its
screen is left at the wrong moment. Which one the person hit cannot be told from their `startup.log`,
for the reason above.

**What changed.**

- `ScreenLifetime` (App.Core, tested) cancels without disposing; all 21 screens use it. A late read
  gets a cancelled token, and the method ends as a cancelled task, which `ComponentBase` ignores.
- Shell wraps the open screen in `ScreenErrorBoundary`: an exception nobody foresaw is written to
  `startup.log`, only the screen shows "This screen stopped because of an error. Choose a tab to
  carry on.", and choosing any tab recovers.
- Dismiss now leaves a clean entry screen: Read empties the link (`ReadModel.Reset`), and a started
  publication puts its file away while keeping the key and the payer (`PublishModel.RecordStarted`).

Unchanged: a running read or publication still cannot be reset (`Reset` refuses while Running), and
the window-close confirmation still reads the run's state.

**Tests** (App.Core): read → reset → read; a second read straight from a finished one;
publish → dismiss → publish; a second publication straight from a finished one; Dismiss empties the
link; a started publication clears the file; `ScreenLifetime`.

**Checked in the VM, real testnet:** publication of `tiny3.json`, Dismiss (empty picker,
`18-publish-after-dismiss-*`), a second publication of `tiny4.json`
(`xrpl-file://testnet/74E30F9A1D85263F324F941CE9D13DEC51176F5C7699903D999F864FA32F8FCB`). A 7 MB read,
Save through the real dialog with real mouse clicks (7,123,692 bytes written), Dismiss by a real click
(empty link, `24-read-after-dismiss-1280`), a second read of `tiny4.json`
(`25-read-second-finished-1280`), and a third started from History's Read button while the second
was still on its finished screen.

## 5. The application's own icon

The icon is the rail's file mark: mint `#64CF95` outline, peach `#EBAF8E` dot, on the `#1A1A1A` card
surface with a rounded `#2A2C2C` edge (`appicon.svg` background, `appiconfg.svg` foreground). The
splash uses the same glyph on `#0D0D0D` (WinUI shows no splash; it matters for other heads). Checked
in the VM: the taskbar button (`29-taskbar-icon.png`) and the exe's own icon (`30-exe-icon.png`).
The title bar shows no icon, as before; WinUI's default title bar draws none.

## 6. The window title

`ApplicationTitle`, the MAUI `Window.Title` and the page `<title>` read "XRPL File"
(`28-window-title.png`). `ApplicationId` stays `com.companyname.xrplfile.app`: the vault, the runs
and the settings live in `%APPDATA%\XrplFile` and do not depend on it, but SecureStorage, which keeps
the device-unlock secret, does. An unpackaged MAUI app keeps it in
`%LOCALAPPDATA%\<publisher>\<ApplicationId>\Settings\securestorage.dat` (on this machine
`%LOCALAPPDATA%\User Name\com.companyname.xrplfile.app\`). A new id would orphan that secret. The
reason is written next to the id in the project file.

## Not verified

- The exact steps the person took before the freeze; the fix covers every screen with the shape.
- `ScreenErrorBoundary` was not triggered in the VM: nothing left in the application throws on demand.
- A run in a state with several notes (HistoryNote, TicketNote, StateNote) was not reached.
- Damage and Vault unreachable screens were not shot (no layout of their own beyond the vault card).
