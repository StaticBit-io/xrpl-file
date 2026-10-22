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
card's `scrollWidth/clientWidth`): every one of the 88 screen × width checks is `page-ok`, and no
Blazor error bar appeared. Every table fits its card at 1900 and 1280 px, and a separate sweep found the
same at 1100, 1000 and 900 px. Keys needs 684 px of columns, about an 840 px window: at 800 px it
scrolls inside its card (684/667). In the main pass the only tables that scroll inside their card
are at 600 px: Keys (684/551) and Unfinished runs (589/551), as section 3 allows below 720 px.

Screens: `01`–`06` Keys, Add key (three tabs), Export, Reveal; `07`–`10` Accounts, New account,
Funding, Close account; `11`–`12` Settings, Change password; `13` History; `14` Publish with
Unfinished runs; `15`–`16` Estimate; `19` Run; `20`–`22` Read; `26`–`27` Unlock, Create vault;
`31`–`34` the finished screens with their entry controls.

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

Two causes, both fixed.

**The finished result stood in the way.** A finished read or publication replaced its tab's entry
screen until Dismiss was pressed, and the person testing did not see Dismiss as the way to a new
start, so they did not press it. Now:

- Read: the outcome (Read "…" successfully, Save…, Saved) sits above the link box, which is empty and
  ready. Read on a new lookup replaces the outcome and empties the box again (`ReadModel.StartRead`).
- Publish: the outcome ("Finished in …", the link, Copy link, Resume where it applies) sits above the
  picker and Unfinished runs. Starting the next publication replaces it; the started file is put away,
  the key and the payer stay (`PublishModel.RecordStarted`).
- Dismiss is gone. Switching tabs and coming back changes nothing: the outcome is still there, and so
  is the entry. History keeps every link.
- A running read or publication is still drawn alone, with Cancel (`RunModel.IsRunning`,
  `ReadState.Running`); `ReadRun.Start` and `PublicationRun.Start` still refuse while one runs, and the
  window-close confirmation still reads the run's state.
- The dark-theme spec (section 5) and the Batch spec (6.3) carry the change.

**A screen closed mid-await froze the window.** Every screen disposed its `CancellationTokenSource`
in `Dispose`. A lifecycle method still awaiting when its screen was closed read `_cts.Token` when it
resumed, and a disposed source throws `ObjectDisposedException`. BlazorWebView treats an exception
escaping a component as fatal: it shows its error bar and stops handling events, so the window stays
on whatever it last drew until the application restarts. The exception reached no log.

Reproduced in the VM, before and after, with the same steps: Accounts → Fund on a network-`1`
account whose node does not answer → Back at once → wait 60 s → choose Settings, then Read. Before
the fix (`fc70b69`): `ObjectDisposedException` at `Funding.OnInitializedAsync`, the error bar, and
neither click changed the screen (`00-before-fix-frozen-after-funding.png`). After: no error, both
clicks navigate. The same shape exists in Publish's first refresh, Accounts' check, Settings'
device-unlock switch and KeyList's delete.

- `ScreenLifetime` (App.Core, tested) cancels without disposing; all 21 screens use it. A late read
  gets a cancelled token, and the method ends as a cancelled task, which `ComponentBase` ignores.
- Shell wraps the open screen in `ScreenErrorBoundary`: an exception nobody foresaw is written to
  `startup.log`, only the screen shows "This screen stopped because of an error. Choose a tab to
  carry on.", and choosing any tab recovers.

**Tests** (App.Core): a finished read followed by a new read without anything put away, with a tab
switch in between; Read does nothing without a manifest; a finished publication followed by a new
one, with a tab switch in between; read → reset → read; publish → dismiss → publish; a started
publication clears its file; `ScreenLifetime`.

**Checked in the VM with real mouse clicks and real keystrokes, testnet:**

- Read `tiny4.json` (link typed, Look up, Read) → outcome above an empty link box
  (`31-read-finished-with-entry-*`) → History and back to Read: unchanged → second link typed and
  looked up under the outcome (`32-read-next-looked-up-1280`) → Read: the outcome is now
  `tiny3.json`, the box empty again.
- Earlier in the pass: a 7 MB read saved through the real Save dialog (7,123,692 bytes written).
- Publish `next1.json` (dialog, key, Estimate, Publish) → outcome above the picker
  (`33-publish-finished-with-picker-*`,
  `xrpl-file://testnet/48A46DCE9A9E3DB1C8034E8081DBFC8B834261AD7BED3B6DAC48138185C42D22`) → Keys and
  back: unchanged → `next2.json` chosen, estimated and published from that screen
  (`34-publish-second-from-finished-1280`,
  `xrpl-file://testnet/B95E3A9137463A3B6950F48960D1E85835B38D600904493BA965995F6B55855B`, in History).
- No step showed the error bar.

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

- Whether the person's session also hit the freeze: they did not press Dismiss, which explains what they
  saw on its own, and their `startup.log` could not have shown the freeze either way.
- `ScreenErrorBoundary` was not triggered in the VM: nothing left in the application throws on demand.
- A run in a state with several notes (HistoryNote, TicketNote, StateNote), and a failed run's Resume
  under its outcome, were not reached.
- Damage and Vault unreachable screens were not shot (no layout of their own beyond the vault card).
