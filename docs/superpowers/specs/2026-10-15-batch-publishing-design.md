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
  missing. An inner that does not consume A's sequence leaves a gap, and every inner after the gap
  fails at close with no record, while B still pays for each batch.
- A later outer that reaches a node before an earlier one gets `terPRE_SEQ` and is held; nothing is
  lost. With outers on tickets instead, the same reordering silently loses chunks.
- While batches are in flight, A must not send a transaction on its own sequence: cross-account
  order at close is not submission order, and a measured case lost B's inners that way.
- An inner appears in `account_tx(A)` as an ordinary `AccountSet` of A with `Fee` "0", its own hash,
  its result and `ParentBatchID`. Both readers, the .NET `XrplFileReader` and the web reader, read a
  file written this way unchanged. The format of spec 2026-09-05 needs no change.
- A public node closes a WebSocket client when about 100 answers wait to be sent ("client is too
  slow"). Batch answers are large. Resending the same signed blobs after the close lost nothing:
  `tefPAST_SEQ` meant applied, `terPRE_SEQ` meant held.
- Accepted batches on testnet usually landed one ledger after acceptance. In one run of 14, 32
  batches trickled in over 148 s, at rippled's 30-second re-relay.
- `account_tx(A)` can lag A's validated `Sequence` by a moment.
- Signatures made with the `Xrpl` package 11.2.0, the version this repository pins, passed the
  node's checks for single-account and two-account batches.

From `E:\GIT\XRPL\xrpl-video-platform`, which ships the same two-account shape in browser
JavaScript (xrpl.js 5.3.0) and measured 74–87 chunks/s on testnet:

- Every batch carries `LastLedgerSequence`, 20 ledgers past the validated ledger. A round of
  batches ends only when the validated ledger has passed the last deadline plus 2 ledgers. Only
  then does it sweep `account_tx(A)` and send what is missing in a new round with fresh
  sequences. After the deadline nothing old can land on top of new sequence numbers.
- A chain check reads both validated sequences every 2 s. It halts the round when A is behind
  what B's applied batches imply, or when the first unapplied batch's deadline has passed.
- Sends go through a window of 16. Three "busy" answers in a row halve it; ten clean sends widen it
  by one. Sends never run more than 256 batches past B's validated sequence.
- A trailing 8 + 1 is regrouped as 7 + 2. A round with a single memo sends it as a plain
  `AccountSet` of A.
- That platform uses `tfAllOrNothing`, has no amendment check and no reconnect.

From this repository, read on 2026-10-15:

- `ISubmissionStrategy` is the seam: `Account`, `FeeDropsPerTransaction`, `TicketMints`,
  `SubmitAllAsync(memos, IProgress<int>, ct)` and `ReleaseLeftoverTicketsAsync`. `XrplFileWriter`
  calls `SubmitAllAsync` once per pass: chunks, block lists, manifest.
- `PublishPolicy` in the application picks `SequenceStrategy` below 100 memos and `TicketStrategy`
  from there. `PublicationSession` receives the strategy already built. It special-cases
  `TicketStrategy` in two places.
- `XrplWallet.Sign` in the SDK routes a `Batch` whose `BatchSigners` names the wallet's account to
  `SignAsBatchPart`. That builds the BatchV1_1 preimage, which includes the outer's `Account` and
  `Sequence`. `VaultSigner.SignAsync` signs with `XrplWallet.Sign`, so it can very likely
  produce A's part with no new interface. Section 4 makes this the first thing proven.
- `PublicationState` is version 2. It carries one account and one key reference and no source
  path. `ChunkHashes` is written only at the end.
- Nothing reads the network's amendments today.
- The ticket path reports progress once per ticket batch, after that batch's sweep.

## 2. Scope

**In.** A `BatchStrategy` in `XrplFile.Core`. An estimate for it. A check that `BatchV1_1` is
enabled. A payer in the application's publish flow, its state file and its resume. Progress as
memos land, with an estimate of the time left, on both paths. Resume from the failed run's screen.
Resume without picking the file again.

**Out.** Removing the ticket path. Mainnet measurements. A payer that is not a key in the vault, such as a Xaman or
WalletConnect signer. Chunks larger than 1000 bytes. A chunk memo on the outer. The cause of the
ticket path's three-minute pause and dropped connection on testnet: that is its own
investigation, and this design does not depend on it.

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| B1 | Two accounts: the chunks are inners of writer A on A's `Sequence`; the outer is signed and paid by payer B on B's `Sequence`, with A as its only `BatchSigner` | The only shape the spike found both fast and safe. Inners on sequences of one account give one batch per ledger; outers on tickets lose chunks when batches reorder |
| B2 | `tfIndependent`, not `tfAllOrNothing` | An inner that ends in `tec` then costs one chunk and keeps A's sequence whole. With all-or-nothing, the same failure stops A's sequence, and every batch behind it is paid for and writes nothing |
| B3 | The payer is any key in the vault on the writer's network, chosen per publication | The person decided this. A long-lived funded account can pay for many publications while each writer stays a throwaway that needs only its base reserve. The payer appears nowhere in the file format, so it can change between a run and its resume |
| B4 | The ticket path stays. A publication uses `BatchStrategy` when a payer is chosen and the network has `BatchV1_1` enabled. With no payer, the existing policy applies: `SequenceStrategy` below 100 memos, `TicketStrategy` from there | The person decided this. A network without the amendment, such as a private stand on an older build, must still be able to publish. A run that started on tickets resumes on tickets |
| B5 | Batches go out in rounds. Each round reads B's and A's validated sequences from one ledger, assigns them, and sends with a deadline. It ends when the validated ledger is 2 past the last deadline. Then it sweeps, and the missing memos go into the next round with fresh sequences | After a deadline has validated, no batch of that round can land, so the next round cannot collide with a straggler. The spike's alternative was to wait out the 30-second re-relay tail with no deadline, which gives no point at which a batch is certainly dead |
| B6 | A batch's `LastLedgerSequence` is 20 past a validated index read no more than 10 s before that batch is signed | 20 ledgers is about 70 s, which covers the re-relay tail of two 30-second periods. Reading the index per batch rather than once per round keeps a long, throttled round from signing batches that are already expired |
| B7 | A chain check every 2 s halts the round early. **Stalled**: the validated ledger has passed the deadline of the first batch B has not applied. **Diverged**: A's validated sequence is not what B's applied batches imply | A lost outer holds every later outer at `terPRE_SEQ` until its deadline, at no cost, but waiting for that is pointless. A gap in A's sequence makes every later inner fail while B pays. Halting stops the spending at the first sign of either |
| B8 | A round sends through a window that starts at 16, halves after 3 busy answers in a row, grows by 1 after 10 clean sends, and never runs more than 256 batches past B's validated sequence. "Busy" is any `tel` answer, a `tooBusy`/`slowDown` error, or a send that never got an answer | Both the platform and the spike show that a public node's limit is the client's own send queue and rate. The 256-batch cap limits what one divergence can cost to about 0.03 XRP at the base fee |
| B9 | After a dropped connection, the strategy reconnects and resends the same signed blobs that were unanswered, then lets the chain check and the sweep decide | A lost answer says nothing about the batch. An identical blob is idempotent: `tefPAST_SEQ` means it applied, `terPRE_SEQ` means it is held |
| B10 | Memos are grouped 8 at a time. A trailing group of 1 borrows one memo from the group before it (8 + 1 becomes 7 + 2). A round with exactly one memo sends it as a plain `AccountSet` of A, and only once nothing of the round's batches is in flight | A batch needs 2 to 8 inners. A's own transaction must never share a ledger window with batches in flight (section 1) |
| B11 | The fee unit is the node's open-ledger fee × 1.5, rounded up, at least the base fee and at most 200 drops, read again at the start of every round. A batch pays (inners + 3) × unit | The exact Batch formula, with headroom for escalation that the next round corrects without a per-batch bump. The 200-drop ceiling keeps a misbehaving node from draining B |
| B12 | `SubmitAllAsync` fails after 3 rounds in a row that land nothing new. A round that lands anything resets the count | A file of 7,000 chunks needs many rounds under the 256-batch cap. Only rounds that make no progress mean something is wrong |
| B13 | A transaction of A's own that the strategy did not send, seen in a round, fails the run with a new error `AccountInUse` | It breaks the sequence arithmetic of every batch in flight. The cause is outside the application, such as the same key used elsewhere, and retrying cannot fix it |
| B14 | `BatchV1_1` is read from the ledger's `Amendments` object through `ledger_entry`, by its id: the SHA-512Half of the name `BatchV1_1`. It is read once per network per application session, and again by the estimate before a run starts | `feature` is restricted on public nodes. `ledger_entry` is public and is what the SDK's own amendment guard reads. The estimate's read is authoritative; the cached one only shapes the screen |
| B15 | Signing goes through `VaultSigner`. The strategy fills the outer, including B's `SigningPubKey` and `BatchSigners` with A's account, and has A sign its part. It merges that part into the outer, and B signs the outer. If the vault's per-signature decryption proves too slow, measured on a 1 MB run, `VaultSigner` gets a wallet cache for the life of one session, cleared by the vault's lock | The order is the platform's and the SDK's. Each signature decrypts a seed today, about 1,800 for 7 MB. Caching is decided by a measurement, not assumed |
| B16 | The state file stays at version 2 and gains three optional fields: `Payer`, `PayerKeyReference`, `SourcePath`. A file without them loads as today | Bumping the version would make every unfinished run's file unreadable, and those runs have spent money |
| B17 | Resume reads the file from `SourcePath` and checks its size and Merkle root before using it. The file picker opens only when that file is missing or changed | The person picked the file once. Asking again is the complaint this design answers. The root check is what makes reusing the path safe |
| B18 | Resume keeps the run's strategy. A Batch run whose payer key has left the vault asks the person to choose another payer on the same network, and records the choice in the state file | The payer is outside the format (B3). Refusing the resume would strand a paid-for run over a choice that does not matter to the file |
| B19 | The failed-run screen offers Resume when the run is listed as unfinished, and it takes the same path as the unfinished-runs list | The person was on that screen when it failed. The list already knows how to resume; the screen only lacked the button |
| B20 | Progress counts memos as they are confirmed, not once per batch. On the Batch path a round's count comes from A's validated sequence while it runs and from the sweep at its end. On the ticket path it comes from every sweep poll. The count never goes down on screen | The person watched "100 of 7,124" for minutes while hundreds more chunks landed. A's validated sequence is already read every 2 s for the chain check, so progress costs no extra request |
| B21 | Time left = memos remaining ÷ the rate of confirmed memos over the last 30 s. Before 30 s of data, the Batch path assumes 70 memos/s, the platform's conservative figure. The ticket path shows "estimating…" until it has data. The run screen also shows the elapsed time and the rate | A rate measured on this run, on this node, beats any constant. A prior is used only where a measured one exists |
| B22 | Every new `XrplFileError` value gets its sentence in `PublishErrorMessages`, and the existing exhaustiveness test keeps it that way | The application must never show an error it cannot explain |

## 4. The library

### 4.1 `BatchStrategy`

```csharp
public sealed record BatchStrategyOptions
{
    public int InitialWindow { get; init; } = 16;
    public int MaxAhead { get; init; } = 256;
    public int DeadlineLedgers { get; init; } = 20;
    public int SettleLedgers { get; init; } = 2;
    public TimeSpan ChainCheck { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan IndexMaxAge { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxIdleRounds { get; init; } = 3;
    public long MaxFeeUnitDrops { get; init; } = 200;
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

public sealed class BatchStrategy(
    ILedgerSink sink, ILedgerSource source,
    ITransactionSigner writer, ITransactionSigner payer,
    BatchStrategyOptions? options = null) : ISubmissionStrategy
```

- `Account` is the writer's account. `TicketMints` is 0.
- `FeeDropsPerTransaction` is the fee unit the strategy was built with, so the session's
  `FeeMismatch` check keeps its meaning: the estimate and the strategy agree on one unit.
- The constructor refuses a payer equal to the writer.
- `SubmitAllAsync` runs rounds (B5) until every memo has landed or B12 fails it.

One round:

1. Read the fee unit (B11). Read B's validated `Sequence` and A's validated `Sequence` from the same
   validated ledger L, so that a ledger close cannot fall between the two reads.
2. Group the round's memos (B10). Batch k gets outer sequence B₀ + k. Its inners take A's sequences
   in order, starting at A₀ plus the inners of every batch before it. A single memo is set aside as
   the round's solo memo.
3. Send through the window (B8). Each batch is built, given its deadline (B6) and signed (4.2) only
   when its window slot opens. The blob is kept until the round ends, for B9.
4. Run the chain check (B7) while sending, and until the round settles:
   - `applied` = B's validated sequence − B₀.
   - Expected A = A₀ + the inners of batches 0 … `applied` − 1.
   - A below that expected value is **diverged**. A above it, by anything other than the round's
     own solo memo, is `AccountInUse` (B13).
   - The validated ledger past the deadline of batch `applied` is **stalled**.
   - Either one stops new sends. The round then settles.
5. Settle: wait until the validated ledger is `SettleLedgers` past the latest deadline of a batch
   this round actually sent.
6. Send the solo memo, if any, as a plain `AccountSet` of A on A's validated sequence. Wait for it
   to validate.
7. Sweep `account_tx(A)` from the call's first ledger to the current validated ledger. The sweep
   matches marker and data, as the ticket sweep does (spec 2026-10-12, I12). It retries while A's
   validated sequence covers transactions that `account_tx` does not yet return.
8. Report progress (4.4). Count the round as idle if it landed nothing new (B12).

Submit answers within a round:

| Answer | Meaning | Action |
|---|---|---|
| `tesSUCCESS`, `terQUEUED`, `terPRE_SEQ` | accepted or held | none; the chain check and the sweep decide |
| `tefPAST_SEQ`, `tefALREADY` | the blob, or this sequence, already applied | none |
| other `tef`, `tec` | the outer will not apply as sent | none; the chain check stalls the round at its deadline, and the next round resends its memos |
| `tel*`, `tooBusy`, `slowDown`, no answer | busy (B8) | narrow the window; resend the same blob after a pause of 400 ms × (attempt + 1), up to 4 attempts, then leave it to the deadline |
| `tem*`, other error | malformed; a bug, not a condition | fail the run with `SubmitRefused`, quoting the answer |
| dropped connection | B9 | reconnect; resend every unanswered blob |

`ReleaseLeftoverTicketsAsync` spends this library's leftover tickets on A with plain `AccountSet`s
of A. It runs only after the last round has settled, so nothing is in flight. The behaviour and the
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

A signs first, then B. The steps to prove first, against the stand, before anything else is built:

1. `writer.SignAsync(outer)` returns a blob whose decoded `BatchSigners[0]` carries A's
   `SigningPubKey` and `TxnSignature`.
2. Merging that part into the outer and calling `payer.SignAsync(outer)` gives a blob that the
   stand accepts. Its inners land as `AccountSet`s of A.
3. All of this goes through two `VaultSigner`s on one vault.

If step 1 does not hold for `VaultSigner` as written, `ITransactionSigner` gains one method,
`SignBatchPartAsync(outer, ct)`, implemented by `VaultSigner` and `WalletSigner` over the SDK's
`SignAsBatchPart`. Nothing else changes. If 11.2.0's signatures are refused, `Xrpl` moves to the
current release in `Directory.Packages.props`. That is a separate commit that runs the whole suite.

### 4.3 The amendment

```csharp
// ILedgerSource
Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken);
```

This reads `ledger_entry` for the `Amendments` object of the validated ledger and looks for
SHA-512Half(name) in `Amendments`. An answer of `entryNotFound` means no amendment is enabled, so
the result is `false`. Any other failure is `NodeUnreachable`, as it is for other reads.

### 4.4 Progress

`IProgress<int>` keeps its contract: the number of the call's memos known to be in the ledger.

- **Batch path.** During a round: memos landed in earlier rounds, plus the round's inners covered
  by A's validated sequence, at most the round's memo count. At the round's end: the sweep's
  count.
- **Ticket path.** `SweepAsync` reports the memos found so far on every poll, not once at the end
  of the batch.
- **Both.** The reported value never decreases: a lower count from a sweep is held at the last
  value, and the final result is always the sweep's.

### 4.5 The estimate

```csharp
public sealed record BatchPublicationEstimate(
    long Chunks, int Blocks, int Lists,
    int Batches, int SoloTransactions,
    long FeeUnitDrops,
    long PayerFeeDrops,            // Σ (inners + 3) × unit over the batches, nothing retried
    long PayerFeeDropsWorstCase,   // the same at ceil(unit × 1.5), capped at MaxFeeUnitDrops, × MaxIdleRounds
    long WriterFeeDropsWorstCase,  // (solo transactions × MaxIdleRounds + MaxLiveTickets) × capped unit
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

- Batches and solo transactions are counted over the three passes, grouped by B10.
- The writer's worst case includes a tidy-up of up to 250 tickets (spec 2026-10-12, I10).
- `PublicationSession.EstimateBatchAsync`, `StartAsync` and `ResumeAsync` take the payer and use
  this estimate when the strategy is a `BatchStrategy`. They refuse in order:
  1. `BatchNotEnabled` when the amendment is off.
  2. `InsufficientBalance` when the writer cannot cover its part.
  3. `PayerInsufficientBalance` when the payer cannot cover its part.
  4. `BudgetExceeded` against `PayerRequiredDrops + WriterRequiredDrops`.
- The two `strategy is TicketStrategy` sites stay as they are. A `BatchStrategy` never reaches
  them.

### 4.6 The report

`PublicationReport` gains `PayerFeeDrops`, the sum of the `Fee` of B's outers that this session
found applied. It is read from `account_tx(B)` over the session's ledgers. The writer's fees are
counted as today. The report says "unknown" for the payer when that read fails. It never prints a
guess.

### 4.7 New errors

| Value | When |
|---|---|
| `BatchNotEnabled` | a Batch run on a network whose ledger does not enable `BatchV1_1` |
| `PayerInsufficientBalance` | the payer's spendable balance is below its part |
| `AccountInUse` | A's sequence moved by a transaction the strategy did not send (B13) |

## 5. State and resume

### 5.1 The state file

`PublicationState` gains three optional fields:

| Field | Value | Absent means |
|---|---|---|
| `Payer` | B's classic address | the run is not a Batch run |
| `PayerKeyReference` | B's vault reference | as above |
| `SourcePath` | the absolute path the file was read from | ask for the file on resume, as today |

A version-2 file without these fields loads as today and resumes on its own strategy.
`SessionOptions` carries the payer and the source path into `StartAsync`, which writes them.

### 5.2 Resume

1. The file: read `SourcePath`. A missing file, a different size or a different Merkle root falls
   back to the file picker, with the sentence "The file was moved or changed since this run
   started. Choose it again." The picked file is checked the same way, as today.
2. The strategy: a run with `Payer` resumes with `BatchStrategy`. A run without it resumes with the
   existing policy.
3. The payer: if `PayerKeyReference` is no longer in the vault, the resume asks for a payer among
   the keys on the run's network. It then writes the new `Payer` and `PayerKeyReference` to the
   state file before running.
4. Reconciliation is unchanged: a sweep of `account_tx(A)` from `StartedInLedger`. A batch of the
   interrupted run that lands after the resume starts is harmless. The resume's batches on the same
   sequences then answer `tefPAST_SEQ`, the sweep finds what landed, and the next round sends the
   rest.

## 6. The application

### 6.1 Publish

- Below the key list comes a "Paid by" drop-down. It lists every other key on the selected key's
  network by label and address, plus "Nobody — publish with tickets". The ticket option carries a
  muted note: "Slower, and holds up to 50 XRP of reserve while publishing."
- Default: the payer used last on that network, kept in a small settings file next to
  `node-urls.json`. Failing that, the first other key on that network. Failing that, "Nobody".
- When the network's cached amendment read (B14) says `BatchV1_1` is off, the drop-down is fixed on
  "Nobody" with the note "This network does not support Batch, so publishing uses tickets." When
  the read failed, the drop-down stays usable, and the estimate decides.
- `PublishRequest` and `PublishModel` gain `PayerReference` and `PayerAccount`.
- `PublishPolicy.Create` returns a `BatchStrategy` when the request has a payer.

### 6.2 Estimate

When the request has a payer, the card shows:

1. The leading rows: chunks; batches; solo transactions; fee unit, in drops.
2. **Payer** (label, address): fees and fees in the worst case, the account reserve, required,
   balance and spendable.
3. **Writer** (label, address): the same, where fees are only the solo transactions and the
   tidy-up.
4. A line naming the method: "Batch, paid by `<label>`."

Amounts are in XRP, the unit in drops, as the screen already does. The shortfall message names the
account that is short: "The payer `<label>` needs X XRP and can spend Y." Without a payer, the card
is the ticket estimate of today.

### 6.3 Run

- The progress line reads "Writing the file — 1,240 of 7,124 · 52 chunks/s · about 2 min left ·
  0:24 elapsed". The time left follows B21. The rate and the time left live in `RunModel` and run on
  an injectable `TimeProvider`.
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
| `AccountInUse` | "The writer account sent a transaction of its own during the publication — is this key in use somewhere else? The run is listed under unfinished runs; resume it once nothing else uses the key." |

## 7. Testing

1. **Signing on the stand, first.** Prove 4.2 steps 1–3 with two `VaultSigner`s on the
   `xrpl-file-ci` stand. That stand enables `BatchV1_1` in `[features]` and `[amendments]`. If the
   proof fails, apply the fallbacks in 4.2 before anything else.
2. **Core unit, with a new `FakeBatchLedgerSink`.** It keeps a sequence per account. It applies an
   outer at submit and its inners at a scripted close, and adds the inners to `FakeLedgerSource` as
   A's `AccountSet`s. Scripts cover:
   - an inner ending in `tec`, and an inner that does not consume A's sequence (diverged);
   - an outer that never lands (stalled);
   - `terPRE_SEQ` reordering;
   - `tel`/busy answers;
   - a dropped connection with unanswered blobs;
   - a foreign transaction of A (`AccountInUse`);
   - `account_tx` lagging the validated sequence.

   Tests cover:
   - grouping (never 1; 8 + 1 becomes 7 + 2);
   - sequence assignment, the fee formula, the deadline per batch, and the window's narrowing and
     widening;
   - the 256 cap, settling, solo memos after settling, idle-round failure and its reset;
   - progress that never decreases;
   - the estimate's arithmetic and its refusals in order.
3. **Core on the stand:**
   - 1 MB through `BatchStrategy` with two vault signers, then read back by `XrplFileReader`;
   - a run whose connection is closed in the middle;
   - a stopped run resumed with a different payer;
   - `IsAmendmentEnabledAsync` true for `BatchV1_1`, and false for a made-up name.
4. **App.Core:**
   - "Paid by" defaults, its memory, and its lock when the amendment is off;
   - `PublishPolicy` picking `BatchStrategy`;
   - the two-account estimate and its shortfall sentences;
   - resume from `SourcePath`, falling back to the picker on a moved or changed file;
   - resume with the payer key gone;
   - Resume from the failed-run screen;
   - the time left with a fake clock: the prior, the measured rate, and "estimating…" on tickets;
   - the new error sentences (the exhaustiveness test).
5. **Acceptance, by hand, in the MauiLab VM against testnet:**
   - the same 7,123,692-byte file, writer and payer from the vault;
   - wall time recorded;
   - read back by the application and by the web reader;
   - one run interrupted by closing the window, then resumed from Unfinished runs without picking
     the file.

## 8. What this does not do

- It does not make the ticket path faster or fix its dropped connection on testnet. That
  investigation stays open, and its findings go to their own spec.
- It does not let a payer outside the vault, such as a hardware or mobile wallet, pay. Any signer
  that can sign an outer would do, but the vault is the only signer the application has.
- It does not move B's funds back to the writer or delete B. B is the person's own key, and the
  Accounts tab already closes accounts.
- It does not measure mainnet. The fee ceiling of B11 and the window of B8 are the guard rails
  until it is measured.
