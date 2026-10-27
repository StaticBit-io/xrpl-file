# Streaming publication: acceptance on testnet in the MauiLab VM (Task 22)

Date: 2026-10-26 (guest clock 2026-10-27 00:25Z to 01:17Z). Branch `claude/streaming-publication-2d065e`, deployed commit `4dd0ce89822c355c0396e4b2004440df70f4b887` (`4dd0ce8`), built Release from this worktree and published to the guest with `06-deploy-and-run.ps1 -Action Deploy`. Spec: `docs/superpowers/specs/2026-10-23-streaming-publication-design.md`, section 6.5, S11-S15, S24, S25, S27.

Nothing was fixed in this task. Defects are in section 10, each with a screenshot and a one-line reproduction.

## 1. Verdict

| Step | Result |
|---|---|
| Deploy, non-elevated launch with DevTools | Pass |
| Five-minute sleep timeout, application keeps the guest awake | Pass (SYSTEM request listed; no Kernel-Power 42 event; guest untouched about 9 minutes) |
| Payer required above 64 MiB (S1) | Pass |
| Estimate, "This publication is large", checkbox, reset on Back | Pass |
| Publish, hashing line, writing line | Pass |
| Lock mid-run | Pass: run stopped 0.8 s after the click, Unlock screen 2.0 s after |
| Resume: no file dialog, hashing, history, "Resuming costs" with checkbox, writing, "Published." | Pass (no separate fence-wait line seen, see 6) |
| Read: size and "About ... to read" before the button; read-back SHA-256 equal to the source | Pass |
| Peak private bytes against the S27 300 MB bound | Fail, see D1: 709.9 MB during the resume's ledger-history read |
| Wall time against the estimate's range | Faster than the range (37 min 53 s against 1 h 3 min to 1 h 16 min) |

## 2. Environment

| Item | Value |
|---|---|
| Host build | `dotnet publish` Release, `net10.0-windows10.0.19041.0`, from `E:\Claude\xrpl-file\streaming-publication-2d065e` at `4dd0ce8`; guest exe written 2026-10-26 21:15 (host local) |
| Guest | MauiLab (Hyper-V, Windows 11, 8 GB startup memory, 4 vCPU), user `lab`, task `XrplPlain` (interactive, `-RunLevel Limited`), `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`, native window 1424 px (screenshots are emulated at 1280 px through CDP) |
| Network | `testnet`, node `wss://s.altnet.rippletest.net:51233` (shown on the estimate card: "Node: wss://s.altnet.rippletest.net:51233 (testnet)"); rippled 3.4.1, NetworkID 1, reserve 1 XRP + 0.2 XRP |
| Keys | Existing vault keys `batch-writer` `r44DFo16gPv4k3ug61QnLxArL3kLzUbPkW` and `batch-payer` `rL54SuvLqRrKtvx5izGEwKCBTdr2KPYzHa`, network `testnet`, both funded from the testnet faucet in an earlier session (about 100 XRP each). No seed or secret appears here |
| File | `accept-300mb.mp4`, generated with ffmpeg (`testsrc2` 1920x1080 at 30 fps, 600 s, x264 CBR 4 Mbit/s), copied to `C:\Lab\accept-300mb.mp4` with `Copy-VMFile` |
| Size | 300,343,426 bytes (286.4 MiB) |
| SHA-256 | `E5C5C8D139CBBDA964C530B9C99E6161136169EB9A48AFCB09CDF259DF720ED9` (host and guest agree) |
| Vault | lab throwaway master password, existing vault |

Notes: the keys were the vault's existing testnet keys, not freshly created ones (the controller's ruling: use them if present and funded). The writer had a few earlier transactions (sequence 21,760,918 at the start). The Accounts screen shows no balances; the funded state came from "Check" (`can be closed now`) and the estimate card (`01-accounts.jpg`).

## 3. Sleep timeout

Recorded before the run (`powercfg /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE`, Balanced scheme `381b4222-f694-41f0-9685-ff5bb260df2e`):

| Setting | Before | During the run | After (restored) |
|---|---|---|---|
| Sleep after, AC | 0x00000000 (never) | 300 s | 0x00000000 |
| Sleep after, DC | 0x00000258 (600 s) | 300 s | 0x00000258 |

`powercfg /requests` 1 min 30 s into the run (00:27:25Z):

```
DISPLAY:
None.

SYSTEM:
[PROCESS] \Device\HarddiskVolume3\Lab\app\XrplFile.App.exe

AWAYMODE:
None.

EXECUTION:
None.

PERFBOOST:
None.

ACTIVELOCKSCREEN:
None.
```

The guest was left alone from 00:27:25Z to 00:36:16Z (about 9 minutes, longer than the 5-minute timeout); the run went from 7,240 to 90,384 chunks in that window. Kernel-Power event 42 (`Get-WinEvent ... Id = 42`): none before the run, none at 00:36Z (10 minutes in), none at 01:04Z (after "Published."). The guest's last boot time was unchanged (2026-10-26 19:48:51 guest local). After the run `powercfg /requests` lists no SYSTEM request (the application had released its lease), as expected.

## 4. Estimate (S11, S12, S25)

Screenshots: `02-payer-required.jpg` (payer "Nobody": "Choose a payer - files larger than 64 MiB publish through Batch."), `03-summary.jpg`.

| Figure | Value |
|---|---|
| Chunks | 300,344 |
| Batches | 37,547 |
| Solo transactions | 1 |
| Fee unit | 15 drops |
| Transactions ("This publication is large") | 337,922 |
| Duration range | 1 h 3 min to 1 h 16 min (also on the main card) |
| Payer fees | 6.195225 XRP |
| Payer fees in the worst case | 67.58439 XRP; needs 68.58439 XRP (balance 97.711045 XRP) |
| Writer fees | 0.000015 XRP |
| Writer fees in the worst case | 27.04125 XRP; needs 28.04125 XRP (balance 99.996305 XRP) |

Checkbox: Publish was disabled until "I understand the cost and the time" was ticked. After a tick, Back and Estimate again, the checkbox was clear and Publish disabled again (one scripted pass: before Back ticked and enabled, after the second estimate clear and disabled). With "Nobody" chosen the note reads "Slower, and holds up to 50 XRP of reserve while publishing."

## 5. The run: times and rates

All times are guest UTC.

| Event | Time |
|---|---|
| Publish pressed | 00:25:57.8 |
| "Hashing the file" first seen | 00:26:00.9 (`04-hashing.jpg`); hashed 300,344 of 300,344 chunks in 0:32 |
| "Writing the file" | about 00:26:30; early rate 137-151 chunks/s, "about 36 min left" at 1:25 (`04-run.jpg`) |
| Lock pressed (98,304 of 300,344 written, 10:44 in) | 00:36:42.2 |
| Unlock pressed | 00:37:30.9 (locked interval 48.7 s) |
| Resume pressed | 00:37:54.95 |
| "Resuming costs" shown | 00:42:58 |
| Continue pressed (checkbox ticked) | 00:43:17.8 |
| "Published." (trace `end Succeeded`) | 01:04:39.3 |

Wall time Publish to "Published.": 38 min 41 s. Excluding the locked interval (48.7 s): 37 min 53 s. The application's own "Finished in 26:43" counts only the resumed segment (D2).

| Segment | Duration |
|---|---|
| Hashing (first) | 0:32 |
| Writing before the Lock | 10:12 (98,304 chunks) |
| Resume preparation (hashing 1:05, history read 3:57; the confirmation then waited about 19 s for the scripted tick) | 5:25 |
| Writing after Continue | 20:55 (202,040 chunks; the rate line moved between 100 and 264 chunks/s) |
| Block list and read-back | 0:25 (30 list records, then 4 of 4 blocks read back) |

Average writing rate about 160 chunks/s on both segments. Against the duration range: the run took 37 min 53 s net, 60% of the estimate's lower bound (1 h 3 min) and 50% of its upper bound. The estimate priced writing at S11's 85 and 70 memos/s; the observed rate was about twice that. The resume's own quote ("Resuming costs", Time 43 min to 51 min for 227,330 transactions) was also high: the resumed segment took 26:43 including the preparation.

Spend: payer 97.711045 XRP before (estimate card), 91.515820 XRP after (testnet `account_info`, validated): 6.195225 XRP spent, exactly the estimate's "Fees" line and 9.2% of its worst case. Writer 99.996305 before, 99.996290 after: 15 drops. Account sequences moved by 300,375 (writer) and 37,547 (payer, equal to the batch count).

## 6. Lock and resume (S13, S14, S24, S25)

Screenshots: `05-locked.jpg`, `05b-publish-after-lock.jpg`, `06-resume-confirm.jpg`, `07-published.jpg`.

- Lock: the shell's Lock click at 00:36:42.2; the batch trace ends the run (`end Cancelled`) at 00:36:42.99, 0.8 s later; the Unlock screen showed at 00:36:44.2, 2.0 s after the click (bound: 10 s). The next unlock landed on Keys.
- After the unlock, Publish shows the run screen as it stopped: "Stopped while writing the file - 98,304 of 300,344 · 10:45 elapsed" and "Cancelled. Nothing further was submitted." (`05b`). Under "Unfinished runs" the row `accept-300mb.mp4 (300,343,426 bytes) testnet Batch, paid by batch-payer, Writing the file, 300,344` is there (the other six rows are runs of earlier sessions and were not touched).
- Resume (sequence from a 250 ms poll of the run screen): "Starting" (0:00); no file dialog (the recorded path `C:\Lab\accept-300mb.mp4` was reopened); "Hashing the file - 1,024 of 300,344 chunks" up to 300,032 (1:05 elapsed); "Reading the ledger history" (counts up in thousands of records, 98,000 records after 3:57); then the confirmation: "RESUMING COSTS" with Time 43 min to 51 min, Transactions 227,330, Payer batch-payer needs 46.46599 XRP (worst-case fees 45.46599), Writer batch-writer needs 19.19389 XRP (worst-case fees 18.19389), and the checkbox "I understand the cost and the time". Continue was disabled until the box was ticked.
- After Continue: "Reading the ledger history" for 2 s, then "Writing the file - 98,304 of 300,344 · about 51 min left" at 5:24. No separate fence-wait line was observed: the recorded fence ledger (21,851,486) had long passed, so there was nothing to wait for. The fence wait was therefore not exercised by this run.
- Then "Writing the block list - 0 of 30 / 30 of 30", "Reading it back from the ledger - 1 of 4 ... 4 of 4", and "Published." with the link `xrpl-file://testnet/4A4C3CCD6C079484AB78A6EDB6F8F2203A5316375725AF5A9B2B96291EB6A5CE` (`07-published.jpg`).

## 7. Read back (S9, S11)

Screenshots: `08-read-lookup.jpg`, `09-read.jpg`. Lookup (1.5 s): `accept-300mb.mp4`, 300,343,426 bytes, "About 9 min to read", type application/octet-stream, written by the writer key, 294 blocks, node `wss://s.altnet.rippletest.net:51233`, Read button below. Read: 0 of 294 blocks at 01:05:31, "Read "accept-300mb.mp4" successfully." by 01:16:31 (about 11 min against "About 9 min"), saved to `C:\Lab\accept-readback.mp4` ("Saved."). Saved file size 300,343,426 bytes, SHA-256 `E5C5C8D139CBBDA964C530B9C99E6161136169EB9A48AFCB09CDF259DF720ED9`: identical to the source.

## 8. Private bytes (S27)

`private-bytes.csv` in this folder (30 s samples of `Process.PrivateMemorySize64`, 105 samples, 00:25:44Z to 01:17:44Z).

| Phase | Private bytes (MiB) |
|---|---|
| Idle before Publish | 64-68 |
| First hash and write (00:26-00:37) | 100-205, typical 115-170 |
| Resume hashing (00:37-00:39) | 341-395 |
| Resume ledger-history read (00:39-00:42) | 531, **677** (peak, 709,857,280 bytes), 513, 545 held, then 89 at the confirmation |
| Writing after Continue (00:43-01:04) | 92-251, typical 115-185 |
| Read-back in the Read tab (01:05-01:17) | 83-91, flat |

Bound: S27 asks for a peak below 300 MB for the 800 MB source and, for the slope, 40 MB between 50 MB and 800 MB. The writing and read phases stay under it (peak 251 MiB while writing, 91 MiB while reading), but the resume's history read reached 709.9 MB for 98,000 landed records, 2.4 times the bound, on a 300 MB file. See D1.

## 9. Restored state

Sampling job stopped (csv copied here), sleep timeouts restored to AC never and DC 600 s, scheduled task `AccKeys` and helper files removed, the 300 MB source and read-back copies deleted from the guest, the application closed, the VM left Saved. Nothing ran on the host except ffmpeg and the build; no container was touched.

## 10. Defects

None was fixed.

| Id | Severity | Defect | Screenshot | Reproduction |
|---|---|---|---|---|
| D1 | Major | The resume's "Reading the ledger history" holds hundreds of MB: 709.9 MB peak (about 340-395 MiB already while the resume hashes the file) for 98,000 records, over S27's 300 MB bound; the figure follows the number of landed records, so a fuller resume would use more | `06-resume-confirm.jpg`, `private-bytes.csv` | Publish a 300 MB file with a payer, Lock after about 10 minutes, unlock, Resume, watch `PrivateMemorySize64` during "Reading the ledger history" |
| D2 | Minor | After a resume the run screen says "Finished in 26:43", the resumed segment only; the whole publication took 38:41 (37:53 net of the lock) | `07-published.jpg` | Lock mid-run, resume, finish; compare "Finished in" with the wall time |
| D3 | Minor | While the resume confirmation waits for the person, the stage line above it stays on "Reading the ledger history - 98,000 records · 5:02 elapsed" with a 0% bar and a running timer, and both "Stop" (inside the card) and "Cancel" (below it) appear | `06-resume-confirm.jpg` | Resume an interrupted Batch run and wait on "Resuming costs" |
| O1 | Observation | The duration range (1 h 3 min to 1 h 16 min) was about twice the real 37 min 53 s, and the read's "About 9 min" was about 11 min; the estimate uses S11's 85 and 70 memos/s while the testnet node gave about 160 chunks/s | `03-summary.jpg`, `08-read-lookup.jpg` | This run |
| O2 | Observation | The history read took 3 min 57 s of the 5 min 25 s resume preparation for 98,000 records (about 400 records/s); on a 300,000-record resume that stage alone would be about 12 minutes | `06-resume-confirm.jpg` | Resume as above |
| O3 | Observation | The fence wait was not exercised (the fence ledger had long passed); the `07` screenshot also keeps the "Choose a file to publish." notice under the "Published." panel | `07-published.jpg` | Resume more than a few ledgers after the stop |
| O4 | Observation | The Accounts screen shows no balance; the funded state is a "can be closed now" status only | `01-accounts.jpg` | Open Accounts |

## 11. Files

`01-accounts.jpg`, `02-payer-required.jpg`, `03-summary.jpg`, `04-hashing.jpg`, `04-run.jpg`, `05-locked.jpg`, `05b-publish-after-lock.jpg`, `06-resume-confirm.jpg`, `07-published.jpg`, `08-read-lookup.jpg`, `09-read.jpg`, `private-bytes.csv`. The screenshots are JPEG (CDP `Page.captureScreenshot`, quality 70, 1280 px wide), all under 100 KB.
