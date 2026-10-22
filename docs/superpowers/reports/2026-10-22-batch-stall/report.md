# The 88 s stall at 1,064 chunks

A follow-up of the read-back report (`../2026-10-22-readback-cache/report.md`, "Follow-ups"): the
Batch publication of the 7,123,692-byte `accept-7mb.bin` in the MauiLab VM stood at 1,064 of 7,124
chunks from 0:47 to 2:15, and the run took 3:12 instead of about 93 s. The 95 s stall of the first
acceptance run (`../2026-10-15-batch-publishing/report.md`, at 1,136 chunks) looked the same.

## Cause

One outer of the first round never applied. It was the 134th batch (ordinal 133), on the payer's
sequence 21662712. Every later outer of the round waited behind it on the payer's sequence, until
the chain check saw the ledger pass that outer's deadline (`LastLedgerSequence` = snapshot + 20). The
round then settled, swept, and a second round sent the remaining memos.

### Evidence on the ledger

The run's writer is `r44DFo16gPv4k3ug61QnLxArL3kLzUbPkW` and its payer `rL54SuvLqRrKtvx5izGEwKCBTdr2KPYzHa`
(manifest `F276C1C6…41CA`, ledger 21735316). Their `account_tx`, per ledger:

| Ledger | Payer's outers | Payer sequences | Writer's chunks |
|---|---|---|---|
| 21735262 | 3 | 21662579–581 | 24 |
| 21735263 | 121 | 21662582–702 | 968 |
| 21735264 | 5 | 21662703–707 | 40 |
| 21735271 | 2 | 21662708–709 | 16 |
| 21735273 | 2 | 21662710–711 | 16 |
| 21735274–296 | none | | none |
| 21735297–304 | 764 | 21662712–3469 | 6,060 |

24 + 968 + 40 + 16 + 16 = 1,064 chunks, exactly 133 batches. In the second round, sequence 21662712
goes to a different outer. The ledgers in between carried 1 to 45 transactions each, nearly all from
other accounts, so the network had room. The first outer's deadline was 21735280. The batches signed
around ledger 21735264 had deadlines near 21735284, and the second round landed at 21735297: 20
ledgers of waiting, a settle, the final sweep and a new snapshot.

### Why the outer did not apply

rippled never queues a Batch. `TxQ::canBeHeld` (rippled `develop`, `src/xrpld/app/misc/detail/TxQ.cpp`)
returns `telCAN_NOT_QUEUE` for every `ttBATCH`: "It must apply straight to the open ledger or not
at all". Section 1 of the spec measured the same thing. Once the open ledger is above its expected
size, the escalated fee level is far above our unit, so every further Batch is refused.

The expected size is `txnsExpected_` in `TxQ::FeeMetrics::update`. A consensus round longer than
5 s (`RCLConsensus.cpp`: `processClosedLedger(app_, *built, roundTime > 5s)`) halves it. It is
floored at 32 and never set above half its old value. A ledger of 1,103 transactions (ledger
21735263, 9 MB of memos) is the kind of round that runs slow. After a halving at a quiet time the
open ledger takes about 32 transactions. The node also retries its own refused transactions from
`LocalTxs` through the same TxQ, for at most 5 ledgers. That matches ledgers 21735271 and 21735273:
two batches (18 transactions) each beside about 20 foreign ones.

The strategy reads `telCAN_NOT_QUEUE` as busy. It made four transmissions of the blob in about
2.4 s (400 ms × (retry + 1)) and then left it to its deadline. Nothing sent it again, and the
node's own retries ended after 5 ledgers. Everything behind it then waited for the deadline.

The application wrote no round trace, so the submit answers of that run are not known. The ledger
shows that the outer on 21662712 was never applied in round 1 while later ones were registered and
held. Section 1 and the rippled source show that `telCAN_NOT_QUEUE` is the only answer a Batch gets
from a full open ledger. The four-transmission rule leaves such an outer to its deadline.

### Not reproduced on the host

The host harness (the throughput report's harness, plus snapshot sequences in the span log and a
per-second `fee`/`server_info` sampler) published the same 7 MB file six times on fresh faucet
accounts between 15:05 and 15:21 UTC:

| Run | Run time | Submit answers | Halts |
|---|---|---|---|
| a, b, c | 82.5, 81.9, 80.4 s | 893 × `tesSUCCESS` each | none |
| d, e, f | 83.1, 91.3, 86.8 s | 893 × `tesSUCCESS` each | none |

During runs d–f the node's `expected_ledger_size` stayed between 2,586 and 2,880, no consensus
round took more than 3.5 s, and the queue was empty. With an expected size that large the open ledger
never filled, so the condition of the VM run did not arise. The VM run at 13:09 UTC came after a
quiet spell. The stall can't be produced on demand on testnet; the fake ledger reproduces it
deterministically (below).

## What bounds it, and the fix

Before the fix, one exhausted outer cost one deadline: 20 ledgers (about 70 s on testnet), then a
settle, the final sweep and a new round. That is 88 s here and 95 s in the acceptance run. The spec
already says waiting for it is pointless (B8).

`d6c4612` changes this. While the chain check passes, during sending and during settlement, the
outer the payer's sequence stopped at is sent again if its transmissions are exhausted. So are the
exhausted outers behind it, in order, as many as the window holds. A send is a new series of four
transmissions of the same signed blob, so it stays one candidate, and the fence and the spending
are unchanged. Once the open ledger has room again, the outer lands and the held outers behind it
follow in the same round. A round that halted, or whose snapshot shows divergence, sends nothing
again, because B would pay for inners that can no longer apply. The trace records each send as
`resend:<ordinal>`. Spec B8 and the submit-answer table are amended.

The fix does not make a full open ledger take more. While the network's expected size is small,
the round moves at the pace the network admits, two batches per ledger in the VM run. The deadline
still bounds the round, and the next round re-reads the unit (B15).

## The rate during a stall

During the stall the line read "1.1 chunks/s · about 1 h 35 min left" for about 30 s, then
"estimating…". The 30 s window still reached back to 1,032 chunks, so 32 chunks in 30 s read as
1.1 chunks/s. `67e9cb1` reports a measured rate under a tenth of the best one the stage has shown
as zero, and the line shows that as "estimating…". Replayed with the VM run's timeline (24 chunks
at 6 s, 1,032 at 11 s, 1,048 at 22 s, 1,064 at 36 s), the line reads "35 chunks/s" at 0:40 and
"estimating…" from 0:41. Before the change it read 1.1 and then 0.5 chunks/s, with hours left,
until 1:06. Spec B21 is amended.

## Tests

The new tests were written first and failed on the old code:

- Outers refused `telCAN_NOT_QUEUE` four times, with one candidate per memo, land in a single round
  without a stall. The old code stalled and then failed with `SubmitRefused`.
- With the VM run's timeline, the line reads "estimating…" at 0:41. The old code read "1.1 chunks/s".

One guard test was written after the code: a round that diverged sends nothing again (`e343f48`).
Its first form counted transmissions of the refused sequence. It failed about once in fifteen runs
because a round that diverged before registering that outer leaves the sequence to the next round,
which may send its own outer on it again, correctly. It now looks only at the round that diverged.
With the settlement guard removed, it fails in 14 of 15 runs.

Unit, with the trace log: Core 680, App.Core 684, Keys 251. The application builds in Release with
0 warnings. This session did not run the integration tests on the stand.

With the fix, the harness published the 7 MB file once more on fresh testnet accounts at
15:34 UTC. The run took 82.6 s, with 893 × `tesSUCCESS`, no halt and no `resend`, and the bytes read
back matched. Nothing in it exhausted an outer, so this run shows only that the fix costs nothing on
a clean run. The resend path itself is shown only on the fake ledger.

## The round trace in the application

`26e37fb` gives a stall that comes back a record. When a Batch run ends, however it ended, the
application appends its round trace to `batch-trace.log`. The file is in the application data
directory, beside `publications.json` and `runs\` (on Windows `%APPDATA%\XrplFile\batch-trace.log`).

Each record has:

- A header: the strategy's start time, the state file's name, the network, the writer, the payer and
  the file size.
- Every event, with seconds from the strategy's start: `round:`, `register:`, `transmit:<ordinal>:<answer>`,
  `resend:`, `unsent:`, `discard:`, `halt:`, `settled:`, `fee-wait:`.
- An `end` line: Succeeded, Cancelled, or Failed with the error code and the engine result.

The log is bounded and safe:

- Each file stays under 1 MiB, and the newest three are kept (`batch-trace.log`, `.1.log`, `.2.log`).
  A 7 MB run writes about 50 KB.
- It is written on the run's own task, not the UI thread, and a write that fails is dropped without
  failing the run.
- The trace carries only ordinals, ledger indexes, fee units and engine results. As a second guard,
  a line with a seed-shaped token or hex longer than a transaction hash is withheld. Tests check that
  a run's trace holds neither the writer's nor the payer's seed nor any long hex, and that such a
  line is withheld.
