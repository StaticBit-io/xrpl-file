# Spike: Batch (XLS-56) for publishing

Date: 2026-10-13
Status: spike finished; no product code changed
Question: can `Batch` make publishing meaningfully faster or cheaper than `TicketStrategy`?

## Verdict

Adopt, in the two-account shape only, as a new `ISubmissionStrategy` next to `TicketStrategy`.
Make the decision final after one run on testnet.

- **Two accounts.** Chunks are inners of writer A on A's sequence. The outer `Batch` is
  signed by payer B on B's sequence. On the stand, 1 MB landed in **one ledger, 4.1 s**. The
  ticket path took 33–82 s. A needs no tickets, so the deposit before a run drops from about
  51 XRP to about 2 XRP on mainnet. The format and both readers are unchanged.
- **Drop the single-account variants.**
  - Inners on sequences give one batch per ledger.
  - Inners on tickets give no gain over tickets.
  - Outers on tickets are fast, but they lose chunks silently when a later batch reaches a
    ledger before an earlier one.

## Stand and method

| | |
|---|---|
| Node | `xrpl-file-ci` stand, xrpld 3.3.0 standalone, `BatchV1_1` enabled; `ledger_accept` every 4 s |
| Mainnet today | `BatchV1_1` enabled; xrpld 3.4.1; base fee 10 drops; reserve 1 XRP + 0.2 XRP per object; `expected_ledger_size` 1600, about 90–310 transactions per ledger |
| File | 1000 chunks of 1000 bytes (1 MB), `tfIndependent`, base fee 10 drops |
| Harness | throwaway, outside the repository; raw JSON-RPC, signed with Xrpl 11.14.0. Baseline, reads and the 11.2.0 check use `XrplFile.Core` as it is on `main` |

## What the protocol does (rippled source, confirmed on the stand)

- At `submit` only the outer is applied to the open ledger. The inners are applied when the
  ledger closes: `BuildLedger` → `applyTransaction` → `applyBatchTransactions`. In the open
  ledger, a batch takes one slot whatever it carries.
- A `Batch` is never queued. `TxQ::canBeHeld` returns `telCAN_NOT_QUEUE` for it. When the
  open ledger is full, a batch pays the escalated fee or is refused at once; it never gets
  `terQUEUED`.
- Fee: 2 × base, plus base per inner, plus base per `BatchSigner`. This is exact: one drop
  less is `telINSUF_FEE_P`.
- A batch holds 2 to 8 inners. Nine inners is `invalidTransaction`.
- Each inner memo keeps the 1024-byte cap: an inner with 2000 bytes of memo is
  `invalidTransaction`. Chunks inside a batch cannot grow.
- The submit answer covers the outer only. It is `tesSUCCESS` even when every inner fails
  later.

## Q1. One account

The reported limit is confirmed. After an outer at `S`, the open ledger is at `S+1`. A next
batch at `S+9`, where the inners will leave the sequence, gets `terPRE_SEQ`. The node holds
it and applies it one ledger later. A batch at `S+1` is accepted at submit, then dropped at
close with `tefPAST_SEQ`, with no record.

| Shape | 1 MB wall | Chunks per ledger | Fee per chunk | Verdict |
|---|---|---|---|---|
| Outer and inners on sequences, one batch per ledger | about 111 ledgers, about 7.5 min (72 chunks took 32 s) | 9 (8 inners + a chunk memo on the outer) | 11.1 drops | slower than tickets |
| Outer on sequence, inners on tickets (248 per mint) | 40.5 s | 248 every 2 ledgers | 12.55 drops | the same ticket cycle; no gain |
| Outer on a ticket, inners on sequences | 8.3 s with the mint; 4 MB 17.2 s | 1000 in one ledger; 2000 per mint | 12.51 drops | fast; unsafe, see Q4 |

Tickets on the inners do not lift the limit. The run is still bounded by the 250-ticket ceiling
and the mint-then-sweep cycle. Tickets on the outers do lift it: one ticket carries eight
chunks, and inner sequences are checked only at close. The cost is the ordering hazard in Q4.

An outer `Batch` that carries a chunk memo counts as a chunk for both readers. The chunk rule
does not check `TransactionType`. This is a possible ninth chunk per batch. It is not
recommended, because it breaks the "chunks are `AccountSet`" convention of spec 2026-09-05.

## Q2. Two accounts

| Measure | Value |
|---|---|
| 1 MB (125 batches) | all 125 submits `tesSUCCESS`; 1000 chunks in **one ledger**; 4.1 s wall, 2.1 s of it submitting |
| 4 MB (500 batches) | 8.9 s; 2072 + 1928 chunks in two ledgers |
| Limit on the stand | sequential submit with two signatures, about 80 batches/s (about 640 chunks/s), not the protocol |
| Writer A | spent 0 drops; owner count 0; needs only its base reserve |
| Payer B | 13.75 drops per chunk (110 per batch of 8); owner count 0 |

The "about 500 per ledger" claim holds, and on the stand the measured rate is higher. Nothing
in the protocol caps one payer's batches per ledger. The caps are the open ledger's free slots
(mainnet about 1600 − 300, shared with everyone) and the submit rate a public node allows us.
Neither was measured here.

### Format and readers

| Check | Result |
|---|---|
| Inner as `account_tx(A)` returns it | `AccountSet`, `Account` A, `Fee` "0", own hash, `tesSUCCESS`, `ParentBatchID` in the metadata |
| `account_tx(B)` | only the outer `Batch` records; no chunk ever names B |
| Manifest as an inner of A (link = the inner's hash) | `.NET XrplFileReader`: writer A, 1,000,000 bytes, identical; JS reader under Node 24: identical |
| Manifest as a plain `AccountSet` of A (one-account file) | both readers: identical |
| Signing with Xrpl **11.2.0** (the pin on `main`) | single-account and two-account batches pass the node's signature checks |

The format needs no change. The writer is the manifest's `Account`, and every chunk is A's.
The fee payer is outside the format. Spec 2026-09-05 still holds, word for word, for every
transaction a reader looks at.

## Q3. Compared with today

Measured on the same stand, the same day, with the real `TicketStrategy` from `XrplFile.Core`.

| Path | 1 MB wall | Chunks per ledger | Fee per chunk | Deposit needed (mainnet figures) |
|---|---|---|---|---|
| `TicketStrategy`, product settings (100 per batch, 25 in flight) | 81.5 s | 100 every 2 ledgers | 10.10 drops | 1 + 50 XRP (estimate prices 250 tickets) |
| `TicketStrategy`, ceiling (250, 250) | 33.1 s | 250 every 2 ledgers | 10.04 drops | 1 + 50 XRP |
| Batch, one account, outers on tickets | 8.3 s | 1000 | 12.51 drops | 1 + 25 XRP for 1 MB; 1 + 50 XRP from 2 MB |
| **Batch, two accounts** | **4.1 s** | **1000** | **13.75 drops** | **2 × 1 XRP; no owner objects** |
| Open-ledger slots per MB | tickets 1000; batch 125 | | | |

- Speed: 8–20× faster on the stand. On mainnet the ticket path fills one open-ledger slot
  per chunk. A batch fills one slot per eight chunks, so fee escalation starts eight times
  later.
- Fees: +37% per chunk, which is +0.0037 XRP per MB.
- Reserve: 51 XRP up front becomes 2 XRP. Today the user must fund the throwaway account for
  the ticket peak. The two-account path has no ticket peak.
- Interrupted runs: they leave no tickets behind, so the leftover-ticket tidy-up does not
  exist on this path.

## Q4. Failures

### A failing inner (third of four), one account, inners on tickets

| Mode | `tec` inner (`asfDisableMaster` → `tecNEED_MASTER_KEY`) | Inner on a missing ticket (`tef`, not applied) |
|---|---|---|
| `tfAllOrNothing` | nothing written; no ticket spent | nothing written |
| `tfOnlyOne` | inner 1 only | inner 1 only |
| `tfUntilFailure` | inners 1–2; inner 3 recorded as `tec`, ticket spent; inner 4 skipped | inners 1–2; inner 3 leaves no record |
| `tfIndependent` | inners 1, 2, 4; inner 3 recorded as `tec` | inners 1, 2, 4 |

In every case the submit answer is `tesSUCCESS` and the outer is `tesSUCCESS`, and its whole
fee is charged (60 drops). Only inners that are `tes` or `tec` reach the ledger. A failure
that applies nothing leaves no trace at all.

### A gap in the writer's sequence, two accounts, 5 batches × 8, `tfIndependent`

| Failure in batch 2, inner 3 | Chunks landed | Outers | Payer |
|---|---|---|---|
| `tec` (A's sequence consumed) | 39/40: only the failing chunk is missing | 5 × `tesSUCCESS` | 550 drops |
| an inner that does not consume A's sequence | **10/40**: every inner after the gap fails at close with no record | 5 × `tesSUCCESS` | 550 drops |

### A later batch reaches a ledger before an earlier one

| Shape | Result |
|---|---|
| One account, outers on tickets | batch 2 applied one ledger early: its 8 inners vanished with no record, and its ticket and fee were spent. Batch 1 landed. 8 chunks lost silently |
| Two accounts | batch 2 got `terPRE_SEQ` and was held. It applied right after batch 1 in the same ledger: 16/16 |

On a real network, consensus can defer one transaction of a burst to the next ledger. With
outers on tickets, that alone is a silent loss. With outers on B's sequence, it only delays the
batches behind it. This is why the two-account shape is recommended.

A related trap: in a ledger where batches are in flight, A must not send anything on its own
sequence. Cross-account order at close is not submission order. In a measured case, B's batch
was applied before A's own batch, and B's inners were lost. The manifest therefore goes through B too, or
after every batch has validated.

### Effect on resume and reconciliation

- The sweep over `account_tx(A)` and the chunk rule are unchanged. Duplicates stay harmless.
- A submit answer says nothing about the chunks, so every batch is confirmed by the sweep.
- After a sequence gap, every batch already in flight behind it is lost, and B pays for each.
  The strategy must bound its window, for example to one ledger. Before the next window it
  checks A's validated `Sequence` against the expected value, then rebuilds from A's and B's
  validated sequences.
- An outer that never lands, for example after an expired `LastLedgerSequence`, holds the
  later outers at `terPRE_SEQ` at no cost. Recovery re-signs from B's validated sequence.
- The answer table changes: `telCAN_NOT_QUEUE` replaces `terQUEUED` as the "open ledger is
  full" answer.
- State names two accounts and two keys. Refund and deletion apply to both. A's sequence moves
  about 1000 per MB, as with tickets today; B's moves about 125 per MB.

## Not measured

- Consensus with more than one validator: how often a burst splits across ledgers.
- A public node's submit rate limits for about 125 two-signature submits per MB.
- Mainnet fee escalation with real traffic.

All three are a testnet run: `BatchV1_1` is enabled there and `expected_ledger_size` is 814.
The next step is that run with the two-account shape, 1 MB and 5 MB, through a public node.
After it comes a design document for the account pair: funding, refund, state, estimate, and
the window and gap rules above.

## Addendum: testnet run (2026-10-13)

Status: done. The verdict stands. Adopt the two-account shape, with three rules this run adds:
cap the submits in flight, allow for a 30-second re-relay tail, and resubmit the same blobs
after a dropped connection.

### Setup

| | |
|---|---|
| Network | XRPL testnet, xrpld 3.4.1, 6 proposers, quorum 5, `BatchV1_1` enabled, base fee 10 drops, reserve 1 XRP + 0.2 XRP |
| Node | `wss://s.altnet.rippletest.net:51233` (the built-in testnet endpoint of `Endpoints`), one WebSocket per run, round trip about 420 ms from the test machine |
| Accounts | a fresh writer A and payer B from the testnet faucet for every run, 100 XRP each |
| Shape | as in Q2: chunks are inners of A on A's `Sequence` (`Flags` 0x40000000, `Fee` "0", empty `SigningPubKey`, one `xf/c/<i>` memo of 1000 bytes). The outer `Batch` is B's, `tfIndependent`, A in `BatchSigners`, 110 drops for 8 inners. No `LastLedgerSequence` |
| Window | submits in flight on the one connection: 1 (wait for each answer), 8, 32, or unbounded (send all, then read the answers). Every blob is signed before the first submit |
| Wall clock | first submit until A's validated `Sequence` covers every inner (polled every 0.5 s) |
| Harness | throwaway, outside the repository. Reads use `XrplFileReader` and the web reader from `main` |

### Results

1 MB is 1000 chunks in 125 batches. 5 MB is 5000 chunks in 625 batches.

| Size | Window | Submit phase | Batches/s | Wall to validated | Ledgers with chunks | Most chunks in one ledger | Notes |
|---|---|---|---|---|---|---|---|
| 1 MB | 1 | 53.4 s | 2.3 | 59.0 s | 15 | 72 | bounded by the round trip; every batch in the next ledger |
| 1 MB | 8 | 4.5 s | 27.7 | 11.4 s | 2 | 824 | |
| 1 MB | 8 | 4.4 s | 28.4 | 11.4 s | 3 | 840 | |
| 1 MB | 8 | 7.7 s | 16.2 | 12.7 s | 3 | 640 | |
| 1 MB | unbounded | 1.8 s | 67.9 | about 150 s | 16 | 696 | 93 batches in 2 ledgers; 32 trickled in over 40 ledgers (see below) |
| 1 MB | unbounded | 2.3 s | 55.4 | 8.7 s | 2 | 728 | |
| 1 MB | unbounded | 2.2 s | 56.0 | 10.2 s | 2 | 616 | |
| 1 MB | unbounded | 15.5 s | 8.1 | 22.7 s | 5 | 464 | busy network, about 800 foreign transactions per ledger; answers slowed to 3.6 s |
| 1 MB | unbounded | 2.0 s | 61.1 | 8.8 s | 2 | 608 | |
| 5 MB | 8 | 19.4 s | 32.3 | 26.2 s | 6 | 1096 | |
| 5 MB | 8 | 19.5 s | 32.0 | 23.0 s | 6 | 1024 | |
| 5 MB | 32 | 11.9 s | 52.6 | 18.4 s | 4 | 1816 | |
| 5 MB | 32 | 10.4 s | 60.2 | 16.3 s | 4 | 2168 | |
| 5 MB | unbounded | 10.0 s | 62.3 | 14.3 s | 3 | 3656 | the node closed the WebSocket; 329 submits resent (see below) |

Totals across the 14 runs: 34,000 chunks in 4,250 batches. **Every chunk landed**, every
outer was `tesSUCCESS`, and no inner had any other result. The largest ledger held 4,128
transactions, inners included.

### Fees

| Measure | Value |
|---|---|
| Payer B per chunk | **13.75 drops in every run** (110 per batch of 8): 13,750 drops per MB, 68,750 per 5 MB |
| Writer A | 0 drops; owner count 0 |
| `open_ledger_cost` in the submit answers | 110 in every answer that carried it |
| `open_ledger_fee` | 10 drops before and after every burst; no escalation |
| `expected_ledger_size` | 814 at the start, then 975, 1122, 1195, 1665, 1911, 4953. Our own full, fast ledgers raised it |
| `telCAN_NOT_QUEUE` / `telINSUF_FEE_P` | never seen |

### Submit answers and limits of a public node

| Answer or event | When | Count |
|---|---|---|
| `tesSUCCESS` | the first answer to every submit, except in the run with the dropped connection | 3,625 of 3,625 |
| `"warning": "load"` in the envelope | once several runs had gone back to back; on 0–20 submit answers per run, and on `account_info` and `ledger` too | 84 submit answers in total |
| `slowDown`, `tooBusy` | never | 0 |
| WebSocket closed: `PolicyViolation: Policy error: client is too slow.` | 5 MB with an unbounded window, about 300 submits in flight | 1 |
| The same blobs resent after the close | `tefPAST_SEQ` 162 (applied before the close), `terPRE_SEQ` 171 (held, applied later), the rest `tesSUCCESS` | 329 resends |

- The load warning means only that the client's IP is close to its limit. The node gave it
  across back-to-back runs and never acted on it. No submit was refused or slowed because of
  it.
- The one close came from the WebSocket send queue. rippled closes a client when more than
  `send_queue_limit` answers wait to be sent (`Port.cpp`: default 100; `BaseWSPeer.h`:
  "client is too slow"). Answers to a `Batch` submit are large, because they echo the blob and
  the decoded inners. With 625 submits in flight, the answers piled up. Nothing was lost. The
  identical blob is idempotent, and B's sequence orders the resent batches. All 5000 chunks
  landed in 3 ledgers, in 14.3 s.
- One run met real load from other traffic. The node then took about 3.6 s per answer. The
  burst fell to 8 batches/s and spread over 5 ledgers. Nothing failed.

### How a burst splits across ledgers

On testnet a burst never landed in one ledger. On the stand, 1 MB did.

| Measure | Value |
|---|---|
| Ledger close interval | about 3.5 s |
| Batch lands in the ledger that was open when the node answered | 333 of 3,500 (9.5%) |
| ... in the next ledger | 3,083 (88.1%) |
| ... two ledgers later | 84 (2.4%) |
| Excluded | the tail run and the run with the dropped connection, both below |

The public node puts an accepted batch into its open ledger. The batch then has to reach the
validators before their round closes. Most batches miss the round they were accepted in and
land in the next one. So 1 MB normally takes 2 ledgers. 5 MB takes as many ledgers as the
submit phase spans, plus one.

**The tail (1 run of 14).** All 125 batches were accepted in one open ledger, and 93 landed
within two ledgers. The other 32 came in small groups over 40 ledgers, the last after 148 s.
The groups came about 30 s apart: at 26–35 s, 64–71 s, 96–103 s, 128–131 s and 148 s. This
matches rippled's re-relay. `OpenLedger::accept` relays a transaction that is still in the
node's open ledger ("Relaying recovered tx"), at most once per `HashRouter` `relayTime` of
30 s. So those batches had stayed on the public node and had not reached enough validators.

Why they did not reach the validators is not proven. A likely cause is out-of-order arrival.
A validator holds a `terPRE_SEQ` transaction only once, unless it was submitted locally or its
`LastLedgerSequence` is at most 5 ledgers ahead (`NetworkOPs::apply`). Nothing was lost,
because B's sequence makes the later batches wait. The cost is time.

### Sequence-gap cascade

None. Not one inner failed in 4,250 batches, so A's sequence never had a gap. This includes
the dropped connection, where 329 answers were lost. The cascade from Q4 needs an inner that
does not consume A's sequence. Nothing on testnet produced one.

### Reading back

| File | Batches | .NET `XrplFileReader` | Web reader (Node 24) |
|---|---|---|---|
| 1 MB, the tail run (chunks in 16 of 42 ledgers) | 125 | identical, 3.4 s | identical, 4.2 s |
| 1 MB, busy network (5 ledgers) | 125 | identical, 3.3 s | identical, 4.2 s |
| 1 MB, window 8 | 125 | identical, 3.6 s | identical, 4.5 s |
| 5 MB, window 32 (5 blocks, one `xf/b/0` list, manifest, all inners of A) | 625 + 2 | identical, 24.7 s | identical, 27.7 s |

Links are `xrpl-file://testnet/<manifest inner hash>`. No reader change was needed.

The busy run also showed a lag the sweep must allow for. A's validated `Sequence` already
covered the manifest inner, but `account_tx(A)` did not return it yet. The record appeared
moments later. A confirmation that reads `account_tx` right after the `Sequence` moves must
retry, not conclude that the record is missing.

### What this changes in the design

| Rule | Reason |
|---|---|
| Keep 8–32 submits in flight on one connection, never close to 100 | the WebSocket send queue closes the client at 100 waiting answers. Window 32 gave 5 MB in 16–18 s. Window 1 is bounded by the round trip: 1 MB in 59 s here |
| After a dropped connection, resend the same signed blobs, then confirm by the sweep | a lost answer says nothing. `tefPAST_SEQ` on a resend means applied; `terPRE_SEQ` means held |
| A batch that is not in a ledger after one or two ledgers is not lost yet. Wait at least two re-relay periods (about 60 s) before rebuilding from validated sequences | the 30-second re-relay tail. A rebuild that comes too early re-signs sequences that are still in flight |
| If outers carry `LastLedgerSequence`, leave room for that tail, or accept a re-sign after expiry | an expired outer stops every later outer at `terPRE_SEQ` until recovery. A `LastLedgerSequence` at most 5 ledgers ahead would also let validators hold out-of-order batches again; not measured |
| Treat `"warning": "load"` as information, not as a reason to stop | it came on up to 20 answers per run, with no refusal, no slowdown and no drop |
| Budget 13.75 drops per chunk, plus 50 drops per list or manifest batch | no fee escalation was seen, even in ledgers of 4,000 transactions |

### Still not measured

- Mainnet. Testnet traffic is lighter, but one run here met about 800 foreign transactions
  per ledger.
- The ticket path on testnet. The 8–20× gain over tickets is a stand figure. On testnet, with a
  window of 8 or 32, the two-account path took 11–13 s for 1 MB and 16–26 s for 5 MB.
  Tickets took 33–82 s for 1 MB on the stand.
- Whether a `LastLedgerSequence` close to the current ledger removes the tail.
- Several publications from one IP at the same time.
