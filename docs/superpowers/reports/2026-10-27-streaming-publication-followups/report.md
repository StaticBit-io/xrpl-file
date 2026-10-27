# Streaming publication follow-ups: acceptance on testnet in the MauiLab VM (Task 17)

Date: 2026-10-27 (guest clock 19:56Z to 21:20Z). Branch `claude/streaming-publication-2d065e`, deployed commit `d8857bf75e427048cbc56f533f23865f5bad2408` (`d8857bf`), built Release from this worktree and published to the guest with `06-deploy-and-run.ps1 -Action Deploy`. Plan: `docs/superpowers/plans/2026-10-27-streaming-publication-followups.md`, Task 17.

Nothing was fixed in this task. Methods as in the 2026-10-26 report (task `XrplPlain`, `-RunLevel Limited`, DevTools on the guest's loopback 9222 over PowerShell Direct; screenshots by CDP `Page.captureScreenshot`, JPEG quality 70, 1280 px, each under 100 KB; private-bytes sampler on the guest, now every 5 s).

The final review's fix wave (`938f2ff`..`9ab44aa`) landed after this deployment. It changes the one-account resume (S17 within the window, amendment 5), documentation wording, the history source's handling of a non-object result, and the Accounts screen (networks read concurrently, the ledger read once per connection, the Status text of an unchecked row). None of these is a Batch publish or read path exercised below except the Accounts screen; its new behaviour is covered by unit tests, not by this acceptance.

## 1. Verdict

| Step | Result |
|---|---|
| 1. Deploy, non-elevated launch with DevTools, unlock | Pass |
| 2. Accounts balances (O4) | Pass for funded and closed rows; the no-account row not exercised (section 3) |
| 3. Files, sizes, SHA-256 | Done |
| 4. (b) Fence wait on 30 MB, twice | Pass: the fence line seen both times, order Starting, Hashing, fence, history, confirmation |
| 4. Confirmation look (D3) | Pass: "Waiting for you to confirm the cost below.", no progress bar, one Cancel, inside the card |
| 5. (a) 300 MB with Lock and Resume | Pass: no file dialog, hashing, history, a confirmation pricing only the remaining work, Published |
| 5. Memory (D1) | Pass: 245.7 MB peak against 300 MB; the resume's history read 73-78 MB (709.9 MB on 2026-10-26) |
| D2: "Finished in" against net wall time | Pass in all three runs |
| 6. (d) Read back | Pass: three SHA-256 equal to the sources |

## 2. Environment

| Item | Value |
|---|---|
| Commit | `d8857bf75e427048cbc56f533f23865f5bad2408` |
| Guest | MauiLab (Hyper-V, Windows 11), user `lab`, task `XrplPlain`, `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`; guest exe written 19:03Z; application started 19:55Z |
| Node | `wss://s.altnet.rippletest.net:51233 (testnet)`, shown on every estimate card |
| Keys | Existing vault keys `batch-writer` `r44DFo16gPv4k3ug61QnLxArL3kLzUbPkW`, `batch-payer` `rL54SuvLqRrKtvx5izGEwKCBTdr2KPYzHa`. No seed or password appears here |
| Guest sleep | Not changed (AC never, DC 600 s before and after) |

## 3. Accounts (O4)

Screenshots: `01-accounts-balances.jpg` (before), `11-accounts-after.jpg`.

On opening Accounts, every row reads "Checking…" in the Balance column within 0.23 s (sampled at 100 ms). After about 10 s:

| Row | Balance | Status |
|---|---|---|
| batch-writer (testnet) | 99.99629 XRP | "Closable from ledger 22,061,547 — about 190,191 ledgers remaining (about 9 days)" |
| batch-payer (testnet) | 91.51582 XRP | "can be closed now" |
| stand key (closed) | empty | "Closed — sent 797.9671 XRP to rHb9…" |
| five keys on network `1` | "Not available. Press Check to try again." | "Not checked yet" |

The Status column does not repeat the balance. The two testnet rows resolved while the network-`1` rows failed, so one unreachable network does not mark the other's rows.

Not exercised: a key with no account yet. The vault's only such key (`unfunded`) is on network `1`, whose node URL in Settings is `ws://172.19.16.1:6006` (the standalone stand), unreachable from the guest, so its row shows the failure text. The vault has no testnet key without an account and the plan forbids making new ones. Under the controller's ruling D3 such a row's Balance is empty and its Status says there is no account yet; unit tests cover it.

After the three runs: batch-writer 99.996245 XRP, batch-payer 84.08194 XRP (`11-accounts-after.jpg`).

## 4. The files

Made on the host with ffmpeg (`testsrc2` 1920x1080, x264, 4 Mbit/s), copied with `Copy-VMFile` to `C:\Lab\`; hashes agree on host and guest.

| File | Bytes | SHA-256 |
|---|---|---|
| `accept-300mb.mp4` (30 fps, 600 s) | 300,274,855 | `DD0178D3FAC80206C896CC3238BA03DA57E2C8D529551FAF342A7BF9658CDC22` |
| `accept-30mb.mp4` (30 fps, 60 s) | 30,054,972 | `D2E96C432F641833A51F02875750CFBF11711BF14B263C9C662397FB7CC5E94A` |
| `accept-30mb-b.mp4` (25 fps, 60 s; the second 30 MB run) | 30,057,529 | `3241183A408BE6FFCE76718C9CBFC8D4C23EA3494BBFDF8611CBDA12F6D33BD5` |

## 5. (b) The fence wait, 30 MB

Estimate (`02-estimate-30mb.jpg`): 30,055 chunks, 3,758 batches, Time 4 min to 8 min, 33,817 transactions, payer fees 0.61998 XRP (worst case 6.76329, needs 7.76329), writer fees 0.000015 XRP (worst case 2.71281). "This publication is large" with the checkbox; ticked, Publish.

Run A (all times guest UTC):

| Event | Time |
|---|---|
| Publish pressed | 19:59:58.879 |
| "Writing the file - 632 of 30,055" seen, Lock clicked | 20:00:38.911 |
| Unlock screen shown / Unlock pressed | 20:00:39.159 / 20:00:39.272 (trace `end Cancelled` 20:00:39.000) |
| Resume pressed (Publish > Unfinished runs) | 20:00:43.224 |
| Fence line first seen: "Waiting for earlier transactions to expire - about 57 s" | 20:01:08.665 (`03-fence-wait.jpg`: 45%, about 39 s, at 20:01:22) |
| "Reading the ledger history" (fence over) | 20:01:59.461 |
| Confirmation shown | 20:02:06.465 |
| Continue pressed (box ticked) | 20:02:30.436 |
| Published (trace `end Succeeded`) | 20:05:56.074 |

The fence began about 20:00:50 (the bar read 25% with 57 s left at 20:01:08), so it lasted about 69 s; 51 s of it were watched. The fence had not passed when the resume reached it. Run A's watcher began after the hashing stage, so run B recorded the whole sequence.

Run B (`accept-30mb-b.mp4`): Publish 20:09:10.206, Lock 20:09:29.256, Unlock 20:09:29.400, Resume 20:09:29.953; "Starting" 20:09:30.9, "Hashing the file" 20:09:31.9, "Waiting for earlier transactions to expire - about 84 s" 20:09:32.98, "Reading the ledger history" 20:10:47.93 (fence 75 s), confirmation 20:10:58.98 (Time 4 min to 7 min, 29,213 transactions), Continue 20:11:14.45, Published with "Finished in 4:52" (net wall time about 4:50; the moment of "Published." was not captured to the second).

Confirmation (`04-confirm-30mb.jpg`, DOM checked): the line is "Waiting for you to confirm the cost below."; no progress bar; one Cancel, inside the card next to Continue; Continue disabled until the box is ticked. Time 4 min to 8 min, 31,747 transactions, payer needs 7.34929 XRP, writer needs 3.54721 XRP.

Time check, run A: Publish to Published 357.2 s, less Lock-to-Resume 4.3 s, less the confirmation wait 24.0 s = 328.9 s (5:28.9); "Finished in 5:27" (1.9 s apart; D2 holds). Inside the estimate's 4 to 8 min. `05-published-30mb.jpg`.

## 6. (a) 300 MB with Lock and Resume

Estimate (`06-estimate-300mb.jpg`): 300,275 chunks, 37,539 batches, 1 solo transaction, fee unit 15 drops, **Time 36 min to 1 h 17 min**, "This publication is large", 337,845 transactions; payer fees 6.19383 XRP (worst case 67.56885, needs 68.56885, balance 90.27577); writer fees 0.000015 XRP (worst case 27.03504, needs 28.03504, balance 99.99626).

| Event | Time |
|---|---|
| Publish pressed | 20:16:04.378 |
| Lock (84,216 of 300,275 written, 10:06 in, 76 chunks/s) | 20:26:10.503 (Unlock screen 83 ms later) |
| Unlock pressed (about 62 s after Lock) | 20:27:12.832 |
| Resume pressed | 20:27:13.586 |
| Hashing the file (38 s) | 20:27:14.9 to 20:27:52.96 |
| Reading the ledger history | 20:27:53 to 20:31:08.9 (88,000 records) |
| Confirmation shown (`07-confirm-300mb.jpg`) | 20:31:08.93 |
| Continue pressed (about 21 s later) | 20:31:29.82 |
| Published (trace `end Succeeded`) | 20:53:32.195 |

No file dialog on Resume. No separate fence line: the fence had passed (101 s between Lock and the end of hashing); the 30 MB runs above are the fence evidence.

Confirmation: "Time 25 min to 54 min", Transactions 238,098 (70.5% of 337,845, matching 25-54 min being 70% of 36-77 min), payer needs 48.61945 XRP (fees 47.61945), writer needs 20.05528 XRP (fees 19.05528). The Time prices the remaining work only: neither the hash pass (38 s) nor the history read (3 min 16 s) is in it. Same look as the 30 MB confirmation.

Time check: Publish to Published 2,247.8 s (37:27.8); less Lock-to-Resume 63.1 s; less the confirmation wait 20.9 s = 2,163.8 s = **36:03.8**; "Finished in **36:03**" (D2 holds, `08-published-300mb.jpg`). Against the estimate's range (36 min to 1 h 17 min) the net time sits at the lower bound. Writing rate: 76 chunks/s at the start, 130-167 chunks/s later.

## 7. Private bytes (D1, S27)

`private-bytes-300mb.csv` (455 samples, 5 s, 20:15:56Z to 20:53:50Z) and `private-bytes-30mb.csv` (178 samples, both 30 MB runs). Bound: 300,000,000 bytes.

| Measure | Bytes |
|---|---|
| 300 MB run, peak excluding [unlock press, +5 s] | **245,682,176** at 20:25:32 (first writing segment, about 9.5 min in) |
| 300 MB run, inside the window [20:27:12.832, 20:27:17.832] | **65,953,792** (one sample, 20:27:17.822) |
| Sample 11 ms before the unlock press | 130,584,576 (the level held while locked) |
| Next sample after the window | 66,207,744 |
| Resume hashing (20:27:15-20:27:52) | 71-79 MB |
| Resume ledger-history read (20:27:53-20:31:08) | 73-78 MB (2026-10-26: 709,857,280) |
| Writing after Continue | 93 MB rising to 218.7 MB (20:32:48), then back to 135 MB |
| 30 MB runs, peak | 215,719,936 |

The sampler's 5 s step cannot see a spike shorter than 5 s: the unlock window holds one sample, so the figure inside it is a sampled value, not a true peak of the about one-second Argon2id derivation (amendment 4 puts that outside S27).

## 8. (d) Read back

Screenshots: `09-read-lookup.jpg` (300 MB), `10-read.jpg`.

| Link | Lookup | Read | Saved size | Saved SHA-256 | Equal to source |
|---|---|---|---|---|---|
| `xrpl-file://testnet/3372DA7F7373FD1D8ADC4CAC69B984C240699D3EAEEF91638FCD71C7178AFC9F` (300 MB) | 1.5 s: 300,274,855 bytes, "About 9 min to read", 294 blocks | 20:54:50 to about 21:05:50, about 11 min | 300,274,855 | `DD0178D3FAC80206C896CC3238BA03DA57E2C8D529551FAF342A7BF9658CDC22` | yes |
| `xrpl-file://testnet/CC943CCE78CEFB9C7F5A8EC605B5F9D67F093064E6541426836CAD01524BC76F` (30 MB, run A) | "About 53 s to read", 30 blocks | started 21:07:26 | 30,054,972 | `D2E96C432F641833A51F02875750CFBF11711BF14B263C9C662397FB7CC5E94A` | yes |
| `xrpl-file://testnet/F5A6E05F1564550D3CC3DF0E16A30139F033D959E42CF0A074A95240A6CCB5D3` (30 MB, run B) | "About 53 s to read" | 21:18:36.8 to 21:19:43.2 (66 s) | 30,057,529 | `3241183A408BE6FFCE76718C9CBFC8D4C23EA3494BBFDF8611CBDA12F6D33BD5` | yes |

## 9. Spend

| Account | Before | After | Spent |
|---|---|---|---|
| batch-payer | 91.51582 XRP | 84.08194 XRP | 7.43388 XRP = 0.61998 (run A) + 0.62007 (run B) + 6.19383 (300 MB), each exactly the estimate's "Fees" line; 9.0% of the 300 MB worst case |
| batch-writer | 99.99629 XRP | 99.996245 XRP | 45 drops (3 x 15 drops) |

## 10. Restored state

Sampler stopped (both CSVs copied here), the application closed (no `XrplFile.App` or its WebView2 processes left), the helper scheduled task removed, guest files removed (`accept-*.mp4`, read-back copies, helper scripts, the guest CSVs), guest sleep timeouts unchanged, host copies of the videos removed, VM left Saved. Nothing ran on the host except ffmpeg and the build; no container and no port 6006 holder were touched.

## 11. Defects and observations

No defect.

| Id | Observation | Reproduction |
|---|---|---|
| N1 | The Accounts banner "The node did not answer. Nothing was spent. Check the node URL in Settings and try again." appears for a balance read (`01`, `11`); "Nothing was spent" is odd for a read-only check | Open Accounts with a network whose node is unreachable |
| N2 | While a row reads "Checking…" its Status keeps the previous status text (e.g. "Closable from ledger ...") | Open Accounts a second time and look in the first second |
| N3 | The 15-minute idle lock fired during a read (about 21:08:33, 15 min after the last signature); the Unlock screen replaced the Read screen and the read finished anyway, with its result on the Read screen after the unlock. By design: reads do not touch the vault | Start a read more than 15 min after the last signature |
| N4 | Not exercised: the no-account Accounts row (section 3) | Needs a testnet key without an account |

## 12. Files

`01-accounts-balances.jpg`, `02-estimate-30mb.jpg`, `03-fence-wait.jpg`, `04-confirm-30mb.jpg`, `05-published-30mb.jpg`, `06-estimate-300mb.jpg`, `07-confirm-300mb.jpg`, `08-published-300mb.jpg`, `09-read-lookup.jpg`, `10-read.jpg`, `11-accounts-after.jpg`, `private-bytes-300mb.csv`, `private-bytes-30mb.csv`. Screenshots are JPEG (CDP, quality 70, 1280 px), all under 100 KB (largest 80,851 bytes).
