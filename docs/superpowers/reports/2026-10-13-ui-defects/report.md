# UI defects D1, D2, D3, O4: fixes verified in the MauiLab VM

Date: 2026-10-13. Branch `claude/ui-defects-d1-d2-o4-d3-0a68d9`, cut from `main` at `2a31911` (the squash merge of the dark-theme branch; its tree is identical to `claude/dark-theme-39373b`). The four defects come from the dark-theme verification report (`docs/superpowers/reports/2026-10-13-dark-theme/report.md`, section 8).

The build was published from this worktree to the MauiLab guest (`06-deploy-and-run.ps1 -Action Deploy`) and started non-elevated in the interactive session. Checks ran over WebView2 DevTools inside the guest, the same way as the dark-theme report (section 2). Stand node: `xrpl-file-ci` rippled.

## 1. Verdict

| Id | Defect | Fix | Commit | Proof | Verdict |
|---|---|---|---|---|---|
| D2 | Unlock screen after Lock showed a stale "Vault unlocked" notice | `UnlockModel.LockAsync` and `UnlockModel.NoticeLock` clear `LastMessage` | `1258695` | Three App.Core tests (below); VM: Unlock, Lock, `01-unlock-after-lock-1280.png` | pass |
| O4 | `form-label` not associated with its input on Add key and Read | `for` / `id` pairs, no text change | `50ce7ec` | VM: every visible `label.form-label` has `label.control`; a click on the Read label focuses `#read-link`. `02-add-key-backup-1280.png`, `03-read-1280.png` | pass |
| D1 | Close account banner: "...Zzhv(currently holds N drops)" | The space opens the span's own text: `<span> (currently holds ...)</span>` | `e74cbec` | Generated Razor code keeps it (`AddContent(42, " (currently holds ")`); VM, `05-close-account-1280.png` | pass |
| D3 | History link wraps after "xrpl-" | `.xf-link { word-break: break-all; }` on the History and Run link | `0937163` | VM line split at 1280 / 900 / 600, `04-history-*.png` | pass |

Build: `dotnet build XrplFile.sln`, 0 warnings, 0 errors. Suites (`dotnet run --project tests/...`): App.Core 538/538, Core 495/495, Keys 249/249.

## 2. Details

### D2

The unlock screen binds to `UnlockModel.LastMessage`. It was set by the last unlock attempt and survived `LockAsync`, so the next Unlock screen opened with "Vault unlocked". `NoticeLock` (idle lock, window close) had the same gap. Both now clear it.

Tests in `tests/XrplFile.App.Core.Tests/UnlockModelTests.cs`:

- `Locking_forgets_the_message_of_the_unlock_before_it` (red before the fix);
- `A_lock_noticed_from_somewhere_else_forgets_the_message_too` (red before the fix);
- `Noticing_nothing_keeps_the_message_of_a_failed_attempt` (guards "Wrong password" against the 1 s poll).

VM: unlock with the lab vault, press Lock in the rail. Screen text: `Unlock your vault / Master password / Unlock`, `.alert` count 0.

### O4

Ids: `add-generate-label`, `add-generate-network`, `add-import-label`, `add-import-network`, `add-import-seed`, `add-backup-password`, `add-select-label`, `add-select-network`, `read-link`. The prefix keeps them unique although only one Add key mode renders at a time.

VM output (`[text, for, label.control.id]`):

- Generate: `[["Label","add-generate-label","add-generate-label"],["Network","add-generate-network","add-generate-network"]]`
- Import a seed: `[["Label","add-import-label","add-import-label"],["Network","add-import-network","add-import-network"],["Seed","add-import-seed","add-import-seed"]]`
- Open a backup file: `[["Backup password (leave blank if it has none)","add-backup-password","add-backup-password"]]`
- Read: `[["Link","read-link","read-link"]]`, `document.activeElement.id` after a click on the label: `read-link`

Not exercised in the VM: `add-select-label` / `add-select-network`, which render only after a backup file with keys is opened. Same markup pattern as the rest.

### D1

Blazor removes whitespace next to an `@if` block, so the space between `</span>` and `@if` never reached the page. The generated `CloseAccount_razor.g.cs` (built with `EmitCompilerGeneratedFiles`) now carries `" (currently holds "` as the span's first content.

VM: throwaway account, destination "unfunded", Check (preflight only, "Close permanently" not pressed). Banner text: `Closing: throwaway — rwWmGwnGGGUV3so5EHXWHwga1KYErGZzhv (currently holds 1,000,000,000 drops)`. The warning above it ("There is no such account on this network...") is the expected answer for an unfunded destination.

### D3

`overflow-wrap: anywhere` on `.xf-mono` still prefers the hyphen as a break point. `.xf-link` adds `word-break: break-all`; applied to the History link and to the Run "Published." link, which uses the same `xrpl-file://` text.

VM, first History row, lines as rendered:

| Width | Lines | Overflow (`xf.over()`) | Screenshot |
|---|---|---|---|
| 1280 | `xrpl-file://1/8C1B3917653318E3298F489E257C5C59927FE55B75B3F2` / `F9845C43349283EC9A` | `[true,true,1280,1280,[1208,1208]]` | `04-history-1280.png` |
| 900 | `xrpl-file://1/8C1B3917653318E3298F489E257C5C59927` / `FE55B75B3F2F9845C43349283EC9A` | `[true,true,900,900,[828,828]]` | `04-history-900.png` |
| 600 | `xrpl-file://1/8C1B3` / `917653318E3298F489E` / `257C5C59927FE55B75B` / `3F2F9845C43349283EC` / `9A` | `[true,true,600,600,[585,585]]` | `04-history-600.png` |

The Run screen was not re-run in the VM for this change (a run needs a ledger publication); its link carries the same class.

## 3. Guest state left

App running from `C:\Lab\app` (this build), vault unlocked, Accounts screen, no width override. Nothing published, closed or funded.
