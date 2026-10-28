# The resume's history read, concurrent

Follow-up to `report.md` in this folder, which found the node's answer to be 98.8% of a resume's `account_tx` read. `ConcurrentHistory` (spec `docs/superpowers/specs/2026-10-27-parallel-history-read-design.md`) reads the range as several ledger segments at once and hands the reconciliation the records of one forward walk, in its order. This report measures it. Testnet was read-only: `account_tx`, `server_info` and `ledger`, nothing signed or submitted; no load warning appeared in any run.

## Environment

| | |
|---|---|
| Host | 11th Gen Intel Core i5-11400F (12 logical cores), 64 GB, Windows 11 Pro |
| Runtime | .NET 10.0.11, Release, workstation concurrent GC, `System.GC.LOHThreshold` = 4,194,304 |
| Testnet | `wss://s.altnet.rippletest.net:51233`, account `r44DFo16gPv4k3ug61QnLxArL3kLzUbPkW` (the report's), one connection from `ClientKeeper.CreateClient` |
| Harness | scratch console, not committed: `ConcurrentHistory` over `XrplClientLedgerSource`, lanes 1 (one call over the whole range, today's walk) against lanes 4, alternating, 10 s apart; records counted and SHA-256 over their hashes in order |

## Results

### 64,135 records, ledgers 21,649,605 to 21,652,605 (final code, three alternating runs, quiet machine)

| Run | One walk, s | records/s | Four lanes, s | records/s | Speed-up | Walks | Most held |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | 131.90 | 486 | 51.62 | 1,242 | 2.56 | 68 | 8,392 |
| 2 | 132.03 | 486 | 56.80 | 1,129 | 2.32 | 50 | 8,392 |
| 3 | 131.53 | 488 | 57.91 | 1,107 | 2.27 | 51 | 8,392 |
| median | 131.90 | 486 | 56.80 | 1,129 | **2.32** | | |

All six reads yielded the same 64,135 records in the same order (SHA-256 prefix `B96E4C5C9F919288`).

### The whole history, 772,116 records, ledgers 21,649,605 to 21,875,158

| Code | Lanes | s | records/s | Speed-up |
|---|---:|---:|---:|---:|
| any (one walk) | 1 | 1,714.28 | 450 | 1 |
| first version (H5 split for idle lanes, hold 4,096 by prefix) | 4 | 1,287.01 | 600 | 1.33 |
| final | 4, then 2, then 1 | 1,279.96 | 603 | 1.34 |

Every read of the whole history yielded the same sequence (SHA-256 prefix `F6428114E96AF586`).

The final read of the whole history drew 825 answers carrying `warning: load`, and the read halved its lanes twice, as spec H9 says, ending at one lane; it read 168 segments. The one walk (29 minutes) and the first version (21 minutes, about three requests a second) drew none, nor did any read of the 64,135-record range (under a minute each). The harness did not record when the warnings came, so how long the node tolerates about five requests a second, and whether one lane after the burst kept drawing them, is not known.

## Verdict

- On a history the size of a resume of about 64,000 chunks, four lanes read 2.3 times as fast as one walk (median of three), with no load warning: the goal is met there.
- On the whole 772,116-record history the public node warned of load and the read backed off to one lane, ending 1.34 times as fast as one walk: the goal is not met for a read that long against this node. The back-off is the politeness the spec asks for; lanes never rise again within a session (H9), so a single burst of warnings costs the rest of the read.

## What the first measurement changed

The first version read 1.33 to 1.47 times as fast as one walk. A probe sent four single-ledger pages of this account at once over one connection: 444 ms against 2,450 ms one after another, five rounds alike. The node serves concurrent requests on one connection well; the limit was the read's own scheduling.

- The history is dense: the first ledgers hold 1,024, 2,048, 1,648 records of the writer, and a ledger is never split between segments, so each lane reads one ledger page by page and three lanes ahead of the consumer need about 6,000 records of room.
- Lanes cut new segments at the frontier, far ahead of the consumer, and filled the hold with records the consumer reached last; with the hold at 4,096, three lanes read faster than four (974 against 681 records/s).
- A segment cut across a long empty stretch landed on every dense ledger after it and was read by one walk; pieces were split off only for idle lanes, and halving what was left handed them the empty far end.

The final scheduler (spec H5 to H7): a free lane takes the far part of the segment nearest the consumer, keeping about 1,000 records with it by its density since its first record; the frontier is cut only while the hold has room; segments ahead of the consumer may hold 8,192 records in all and the one it reads 1,024 of its own.

Hold sizes on the 64,135-record range, four lanes, one run each (before the frontier rule and the head limit): 4,096 gave 1.47 times, 8,192 gave 2.44 times, 16,384 gave 3.19 times. 8,192 is the smallest that meets the goal.

## Memory (Slow tests, bounds unchanged)

| Test | Before this work | Final | Bound |
|---|---:|---:|---:|
| `PublicationMemoryTests` peak(50), MB | 123.9 | 257.0 | — |
| `PublicationMemoryTests` peak(800), MB | 156.3 | 233.3 | < 300 |
| peak(800) − peak(50) | 32.5 raw, 13.0 net | −23.7 raw | < 40 net |
| `ResumeMemoryStandaloneTests` resume peak, MB | 79.0 | 165.4 | < 300 |

Both pass. The headroom is smaller: the hold is up to 9,216 records (about 40 MB at a full chunk memo) plus one page per lane in flight, and four lanes allocate four times as fast, which a workstation collector answers with a larger heap. The figures before this work are `report.md`'s (Task 2's stand figures); a hold of 2 × 8,192 measured peak(800) at 269 MB, which is why the segment the consumer reads now holds at most 1,024.

## Tests

- Unit: `XrplFile.Core.Tests` 905, `XrplFile.App.Core.Tests` 819, all passing; `ConcurrentHistoryTests` 12 times in a row without a failure after the last change.
- Stand (`xrpl-file-ci`, `ws://localhost:16006`): Core 44, App.Core 1, all passing.
- Slow: S27 and `ResumeMemoryStandaloneTests`, passing with the bounds above.

## Review

- Builder check of the spec: four questions, ruled in the spec's section 6; one was the deadlock of a split piece with no lane, which the final scheduler removes (a piece is started by the lane that takes it).
- Cold review, two passes, sonnet and opus (Codex out: its five-hour quota at 96%): four regressions fixed (hold bound by prefix, fake call order, empty-stretch growth, cancellation sources held until the read ended), two findings rejected with reasons (a split segment's read-ahead page; a tail segment past the node's history index, which rippled checks against its validated range, not the index).
- The scheduler rewritten after the measurement, reviewed by opus against the spec: no findings.
