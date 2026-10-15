# Network and node server drop-downs: verification in the MauiLab VM

Date: 2026-10-15. Branch `claude/network-select`, build deployed from this worktree
(`E:\Claude\xrpl-file\network-select`, Release publish, commit `c4e4018`).

The app ran only in the MauiLab guest (Hyper-V), started non-elevated through the existing
`XrplPlain` scheduled task in the interactive session, with WebView2 DevTools on the guest's
`127.0.0.1:9222`. The lab vault was unlocked with the throwaway lab password. Real mouse clicks came
from the guest's `XrplUi` task (the native `<select>` popup is not reachable through DevTools);
screens were captured from the hypervisor with `08-vm-screen.ps1`.

## What was checked

| Check | Result | Screenshot |
|---|---|---|
| Add key, Generate: the network is a `<select>` with the empty first option, the three known networks and `NetworkID 1` (the NetworkID already in the guest's `node-urls.json`) | Pass | `add-key-generate-closed.png` |
| The opened list is dark, with the mint focus ring on the control and a visible, non-blue chevron | Pass | `add-key-generate-list-open.png` |
| Generate with nothing chosen still reports "Choose a network." | Pass | `add-key-generate-nothing-chosen.png` |
| Choosing `testnet` with a real click, then Generate, previews an address and the select reads back `testnet` | Pass | `add-key-generate-selected.png` |
| Settings, Node URLs: known networks show "Built-in (host)" selects; the user-added NetworkID row `1` keeps its text box | Pass | `settings-node-urls-default.png` |
| The mainnet list: Built-in (xrplcluster.com), s2.ripple.com, "s1.ripple.com — recent history only", Custom URL... | Pass | `settings-node-urls-mainnet-list-open.png` |
| s2.ripple.com selected on mainnet | Pass | `settings-node-urls-mainnet-s2.png` |
| Custom URL... reveals the `wss://...` text box under the select | Pass | `settings-node-urls-mainnet-custom.png` |
| Save with s2 selected writes `"mainnet": "wss://s2.ripple.com"` to `node-urls.json`; leaving Settings and coming back preselects s2 | Pass (read through DevTools and from the file) | not screenshotted |

The stock Bootstrap dark-mode chevron (`#dee2e6`) is already legible on the `--chips` background and
the `.form-select` rules already in `app.css` give the dark background, the `--control-line` border
and the mint focus ring, so `app.css` was not changed.

## Not covered

- The Import and backup-file tabs and the New account screen were not driven in the VM; they use the
  same `NetworkSelect` component and are covered by the model tests only.
- The preselection of a backup container's own network was not driven in the VM (unit-tested through
  `NetworkChoices.Pick`).
- The guest's `node-urls.json` was restored to its original content afterwards and the VM was
  returned to the Off state it was in.
