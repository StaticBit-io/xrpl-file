# Batch publishing — design

A ticketed publication is slow on a public node and asks for a large deposit. A 7,123,692-byte
file on testnet needed 51.2 XRP up front, of which 50 XRP was the reserve of 250 live tickets.
It wrote 530 of 7,124 chunks in about seven minutes, sat for three minutes on a batch that was
already in the ledger, and stopped when the node stopped answering. The spike of 2026-10-13
(`docs/superpowers/spikes/2026-10-13-batch-publishing.md`) measured the alternative: XLS-56
`Batch` with two accounts wrote 5 MB on testnet in 16–26 s, with a deposit of about 2 XRP.

This document adopts that path as a new submission strategy next to `TicketStrategy`. It also
fixes three things the person meets on either path: progress that moves only once per ticket
batch, no estimate of the time left, and a resume that cannot start from the failed run's screen
and asks for the file again.

**Revised the same day after a review by Codex.** It found that the first draft did not fence
old transactions off from a resume. The draft also read the two accounts' sequences from different
ledgers, trusted sequence movement as proof of identity, counted failed inners as progress, and
gave a worst-case fee that the retry rule did not bound. A second pass found that the manifest
check ran before the fence and matched on the root alone, that attempts were counted per answer
rather than per signed candidate, and that history completeness and the per-session limits were
underspecified. Decisions B5–B13, B16, B20 and B23–B30 and sections 4, 5 and 7 carry the changes.

**Amended 2026-10-21 after the final review of the implementation.** A resume trusted `account_tx`
as complete once the fence had passed. On a node whose history lags the validated ledger by more
than the settle ledgers, a memo that landed near its deadline was missing from the sweep, and the
resume paid for it again or sent a second manifest. Reconciliation now waits for complete history
the way a round's final sweep does (B30); section 5.1 gains `StartSequence` and section 5.2 step 3
carries the rule.

**Amended 2026-10-21 after a throughput measurement on testnet.** A host harness timed every phase
of a 1 MB and a 7 MB run (report `docs/superpowers/reports/2026-10-21-batch-throughput/`). Rounds
waited two more ledgers after their last candidate resolved, about 7 s each; resolution already
proves that nothing the round sent can apply, and the sweep's wait for complete history (B30)
covers a lagging `account_tx`. B9 now settles at the resolving snapshot; the resume fence (B23)
keeps its two ledgers. The window of B10 grew without bound on clean answers, to 105 on a 7 MB run
with 95 in flight against a node limit of about 100; it now stops at 32, the spike's best figure.

**Amended 2026-10-21 for the run screen.** The first progress sweep finds landed memos 5 s or more
into sending, and the screen said "Starting" all that time. B20 now also reports the count found so
far, once, when a call's first transmission is answered; the count stays a count of memos found.

## 1. What is known

From the spike, measured on the `xrpl-file-ci` stand (xrpld 3.3.0) and on testnet (xrpld 3.4.1,
`BatchV1_1` enabled):

- Only the outer `Batch` is applied when submitted. The inners are applied when the ledger closes.
  A batch takes one open-ledger slot whatever it carries, and it is never queued: a full open
  ledger answers `telCAN_NOT_QUEUE`.
- A batch holds 2 to 8 inners. Each inner keeps the 1024-byte memo cap, so chunks stay 1000 bytes.
- The fee is exact: 2 × base + base per inner + base per `BatchSigner`. With one signer that is
  (inners + 3) × base. One drop less is `telINSUF_FEE_P`.
- The submit answer covers the outer only. It is `tesSUCCESS` even when every inner fails later.
- With one account, one batch lands per ledger. With the inners on writer A's `Sequence` and the
  outer on payer B's `Sequence`, batches chain: B's sequence moves in the open ledger, A's is checked
  only at close, in B's order. 1 MB landed in one ledger on the stand.
- With `tfIndependent`, an inner that ends in `tec` consumes A's sequence and only its own chunk is
  missing. An inner that does not consume A's sequence leaves a gap. Every inner after the gap then
  fails at close with no record, while B still pays for each batch.
- A later outer that reached a node before an earlier one got `terPRE_SEQ` and applied after it.
  `terPRE_SEQ` is a retry result, not a promise that the node keeps the transaction. Validators hold
  such a transaction only conditionally, and that was the likely cause of the re-relay tail below.
- While batches are in flight, A must not send a transaction on its own sequence. Cross-account
  order at close is not submission order, and a measured case lost B's inners that way.
- An inner appears in `account_tx(A)` as an ordinary `AccountSet` of A with `Fee` "0", its own hash,
  its result and `ParentBatchID`. Both readers, the .NET `XrplFileReader` and the web reader, read a
  file written this way unchanged. The format of spec 2026-09-05 needs no change.
- A public node closes a WebSocket client when about 100 answers wait to be sent ("client is too
  slow"). Batch answers are large. After such a close, the same signed blobs were resent and
  everything landed. A resend answered `tefPAST_SEQ` when its sequence was already used and
  `terPRE_SEQ` when it was ahead. The first does not prove that this blob applied, only that the
  sequence is spent.
- Accepted batches on testnet usually landed one ledger after acceptance. In one run of 14, 32
  batches trickled in over 148 s, at rippled's 30-second re-relay.
- `account_tx(A)` can lag A's validated `Sequence` by a moment.
- Signatures made with the `Xrpl` package 11.2.0, the version this repository pins, passed the
  node's checks for single-account and two-account batches.

From `E:\GIT\XRPL\xrpl-video-platform`, which ships the same two-account shape in browser
JavaScript (xrpl.js 5.3.0) and measured 74–87 chunks/s on testnet:

- Every batch carries `LastLedgerSequence`, 20 ledgers past the validated ledger. A round ends
  when the validated ledger has passed the last deadline plus 2. Only then does it sweep
  `account_tx(A)` and send what is missing in a new round with fresh sequences.
- A chain check reads both validated sequences every 2 s. It halts the round when A is behind
  what B's applied batches imply, or when the first unapplied batch's deadline has passed.
- Sends go through a window of 16. Three "busy" answers in a row halve it, and ten clean sends
  widen it by one. Sends never run more than 256 batches past B's validated sequence.
- A trailing 8 + 1 is regrouped as 7 + 2. A round with a single memo sends it as a plain
  `AccountSet` of A.
- That platform uses `tfAllOrNothing`, has no amendment check and no reconnect.

From this repository, read on 2026-10-15:

- `ISubmissionStrategy` is the seam: `Account`, `FeeDropsPerTransaction`, `TicketMints`,
  `SubmitAllAsync(memos, IProgress<int>, ct)` and `ReleaseLeftoverTicketsAsync`.
  - `SubmitAllAsync` returns when every memo is in a validated ledger and its hash is known.
  - `XrplFileWriter` calls it once per pass: chunks, block lists, manifest.
- `PublishPolicy` in the application picks `SequenceStrategy` below 100 memos and `TicketStrategy`
  from there. `PublicationSession` receives the strategy already built and special-cases
  `TicketStrategy` in two places.
- `XrplWallet.Sign` in the SDK routes a `Batch` whose `BatchSigners` names the wallet's account to
  `SignAsBatchPart`. That builds the BatchV1_1 preimage, which includes the outer's `Account` and
  `Sequence`. `VaultSigner.SignAsync` signs with `XrplWallet.Sign`, so it can very likely produce
  A's part. Section 4.2 makes this the first thing proven.
- `ILedgerSink.SubmitAsync(tx, signer, ct)` signs and submits in one call and retries internally.
  It cannot resend a kept blob. Nothing in the library reconnects a dropped WebSocket.
- `LedgerTransaction` has no `Sequence`, `Fee`, `TransactionType` or `ParentBatchID`.
  `ILedgerSink.SequenceAsync` cannot pin a ledger.
- `FeesAsync` derives the fee from `server_state`. It does not read the `fee` method's
  `open_ledger_fee`.
- `PublicationState` is version 2. `Load` accepts version 2 only and ignores unknown properties.
  It carries one account, one key reference and no source path. `ChunkHashes` is written only at
  the end.
- `PublicationSession.ReconcileAsync` reconciles chunks and block lists, not a manifest. A run
  that dies after its manifest validated, but before it recorded "done", publishes the manifest
  again on resume.
- Nothing reads the network's amendments today.
- The ticket path reports progress once per ticket batch, after that batch's sweep.

## 2. Scope

**In.**
- A `BatchStrategy` in `XrplFile.Core`, with the transport, ledger and fee data it needs.
- An estimate and a spending bound for it.
- A check that `BatchV1_1` is enabled.
- A payer in the application's publish flow, its state file and its resume, with old transactions
  fenced off from a resume.
- Manifest reconciliation on every path.
- Progress as memos land, with an estimate of the time left, on both paths.
- Resume from the failed run's screen, and resume without picking the file again.

**Out.** Removing the ticket path. Mainnet measurements. A payer that is not a key in the vault,
such as a Xaman or WalletConnect signer. Chunks larger than 1000 bytes. A chunk memo on the outer.
The cause of the ticket path's three-minute pause and dropped connection on testnet: that is its
own investigation, and this design does not depend on it.

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| B1 | Two accounts: the chunks are inners of writer A on A's `Sequence`; the outer is signed and paid by payer B on B's `Sequence`, with A as its only `BatchSigner` | The only shape the spike found both fast and safe. Inners on sequences of one account give one batch per ledger; outers on tickets lose chunks when batches reorder |
| B2 | `tfIndependent`, not `tfAllOrNothing` | An inner that ends in `tec` then costs one chunk and keeps A's sequence whole. With all-or-nothing, the same failure stops A's sequence, and every batch behind it is paid for and writes nothing |
| B3 | The payer is any key in the vault on the writer's network, chosen per publication | The person decided this. A long-lived funded account can pay for many publications while each writer stays a throwaway that needs only its base reserve. The payer appears nowhere in the file format, so it can change between a run and its resume, behind the fence of B23 |
| B4 | The ticket path stays. A publication uses `BatchStrategy` when a payer is chosen and the network has `BatchV1_1` enabled. With no payer, the existing policy applies: `SequenceStrategy` below 100 memos, `TicketStrategy` from there. A run resumes on the strategy it recorded (B16) | The person decided this. A network without the amendment, such as a private stand on an older build, must still be able to publish |
| B5 | Batches go out in rounds. A round assigns sequences from one pinned snapshot (B6), sends, settles (B9), then sweeps. The memos still missing go into the next round with fresh sequences | Once a round has settled, nothing it sent can land, so the next round cannot collide with a straggler |
| B6 | The strategy's view of the chain is a snapshot: one validated ledger index L, read first, then `account_info` of A and of B at `ledger_index` L. Every comparison of the two sequences uses one snapshot. A snapshot whose ledger cannot be read at L fails and is taken again; it never falls back to "validated" | Two separate "validated" reads can straddle a close. A at L and B at L + 1 looks like divergence, and the reverse looks like foreign activity |
| B7 | Every transmission carries `LastLedgerSequence` = I + 20. I is the index of a snapshot no more than 10 s old when the transaction is signed. That applies to batches, solo memos and tidy-up spends. The deadline is registered with the round, and persisted (B23), before the blob is first transmitted | 20 ledgers is about 70 s, which covers two 30-second re-relay periods. A deadline registered before transmission means nothing can be in flight that the round does not know will expire |
| B8 | A chain check takes a snapshot every 2 s while a round sends. **Stalled**: snapshot L is past the deadline of the first batch whose outer B has not consumed, if that batch has been signed. **Diverged**: A's sequence differs from what the outers B consumed imply. Both halt the round: no new batch is signed, and the round settles | A lost outer holds every later outer until its deadline, at no cost, but waiting for that is pointless. A gap in A's sequence makes every later inner fail while B pays. B's sequence can also move for reasons that are not ours (B12); the check does not try to tell them apart. It only stops spending, and identity is decided at settlement |
| B9 | A round settles when signing has stopped, every worker has either registered its candidate or discarded it unsent, and every registered candidate is resolved: either snapshot L has passed its deadline, or B's sequence (for an outer) or A's (for a solo) has passed its sequence. It then sweeps at once (`SettleLedgers` = 0; 2 until the 2026-10-21 amendment) | A transmission whose sequence is consumed can never apply again, so a round that landed everything settles in a ledger or two instead of waiting out 70 s. Further ledgers prove nothing more: the snapshot is validated, and B30 waits for history that lags. One that did not waits only for what is still live. Sequence consumption proves only that a candidate can no longer apply, not that its memo succeeded; the sweep decides that. A tidy-up spend rides a ticket, so it resolves only by its deadline or by its ticket disappearing |
| B10 | Sends go through a window that starts at 16, halves after 3 busy answers in a row, and grows by 1 after 10 clean sends, up to 32. The send allowance is never more than 256 batches past B's sequence in the latest snapshot, and it is raised only by a snapshot that passed the chain check. "Busy" is a `tel` answer, a `tooBusy` or `slowDown` error, or a send that got no answer | A public node's limit is the client's own send queue and rate. Raising the allowance only on a checked snapshot limits what one divergence can cost to 256 batches: 256 × 11 × unit, which is 0.042 XRP at a unit of 15 drops |
| B11 | Memos go 8 to a batch. A trailing group of 1 borrows one memo from the group before it, so 8 + 1 becomes 7 + 2. A round with exactly one memo sends it as a plain `AccountSet` of A at the round's unit, under B7, and only after every batch of the round is resolved | A batch needs 2 to 8 inners. A's own transaction must never share a ledger window with batches in flight (section 1) |
| B12 | Both accounts must be used by nothing else during a run. At settlement the strategy reads `account_tx` of A and of B over the round's ledgers. A transaction of B that is not one of the round's outers, or a transaction of A that is neither an inner of those outers (its `ParentBatchID`) nor the round's solo memo, fails the run with `AccountInUse`, naming the account | Sequence arithmetic cannot prove identity. A foreign transaction can consume the sequence of one inner while the rest catch A up exactly. A `TicketCreate` on B moves its sequence by more than one. Only transaction identities show what really applied |
| B13 | Each memo may be put into at most 3 **candidates** per session. A candidate is a distinct signed transaction carrying the memo: a batch or a solo with its own sequence and deadline. A candidate is reserved when it is registered (B7), before its first transmission, and it counts whatever its outcome. Resending the same blob is a **transport retry** of that candidate, up to 4 transmissions, and is not a new candidate. A memo that would need a fourth candidate fails the run with `SubmitRefused` | A candidate is what can charge a fee, however often its bytes are sent; a `tec` outer charges and writes nothing, so it must count. Counting answers instead would either fail a run after a lost connection or let fee-charging failures go uncounted |
| B14 | `BatchV1_1` is enabled when its id, the SHA-512Half of the name `BatchV1_1`, appears in the `Amendments` field of the ledger's singleton `Amendments` object. That object is read through `ledger_entry` by its fixed index `7DB0788C020F02780A673DC74757F23823FA3014C1866E72CC4CD8B226CD6EF4`. The screen's read is cached per network for 5 minutes. The estimate reads again before every run, and its answer is the one that decides | `feature` is restricted on public nodes, and `ledger_entry` is public. The amendment id is searched for inside the object, not used as the object's index. A cached "off" would otherwise lock the screen for the whole session |
| B15 | The fee unit of a round is ceil(`open_ledger_fee` × 1.5) from the `fee` method, at least the base fee. It may not exceed the ceiling the estimate approved: ceil(the estimate's `open_ledger_fee` × 1.5) × 2. A round whose unit would exceed the ceiling waits one minute for fees to fall. If they have not, the run fails with `FeeTooHigh`, a new error, and can be resumed later. A batch pays (inners + 3) × unit; a solo pays one unit. The estimate refuses up front when `open_ledger_fee` is above 200 drops | The exact Batch formula, with headroom for escalation. A fixed ceiling makes the approved worst case a true bound (B26). A fee rise past it is an outcome the person is told about, not a silent loop of refused blobs |
| B16 | Batch runs write state version 3. Ticket and sequence runs keep writing version 2 with three new optional fields: `Strategy` (`"sequence"` or `"tickets"`), `SourcePath` and `ManifestHash`. Version 3 requires `Strategy` = `"batch"`, `Payer`, `PayerKeyReference` and `Fence`, and allows `SourcePath` and `ManifestHash`. The new application reads 2 and 3 and resumes a run on its recorded strategy; only a version-2 file written before this change, which has no `Strategy`, resumes on the existing policy | An older application refuses version 3 and lists it as unreadable. It cannot resume a Batch run as an ordinary one-account run and erase its payer on save. Ignoring `SourcePath` in version 2 does no harm |
| B17 | Resume reads the bytes once from `SourcePath`, computes their size and Merkle root, and uses those same bytes for the run. A missing, unreadable or changed file falls back to the picker with the reason. A picked file passes the same check against the state | The person picked the file once. Asking again is what this design answers. Hashing and publishing the same in-memory bytes leaves no window for the file to change in between |
| B18 | A Batch run resumes as a Batch run. If its payer key has left the vault, the person chooses another payer on the same network, and the choice is written to the state file. That happens only after the fence of B23 has passed | The payer is outside the format (B3). The fence is what makes changing it safe: no outer of the old payer can still apply |
| B19 | The failed-run screen offers Resume when the run is listed as unfinished, and it takes the same path as the unfinished-runs list | The person was on that screen when it failed. The list already knows how to resume; the screen only lacked the button |
| B20 | Progress counts memos that a sweep found landed with `tesSUCCESS`, never sequence movement. On the Batch path an incremental sweep reads `account_tx(A)` forward from its last position every 5 s while a round runs. On the ticket path, every poll of `SweepAsync` reports what it has found so far. The count never decreases, because it only ever counts memos found successful. The first answered transmission of a call reports the count found so far once, usually zero, so a pass that is sending is told from one not started | A `tec` inner consumes a sequence and writes nothing, so sequence movement overstates progress. An incremental sweep costs one `account_tx` page every 5 s |
| B21 | Time left = memos remaining ÷ the rate of confirmed memos over the last 30 s, plus one settle per pass still to come. Before 30 s of data, the Batch path assumes 70 memos/s, the platform's conservative figure, and the ticket path shows "estimating…". The run screen also shows the elapsed time and the rate | A rate measured on this run, on this node, beats any constant. A prior is used only where a measured one exists |
| B22 | Every new `XrplFileError` value gets its sentence in `PublishErrorMessages`, and the existing exhaustiveness test keeps it that way | The application must never show an error it cannot explain |
| B23 | **The fence.** A version-3 state file records `Fence`: the highest deadline registered by any transmission of the run. It is written before that transmission is first sent; a save that fails stops the transmission and fails the run, so no blob is ever in flight past the recorded fence. A run that ends for any reason, whether failure, cancellation, a closed window or a crash, leaves its fence behind. Resume waits until a snapshot is past `Fence` + `SettleLedgers` before it reads anything for reconciliation, changes the payer, or assigns a sequence. The screen shows "Waiting for earlier transactions to expire", with the time left | Without it, an outer of the interrupted run can apply after the resume began. It can consume sequences the resume assigned, or charge an old payer while a new one pays for inners that fail. A persisted fence turns "nothing old can land" from a hope into a check |
| B24 | `ILedgerSink` gains `SubmitBlobAsync(txBlob, ct)`: one transmission of a signed blob, no internal retry, no lookup. A transport failure surfaces as `NodeUnreachable`. The connection under the sink and the source reconnects on the next call after a transport failure. `BatchStrategy` owns every retry and resend | The strategy has to resend the same bytes after a drop (section 1), and only one layer may own retries. Today's `SubmitAsync` re-signs and retries on its own |
| B25 | `LedgerTransaction` gains `TransactionType`, `Sequence`, `TicketSequence`, `Fee` and `ParentBatchID`. `ILedgerSource` gains `SnapshotAsync(accounts, ct)`, which returns L and each account's sequence at L (B6), `OpenLedgerFeeAsync(ct)` from the `fee` method, and `IsAmendmentEnabledAsync(name, ct)` (B14) | Settlement attributes transactions by identity (B12). The report sums the payer's fees (section 4.6). The fee unit needs `open_ledger_fee` (B15). None of this is readable today |
| B26 | The estimate's worst case is a true bound for one session under B13 and B15; a resume is a new session whose estimate prices the memos still missing and is checked again. **Payer**: every memo sent 3 times, in batches of 2, so at most 2.5 units per memo per attempt, at the fee ceiling. **Writer**: every memo sent 3 times as a solo, plus a tidy-up of up to 250 tickets, at the fee ceiling. The strategy also counts committed fees as it signs, and refuses to sign past either account's budget with `BudgetExceeded` | A bound that the algorithm cannot exceed is the one the person approves. Both are pessimistic, but small. For 7 MB at a 10-drop open-ledger fee, the unit is 15 drops and the ceiling 30: about 1.6 XRP for the payer and 0.65 XRP for the writer, against 50 XRP of ticket reserve today. The runtime count is the safety net if an assumption breaks |
| B27 | Every resume, on every path, looks for this run's manifest **after** the fence (B23) and after reconciliation. When every chunk and list has landed, it builds the manifest the writer would send from the reconciled hashes and ledgers, and looks for a validated `tesSUCCESS` transaction of the writer after `StartedInLedger` whose manifest memo has exactly those bytes. Only that one counts: its hash goes to `ManifestHash`, the state moves to "done", and nothing is sent. A manifest with the same root but other bytes, such as another publication of the same content, is not adopted | A run can die between its manifest validating and its state recording "done". Resuming it today pays for a second manifest and gives a second link. Checking before the fence would miss a manifest still in flight; matching only the root would adopt a different publication's link |
| B28 | `tefPAST_SEQ` and `tefALREADY` mean "this sequence is used", not "this blob applied". `terPRE_SEQ` means "not yet", not "held". Only a sweep that finds the memo, matched on marker and data with `tesSUCCESS`, confirms it | The answers are about sequences. Treating them as confirmation would count a chunk that another transaction displaced |
| B29 | The report's payer fee is the sum of the `Fee` of the outers this session signed that settlement found applied, matched by hash. It covers this session only, and says so | Summing all of B's fees over the session's ledgers would charge this run for anything else B did |
| B30 | A round's final sweep is complete for an account when its records cover the account's sequence interval for the round: from its sequence in the round's first snapshot up to its sequence in the settling snapshot. A record with a non-zero `Sequence` s covers s, and a `TicketCreate` at s also covers the sequences its tickets took. A record with `Sequence` 0 (a ticket) covers nothing but is still attributed under B12. Records of any result count. Both A and B must be complete. The sweep retries for up to 30 s, then fails with `NodeLacksHistory` | `account_tx` lags the sequence a little (section 1). Counting by sequence interval rather than by number of records survives `TicketCreate` and tickets. Requiring B's history too means a foreign payer transaction cannot slip past B12 while B's index catches up |

## 4. The library

### 4.1 `BatchStrategy`

```csharp
public sealed record BatchStrategyOptions
{
    public int InitialWindow { get; init; } = 16;
    public int MaxWindow { get; init; } = 32;
    public int MaxAhead { get; init; } = 256;
    public int DeadlineLedgers { get; init; } = 20;
    public int SettleLedgers { get; init; } = 0;
    public TimeSpan ChainCheck { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ProgressSweep { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan SnapshotMaxAge { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HistoryLagLimit { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan FeeWait { get; init; } = TimeSpan.FromMinutes(1);
    public int MaxAttemptsPerMemo { get; init; } = 3;
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

public sealed class BatchStrategy(
    ILedgerSink sink, ILedgerSource source,
    ITransactionSigner writer, ITransactionSigner payer,
    BatchBudget budget,                 // fee ceiling, payer and writer budgets, from the estimate
    IBatchFenceRecorder fence,          // persists Fence before a first transmission (B23)
    BatchStrategyOptions? options = null) : ISubmissionStrategy
```

- `Account` is the writer's account. `TicketMints` is 0.
- `FeeDropsPerTransaction` is the estimate's fee unit, so the session's `FeeMismatch` check keeps
  its meaning: the estimate and the strategy agree on one approved unit. A round's own unit
  follows B15.
- The constructor refuses a payer equal to the writer.
- `IBatchFenceRecorder` is implemented by `PublicationSession`. It writes `Fence` to the state
  file and returns once the file is saved.

**One round:**

1. Take a snapshot of A and B (B6). Read the round's unit (B15) and check it against the ceiling.
2. Group the round's memos (B11). Batch k gets outer sequence B₀ + k. Its inners take A's sequences
   in order, starting at A₀ plus the inners of every batch before it. A single memo is set aside as
   the round's solo memo.
3. **Send.** A batch is built and signed (4.2) only when the window (B10) and the budget (B26)
   allow it. It is then registered under the round's lock: its deadline is persisted (B7, B23),
   and its memos' candidates are reserved (B13). Only a registered candidate is transmitted, and
   the blob is kept until the round settles. The rules for submit answers are in the table below.
4. **Chain check** every 2 s while sending (B8). A halt is set under the same lock, so after it no
   candidate can register: a worker holding a signed but unregistered blob discards it unsent.
   Settlement starts only once every worker has returned (B9).
5. **Progress sweep** every 5 s (B20).
6. **Settle** (B9).
7. **Solo memo**, if any: snapshot, sign at the round's unit with a fresh deadline, register,
   persist, transmit. Then settle it the same way.
8. **Final sweep** of `account_tx(A)` and `account_tx(B)` over the round's ledgers:
   - Record each memo found with `tesSUCCESS` (B28, B30).
   - Apply B12.
   - Return every memo still missing to the next round. Its candidates were already counted when
     they were registered (B13).

**Submit answers:**

| Answer | Meaning | Action |
|---|---|---|
| `tesSUCCESS`, `terQUEUED`, `terPRE_SEQ` | accepted, or not yet | settlement decides |
| `tefPAST_SEQ`, `tefALREADY` | the sequence is used (B28) | settlement decides |
| `tec` on the outer | applied: B's sequence and the fee are consumed, the inners are not | settlement decides; the chain check sees it as divergence |
| other `tef` on the outer | will not apply as sent | left to the chain check, which stalls the round at the outer's deadline |
| `tem*` | malformed | fail the run with `SubmitRefused`, quoting the answer: it is a bug, not a condition |
| `telINSUF_FEE_P` | the unit is below what the open ledger asks | busy; the next round reads the unit again (B15) |
| other `tel*`, `tooBusy`, `slowDown`, no answer | busy (B10) | transport retry of the same blob after 400 ms × (retry + 1), up to 4 transmissions in all, then leave it to its deadline |
| `NodeUnreachable` from `SubmitBlobAsync` | the connection dropped | the next call reconnects (B24); a transport retry of every registered blob that has no answer yet, within its 4 transmissions |

Every registered candidate counts against its memos' limit (B13), whatever the answer. A
candidate that never applies has still used a slot, which keeps the bound simple and
conservative.

**Tidy-up.** `ReleaseLeftoverTicketsAsync` spends this library's leftover tickets on A with plain
`AccountSet`s of A. It runs only after the last round has settled, under B7. The behaviour and
bounds are those of spec 2026-10-12 (I6–I8). An account that switches from tickets to batches is
tidied up too.

### 4.2 Signing

The outer is a `Dictionary<string, object>`, like every other transaction Core builds:

```
TransactionType Batch, Account B, Sequence B₀+k, Fee (n+3)×unit, Flags tfIndependent,
LastLedgerSequence, SigningPubKey B's key,
RawTransactions [ { RawTransaction: AccountSet of A, Sequence, Fee "0", SigningPubKey "",
                    Flags tfInnerBatchTxn, Memos [one memo] } × n ],
BatchSigners [ { BatchSigner: { Account A } } ]
```

A signs first, then B. These steps are proven on the stand before anything else is built:

1. `writer.SignAsync(outer)` returns a blob whose decoded `BatchSigners[0]` carries A's
   `SigningPubKey` and `TxnSignature`.
2. Merging that part into the outer and calling `payer.SignAsync(outer)` gives a blob that the
   stand accepts through `SubmitBlobAsync`. Its inners land as `AccountSet`s of A.
3. All of this works through two `VaultSigner`s on one vault.

If step 1 does not hold for `VaultSigner` as written, `ITransactionSigner` gains one method,
`SignBatchPartAsync(outer, ct)`, implemented by `VaultSigner` and `WalletSigner` over the SDK's
`SignAsBatchPart`. If 11.2.0's signatures are refused, `Xrpl` moves to the current release in
`Directory.Packages.props`, in a separate commit that runs the whole suite.

Each signature decrypts a seed today: about 1,800 for 7 MB. The 1 MB run on the stand measures
this. If signing takes more than a fifth of the run's time, `VaultSigner` gets a wallet cache for
the life of one session, cleared by the vault's lock and by the session's end.

### 4.3 The amendment

`IsAmendmentEnabledAsync(name, ct)` works as follows:

- It reads `ledger_entry` with `index` = the `Amendments` singleton's index (B14), at the
  validated ledger.
- It returns whether SHA-512Half(name) is in the object's `Amendments` array.
- An answer of `entryNotFound` means that no amendment is enabled, so the result is `false`.
- Any other failure is `NodeUnreachable`.

### 4.4 Progress

`IProgress<int>` keeps its contract: the number of the call's memos known to be in the ledger
with `tesSUCCESS`.

- **Batch path:** each progress sweep and the final sweep report it (B20).
- **Ticket path:** every poll of `SweepAsync` reports it.
- **Both:** the count only grows.

### 4.5 The estimate

```csharp
public sealed record BatchPublicationEstimate(
    long Chunks, int Blocks, int Lists, long Memos,
    int Batches, int SoloTransactions,
    long OpenLedgerFeeDrops, long FeeUnitDrops, long FeeCeilingDrops,
    long PayerFeeDrops,            // Σ (inners + 3) × unit over the batches, nothing retried
    long WriterFeeDrops,           // solo transactions × unit, nothing retried
    long PayerFeeDropsWorstCase,   // ceil(Memos × 3 × 2.5) × ceiling              (B26)
    long WriterFeeDropsWorstCase,  // (Memos × 3 + 250) × ceiling                  (B26)
    long BaseReserveDrops, long OwnerReserveDrops,
    long WriterBalanceDrops, int WriterOwnerCount,
    long PayerBalanceDrops, int PayerOwnerCount)
{
    public long WriterRequiredDrops => BaseReserveDrops + WriterFeeDropsWorstCase;
    public long PayerRequiredDrops => BaseReserveDrops + PayerFeeDropsWorstCase;
    public long WriterSpendableDrops => WriterBalanceDrops - WriterOwnerCount * OwnerReserveDrops;
    public long PayerSpendableDrops => PayerBalanceDrops - PayerOwnerCount * OwnerReserveDrops;
    public bool WriterCovers => WriterSpendableDrops >= WriterRequiredDrops;
    public bool PayerCovers => PayerSpendableDrops >= PayerRequiredDrops;
}

public static Task<BatchPublicationEstimate> EstimateBatchAsync(
    ILedgerSource source, string writer, string payer, long fileBytes, CancellationToken cancellationToken);
```

- `FeeUnitDrops` = max(base, ceil(`OpenLedgerFeeDrops` × 1.5)). `FeeCeilingDrops` =
  2 × `FeeUnitDrops`.
- Batches and solo transactions are counted over the three passes: chunks, block lists only when
  there is more than one block, and the manifest. They are grouped by B11.
- `PublicationSession` gets a Batch overload of `EstimateAsync`, `StartAsync` and `ResumeAsync`.
  Each takes the payer and uses this estimate.
- The session refuses in this order:
  1. `BatchNotEnabled`.
  2. `FeeTooHigh` when `OpenLedgerFeeDrops` > 200.
  3. `InsufficientBalance` for the writer.
  4. `PayerInsufficientBalance`.
  5. `BudgetExceeded` against the sum of both requirements.
- The session hands the strategy its `BatchBudget`: the ceiling, and each account's worst case.
- The two `strategy is TicketStrategy` sites stay as they are.

### 4.6 The report

`PublicationReport` gains `PayerFeeDrops`, as defined in B29, plus the writer's solo and tidy-up
fees counted as today. When settlement could not attribute the payer's fees, the report says
"unknown". It never prints a guess.

### 4.7 New errors

| Value | When |
|---|---|
| `BatchNotEnabled` | a Batch run on a network whose ledger does not enable `BatchV1_1` |
| `PayerInsufficientBalance` | the payer's spendable balance is below its part |
| `AccountInUse` | a transaction of the writer or the payer that the run did not send applied during a round (B12); the message names the account |
| `FeeTooHigh` | the open-ledger fee is above 200 drops at estimate time, or above the approved ceiling for a minute during a run (B15) |

## 5. State and resume

### 5.1 The state file

| Field | Version | Value |
|---|---|---|
| `Strategy` | 2 (optional), 3 (required) | `"sequence"` or `"tickets"` in version 2; `"batch"` in version 3 |
| `SourcePath` | 2 and 3, optional | the absolute path the file was read from; absent when the caller published bytes it did not read from a file |
| `ManifestHash` | 2 and 3, optional | the manifest's hash once the run is done (B27) |
| `Payer`, `PayerKeyReference` | 3, required | B's classic address and vault reference |
| `Fence` | 3, required | the highest registered deadline (B23); 0 before the first transmission |
| `StartSequence` | 3, optional | the writer's next sequence in `StartedInLedger`, read from the same snapshot (B6); what a resume's coverage check starts from (5.2 step 3) |

- `StartAsync` writes version 3 when it is given a payer, and version 2 otherwise.
- A version-2 file loads as today.
- A version-3 file is unreadable in any of these cases: its `Strategy` is not `"batch"`, its
  `Payer` is not a classic address, or it lacks a required field.
- The application writes History from the state, not from memory. At start-up, a state at "done"
  whose `ManifestHash` is not in History is recorded there, so a crash between the two writes loses
  no link.
- The application of today refuses version 3, which is the point (B16).

### 5.2 Resume

1. **The file.** Read the bytes from `SourcePath` and check them (B17). The picker opens only when
   that fails or there is no path, and its file is checked the same way.
2. **The fence.** For a version-3 run, wait until a snapshot is past `Fence` + 2 (B23). The run
   screen shows the wait.
3. **Reconciliation.** Take a snapshot of A after the fence and sweep `account_tx(A)` from
   `StartedInLedger` to the snapshot's ledger. Behind the fence, nothing the interrupted run sent
   can still apply, so the sweep's answer is final once it is complete. It is complete when A's
   records cover A's sequence interval from `StartSequence` up to A's sequence in that snapshot,
   counted as in B30. Until then the sweep is read again, for up to the 30 s of B30, and the
   resume then fails with `NodeLacksHistory` and stays resumable. The same records serve step 4,
   so the manifest check cannot miss what the reconciliation saw. A state written before
   `StartSequence` existed counts from the lowest sequence among A's records, which still catches
   a history that lags at its end.
4. **Done already?** Look for this run's manifest (B27). If it is found, the run is complete.
5. **The payer.** If `PayerKeyReference` is no longer in the vault, ask for a payer among the keys
   on the run's network. Write the new `Payer` and `PayerKeyReference` to the state file before
   running (B18).
6. **The estimate.** Price the memos still missing and check both accounts again (B26). The
   attempt limits and the budget start fresh for the new session.
7. **The strategy.** The run resumes on its recorded `Strategy`. Only a version-2 file without one
   resumes on the existing policy.

## 6. The application

### 6.1 Publish

- **"Paid by".** Below the key list comes a "Paid by" drop-down. It lists every other key on the
  selected key's network, by label and address. Its last entry is "Nobody — publish with tickets",
  with the muted note "Slower, and holds up to 50 XRP of reserve while publishing."
- **Default.** The payer used last on that network, kept in a small settings file next to
  `node-urls.json`. Failing that, the first other key on that network. Failing that, "Nobody".
- **Batch not available.** When the cached amendment read (B14) says `BatchV1_1` is off, the
  drop-down is fixed on "Nobody", with the note "This network does not support Batch, so
  publishing uses tickets." When the read failed, the drop-down stays usable and the estimate
  decides.
- **Models.** `PublishRequest` and `PublishModel` gain `PayerReference` and `PayerAccount`.
  `PublishPolicy.Create` returns a `BatchStrategy` when the request has a payer. It wires the
  budget and the fence recorder from the session.

### 6.2 Estimate

When the request has a payer, the card shows:

1. The leading rows: chunks; batches; solo transactions; fee unit, in drops.
2. **Payer** (label, address): fees, fees in the worst case, the account reserve, required,
   balance and spendable.
3. **Writer** (label, address): the same.
4. A line naming the method: "Batch, paid by `<label>`."

Amounts are in XRP and the unit in drops, as the screen already does. The shortfall message names
the account that is short: "The payer `<label>` needs X XRP and can spend Y." Without a payer, the
card is today's ticket estimate.

### 6.3 Run

- The progress line reads "Writing the file — 1,240 of 7,124 · 52 chunks/s · about 2 min left ·
  0:24 elapsed". Time left follows B21.
- The rate and the time left live in `RunModel`, on an injectable `TimeProvider`.
- During a resume's fence (B23) the line reads "Waiting for earlier transactions to expire — about
  40 s".
- A failed run that is listed as unfinished shows **Resume** beside Dismiss (B19).

### 6.4 Unfinished runs and History

- An unfinished row shows its method: "Batch, paid by `<label>`" or "Tickets".
- Resume follows 5.2. The file picker no longer opens first.
- `PublicationRecord` gains an optional `Payer`. History shows it when present.

### 6.5 Error sentences

| Error | Sentence |
|---|---|
| `BatchNotEnabled` | "This network does not enable Batch. Choose Nobody under Paid by to publish with tickets." |
| `PayerInsufficientBalance` | "The payer does not hold enough XRP for this publication. Fund it, or choose another payer." |
| `AccountInUse` | "The `<writer/payer>` account sent a transaction of its own during the publication. Is this key in use somewhere else? The run is listed under unfinished runs; resume it once nothing else uses the key." |
| `FeeTooHigh` | "Network fees rose above what this publication was approved for. The run is listed under unfinished runs; resume it when fees are back down." |

## 7. Testing

1. **Signing on the stand, first.** Prove 4.2 steps 1–3 with two `VaultSigner`s on the
   `xrpl-file-ci` stand, which enables `BatchV1_1`. Measure the signing time. If the proof fails,
   apply the fallbacks in 4.2 before anything else.
2. **Transport and data on the stand:**
   - `SubmitBlobAsync` with one transmission;
   - a reconnect after the stand drops the socket;
   - `SnapshotAsync` pinned to one ledger;
   - `OpenLedgerFeeAsync`;
   - `IsAmendmentEnabledAsync`, true for `BatchV1_1` and false for a made-up name;
   - the new `LedgerTransaction` fields on an inner (`ParentBatchID`, `Fee` "0").
3. **Core unit, with a new `FakeBatchLedgerSink`.** It keeps a sequence per account, applies an
   outer at submit and its inners at a scripted close, adds the inners to `FakeLedgerSource` as A's
   `AccountSet`s with `ParentBatchID`, and answers snapshots per ledger.

   Scripts:
   - a `tec` inner, and an inner that does not consume A's sequence (diverged);
   - a `tec` outer, and an outer that never lands (stalled);
   - `terPRE_SEQ` reordering;
   - busy answers and `telINSUF_FEE_P`;
   - a dropped connection with unanswered blobs;
   - a foreign transaction of A, and one of B (including a `TicketCreate`);
   - `account_tx` lagging the snapshot;
   - a fee above the ceiling;
   - a halt while a signed batch is not yet registered, and one registered but not yet
     transmitted;
   - a fence save that fails: nothing is transmitted;
   - a `TicketCreate` and a ticketed transaction of A inside the round's interval (B30).

   Tests:
   - grouping (never 1; 8 + 1 becomes 7 + 2) and sequence assignment from one snapshot;
   - the fee formula and the deadline per transmission, registered and persisted before sending;
   - the window and the checked allowance;
   - early settlement on consumed sequences, and settlement on deadlines;
   - solo memos after settling;
   - candidates per memo, transport retries that are not candidates, a `tec` outer that is one,
     and the `SubmitRefused` at a fourth candidate;
   - the runtime budget;
   - progress from sweeps that never decreases and never counts a `tec`;
   - `AccountInUse` by identity;
   - the estimate's arithmetic and its refusals in order.
4. **Core on the stand:**
   - 1 MB through `BatchStrategy` with two vault signers, then read back by `XrplFileReader`;
   - a run whose connection is closed in the middle;
   - a run stopped while batches are in flight, then resumed with a different payer: the resume
     waits for the fence, and no chunk is paid twice by the new payer;
   - a run stopped after its manifest validated: the resume finds the manifest and sends nothing
     (B27), on the ticket path as well;
   - a run stopped while its manifest was in flight: the resume waits for the fence, then finds
     the manifest;
   - a second publication of the same bytes by the same writer: its manifest is not adopted.
5. **App.Core:**
   - "Paid by": its defaults, its memory, its lock when the amendment is off, and its refresh
     after 5 minutes;
   - `PublishPolicy` picking `BatchStrategy`;
   - the two-account estimate and its shortfall sentences;
   - resume from `SourcePath`, and the picker fallback on a moved, unreadable or changed file;
   - resume with the payer key gone;
   - the fence wait on the run screen;
   - Resume from the failed-run screen;
   - time left with a fake clock: the prior, the measured rate, and "estimating…" on tickets;
   - state versions 2 and 3, loaded and refused as 5.1 says, and a resume on the recorded
     strategy;
   - a "done" state missing from History is recorded at start-up;
   - the new error sentences (the exhaustiveness test).
6. **Acceptance, by hand, in the MauiLab VM against testnet:**
   - the same 7,123,692-byte file, with writer and payer from the vault;
   - wall time recorded;
   - read back by the application and by the web reader;
   - one run interrupted by closing the window, then resumed from Unfinished runs without picking
     the file.

## 8. What this does not do

- It does not make the ticket path faster or fix its dropped connection on testnet. That
  investigation stays open, and its findings go to their own spec. The ticket path gains only
  progress per poll (B20), the manifest check (B27) and the resume changes.
- It does not let a payer outside the vault, such as a hardware or mobile wallet, pay. Any signer
  that can sign an outer would do, but the vault is the only signer the application has.
- It does not move B's funds back to the writer or delete B. B is the person's own key, and the
  Accounts tab already closes accounts.
- It does not measure mainnet. The fee ceiling of B15 and the window of B10 are the guard rails
  until it is measured.
- It does not stop the person from using the writer or the payer elsewhere during a run. It
  detects it (B12) and stops.
