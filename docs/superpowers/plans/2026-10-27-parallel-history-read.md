# Concurrent History Read Implementation Plan

**Goal:** a resume reads the writer's history (`account_tx`) as several ledger ranges at once and hands the reconciliation exactly the records, in exactly the order, of one forward walk; at least twice as fast on testnet, with memory bounded by a constant.

**Spec:** `docs/superpowers/specs/2026-10-27-parallel-history-read-design.md` (H1–H11, rulings in section 6).

**Evidence:** `docs/superpowers/reports/2026-10-27-history-read/report.md` (the node's answer is 98.8% of the read); results of this plan in `docs/superpowers/reports/2026-10-27-history-read/concurrent.md`.

## Global constraints

- Branch on top of `claude/streaming-publication-2d065e`. .NET 10, explicit types, `ConfigureAwait(false)` in `src/`, comments explain why, English only, no attribution lines in commits.
- Unit runs: `dotnet run --project tests/<P> -- -trait- "Category=Integration" -trait- "Category=Slow"`. Stand runs: `XRPL_FILE_NODE_URL=ws://localhost:16006`, compose project `xrpl-file-ci` only. Slow runs: `-trait "Category=Slow"` (S27 and `ResumeMemoryStandaloneTests`, about 20 minutes).
- Testnet: read-only `account_tx`, `server_info`, `ledger`; never mainnet. The timing harness is scratch and is not committed.

## Tasks

- [x] **1. `ConcurrentHistory`** (`src/XrplFile.Core/ConcurrentHistory.cs`): segments, lanes, hold, ordered delivery, failure order, teardown, load halving (H2–H9). Unit tests in `tests/XrplFile.Core.Tests/ConcurrentHistoryTests.cs` over a scripted paging source: equivalence for six history shapes and 1/2/4/8 lanes, hold bound with a slow consumer and with ranges answered last-first, speed with a page delay, failure order, early stop and cancellation, load and failure halving, one lane as one walk, the span rule.
- [x] **2. Session wiring**: `SessionOptions.HistoryLanes` (default 4, at least 1); `PublicationSession` owns one `ConcurrentHistory`; `StreamReconcileAsync` and `ScanForManifestAsync` read through it.
- [x] **3. Test doubles**: `FakeLedgerSource.Passes`, `HistoryReadFailsAfter`, `HistoryHideReadsFrom`; resume tests assert reads, not calls; the stand test's `RecordingSource` measures a whole read.
- [x] **4. Builder check of the spec** and its rulings (section 6): over-quota lanes and split pieces, a read at one lane is one walk, the testnet gate may tune constants.
- [x] **5. Cold review, two passes, two models** (Codex out on quota): hold bound by total (regression, fixed), fake call order (fixed), empty-stretch growth ×8 (fixed), per-segment cancellation sources released with their walk (fixed); two findings rejected with reasons.
- [x] **6. Testnet timing**, first round: 1.33× on the full 772,116-record history and 1.47× on a 64,135-record range. A probe showed the node answers four concurrent pages on one connection in the time of one, so the limit was ours: dense ledgers of up to about 2,000 records that cannot be split, and lanes that cut far ahead filled the hold.
- [x] **7. Scheduler after measurement**: a free lane takes the far part of the segment nearest the consumer (H5, density from its first record), cuts the frontier only while the hold has room (H6), `HeldRecords` 8,192 for the segments ahead and `HeadRecords` 1,024 for the one the consumer reads (H7).
- [x] **8. Verification of task 7**: unit suites (Core, App.Core), stand integration run, Slow (S27, ResumeMemory) with unchanged bounds, three alternating testnet runs and one full-history read; report `concurrent.md`. The 64,135-record range reads 2.3 times as fast; the 772,116-record history drew load warnings and backed off to one lane, 1.34 times.
- [x] **9. Review of task 7**, the scheduler rewrite after the cold review's two passes: opus against the spec, no findings.

## Open

- Lanes never rise again in a session after a load warning (H9). Measured 2026-10-28 (report, "When the warnings come"): testnet let four lanes read 175 s (164,522 records), then warned once a second for the rest of the read even at one lane, so recovering lanes after a quiet period would not help and is not planned.
- The Slow tests' headroom is smaller than before (S27 peak(800) 233 MB, ResumeMemory 165 MB, both bounded at 300 MB).
