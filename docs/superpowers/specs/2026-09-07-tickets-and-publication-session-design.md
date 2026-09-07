# Tickets and the publication session

Date: 2026-09-07
Status: approved design, ready to be broken into a plan
Format: `docs/spec/xrpl-file-v1.md` (unchanged by this work)

## Why

`XrplFileWriter` submits one transaction at a time on the account's sequence, waiting for
validation every `BatchSize` submissions. That is about three transactions a second, so a
megabyte takes five minutes and twenty megabytes take an hour and a half. For a library that is
about to sit under an application people use, that is the difference between a tool and a
demonstration.

Tickets (XLS-16) remove the ordering constraint: a transaction carries a `TicketSequence`
instead of a `Sequence` and stands on its own, so many can be in flight at once. The XRPL video
platform measured the difference on testnet: two hundred transactions took 74 seconds serially
and 2 seconds on tickets, with nothing lost.

This work is the first of three that lead to a desktop application. It is the part where a
mistake costs money, so it lands first and alone.

| Part | What it is | State |
|---|---|---|
| A | tickets and the publication session — this document | designed |
| B | the key store: several keys, a master password, a backup | not started |
| C | the MAUI Blazor Hybrid application over A and B | not started |

## What the format says about this

Nothing, and that is the point. A chunk's index lives in its `MemoType`, so a reader never
depended on the order transactions were submitted in. Section 6 of the format document defines a
chunk by its marker, its account, its result and its ledger range — never by a sequence number.

Tickets are therefore a delivery concern alone. No published file changes meaning, no reader
changes, and the browser library is untouched by this work.

## Decisions

| Decision | Choice |
|---|---|
| Where the ticket logic lives | a submission strategy behind an interface, not inside the writer |
| Where money and durability live | a publication session, which is what an application holds |
| What the writer keeps | the format's order and the manifest's construction, nothing else |
| Reserve figures | read from the node's `server_state`, never hardcoded |
| Funding a throwaway account | the user sends to an address the app shows; the app never moves their money |
| Returning the remainder | a payment, not `AccountDelete` — see "Why the account cannot be deleted" |
| The refund destination | proposed from the observed funding payment, confirmed by the user, never assumed |

## 1. Three units

### `ISubmissionStrategy`

One question: how does a set of memos reach the ledger. The contract is deliberately strong —
when the call returns, every memo is in a validated ledger and its transaction hash is known.

```csharp
public sealed record MemoSubmission(string MemoTypeHex, string MemoDataHex, string? MemoFormatHex);
public sealed record SubmittedMemo(int Index, string TxHash);

public interface ISubmissionStrategy : IAsyncDisposable
{
    Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken);
}
```

Two implementations. `SequenceStrategy` reproduces today's behaviour exactly: one transaction at
a time on the account's sequence, a validation wait every `BatchSize` submissions.
`TicketStrategy` mints tickets in batches and submits in parallel.

### `PublicationSession`

Money and durability: an estimate before anything is sent, a budget ceiling, state on disk,
resumption, progress, and a run report. This is the object an application holds.

### `XrplFileWriter`

Loses `sequence++` and its validation waiting. Keeps exactly one piece of knowledge: the
format's order — chunks, then block lists, then the read-back, then the manifest last. It
receives a strategy from outside and no longer knows how a transaction reaches the ledger.

The split is justified by how each unit fails. A strategy fails on a lost ticket or a full open
ledger. A session fails on an insufficient balance or an interrupted run. A writer fails on a
wrong order or a read-back that does not match. Today all three failures live in one class and
are reached through one entry point.

## 2. The ticket strategy

### What the protocol fixes

Read from `TicketCreate.cpp` and `TicketCreate.h` in rippled at `5ce0b1c2c`:

| Fact | Value |
|---|---|
| Tickets per `TicketCreate` | 1 to 250 (`kMinValidCount`, `kMaxValidCount`) |
| Live tickets per account | at most 250 (`kMaxTicketThreshold`), else `tecDIR_FULL` |
| Which numbers are minted | a `TicketCreate` at sequence `S` mints tickets `S+1` through `S+N` |
| The sequence afterwards | `S+1+N`; a ticketed transaction does not move it at all |
| Not enough reserve | `tecINSUFFICIENT_RESERVE`, measured against the balance before the fee |

`TicketCreate` is the only transaction in the protocol that moves an account's sequence by more
than one. That fact returns in section 4 and decides the refund's shape.

### The batch loop

1. **Mint.** One `TicketCreate` on the current sequence, awaited for validation. It is the only
   transaction the strategy sends on a real sequence, so it is the only one whose validation can
   be awaited by watching the sequence.
2. **Submit.** Up to `N` memos in parallel, each on its own ticket, with `Sequence` zero.
3. **Reconcile.** One `account_tx` sweep forward from the mint's ledger, until every marker in
   the batch is seen or a deadline passes.
4. **Carry over.** Whatever is missing joins the next batch on fresh tickets.

The reconciliation sweep replaces the sequence-based validation wait, which cannot work here:
ticketed transactions do not move the sequence, so there is nothing to watch. The sweep answers
both questions at once — whether the batch landed, and which parts of it did — and it is the
same mechanism a reader uses.

### What each answer from the node means

| Answer | Meaning | What the strategy does |
|---|---|---|
| `tesSUCCESS` | applied | waits for the marker in the sweep |
| `terQUEUED` | queued, nothing spent yet | waits for the marker until the deadline; only an expired deadline is a retry |
| `terPRE_TICKET` | the ticket is not in a ledger yet | a bug in the strategy: the mint was not awaited |
| `tec*` | applied, ticket spent, chunk not written | retried on a fresh ticket in the next batch |
| `tem*`, `tef*` | not applied, ticket intact | retried on the same ticket |

`terQUEUED` deserves the care. It is a `ter` code, and rippled's own comment on that category
reads "Not applied. May be forwarded. Might succeed later." The transaction is in the queue and
has spent nothing. Treating it as lost and re-sending the chunk on a fresh ticket puts two
copies in the ledger. For chunks that is not corruption — the bytes are identical and a reader
keeps the first copy in canonical order — but it is money and transactions spent for nothing.

A `tec` is the opposite case. Its category comment reads "Claim fee only… claim fee to use the
sequence number", followed by "Implications: Applied." What happens to the ticket is visible in
`Transactor::reset`, the path a `tec` takes: it charges the fee and updates the sequence "or
consume the Ticket", and `consumeSeqProxy` calls `ticketDelete` for a ticketed transaction. The
ticket is gone and the chunk was not written, so the retry needs a new one.

### Two limits that meet

A batch may hold at most 250 live tickets. Separately, a standalone node was measured refusing
one account's transactions beyond about 1010 in the open ledger, with `telCAN_NOT_QUEUE_FULL`.
The ticket ceiling therefore keeps the open ledger under its own limit without a second
mechanism. The batch size stays an option, defaulting well under both.

### Reserve figures come from the node

Reserves have changed and will change again. The strategy and the estimate read
`reserve_base_xrp` and `reserve_inc_xrp` from `server_state` rather than carrying a constant.
The peak reserve of a run is the batch size multiplied by the incremental reserve, plus the
account's base reserve.

## 3. The publication session

### The estimate

Computed before anything is sent, and returned as numbers rather than a verdict, because a
person is shown it before they spend money.

| Quantity | Where it comes from |
|---|---|
| Transactions in total | chunks, plus block-list memos, plus the manifest, plus one mint per batch |
| Fee per transaction | the node's open-ledger fee, capped by an option |
| Peak reserve | the batch size multiplied by the incremental reserve from `server_state` |
| Balance required | the fees, plus the peak reserve, plus the account's base reserve |

The session refuses to start when the balance does not survive the worst case, or when the worst
case exceeds the caller's budget ceiling. The ceiling is the same rail the war-and-peace
uploader carried as `--budget-xrp`.

### State

State survives the process dying. It holds the file's identity (root, size, chunk size, MIME
type, name), the network and the writer's address, the strategy and its options, the phase
reached, the ledger the run started in, and the map from index to transaction hash for what has
landed.

The hash map is not what resumption trusts — the next section says why — but it is not
redundant either. It carries the run's report through an interruption, and the starting ledger
it accompanies is where the reconciliation sweep begins, which is the difference between reading
a few hundred ledgers and reading the account's whole history.

It does not hold a seed. The session stores a reference to a key, never the key itself; storing
keys is part B's job. That keeps the whole secret surface in one place.

### Resumption does not trust the state file

A crash can fall between a submission and its record, so `ResumeAsync` reconciles against the
ledger with the same `account_tx` sweep the strategy uses, and only then continues. It refuses
when the file has changed, meaning a different root, and when the wallet is not the writer the
state names.

### Abandoned runs must be findable

A user who closes the application and does not return has left money on a throwaway account, and
the only record of it is a state file. The session therefore defines where those files live and
can enumerate the unfinished ones. Part C turns that into a screen.

### The report

The run dataset in the shape war-and-peace produced: transaction hashes, ledgers, fees, timings,
per-ledger counts. It is what an application shows after a success and offers to save.

Progress grows to what an interface needs: the phase, how much is done, the total, the current
rate and an estimate of what remains.

## 4. The throwaway account

### The shape

1. **The key is born in the application** and enters the store before a single drop reaches the
   account. An interruption between funding and storage would otherwise lose money.
2. **Funding arrives from wherever the user chooses.** The application shows the address, a copy
   button and a QR code; the user sends from Xaman, an exchange, or a hardware wallet. The
   application never moves their money and never needs their own key for this.
3. **The session waits.** It polls the account's balance until the estimate is covered. This is a
   state that survives the application closing, because the money may arrive an hour later.
4. **Publication runs** from the throwaway account by the ordinary path.
5. **The remainder is returned** by payment, to an address the user confirmed.

### The refund destination is proposed, never assumed

The incoming funding payment names its sender, and the throwaway account's history is where the
session reads it. That is a good default for the refund field. It stays a proposal: the user
confirms it or types another address. The same rule governs `AccountDelete` later — the
application does not pre-fill the destination and does not enable the action until an address is
entered and confirmed.

### Why the account cannot be deleted

`AccountDelete` would return the base reserve too, and it is unavailable here for a long time.

Its precondition in `AccountDelete.cpp` is that the account's sequence plus 255 must not exceed
the current ledger index, or the answer is `tecTOO_SOON`. A newly created account's sequence is
the index of the ledger that created it. Every `TicketCreate` then moves that sequence forward
by the number of tickets plus one, while the ledger index advances once every four seconds or
so.

| File | Mints | Sequence moves ahead by about | Wait before deletion |
|---|---|---|---|
| 1 MB | 4 | 1000 | an hour and a half |
| 20 MB | 80 | 20000 | a day |

Making further transactions cannot help: they move the sequence up, which is the wrong
direction. Only the ledger index catching up makes deletion possible.

The remainder is therefore returned by payment, and the base reserve stays on a dead account.
That is the honest price of this mode and the estimate states it before the run starts, rather
than letting it be discovered afterwards.

Reclaiming the reserve later is possible and belongs to part C: the application remembers such
accounts and offers to delete one once the ledger index has caught up. What part A owes that
feature is a record of the account that outlives the publication, and the arithmetic that says
when the moment arrives.

Tickets do not stand in the way. `ltTICKET` maps to `removeTicketFromLedger` in `AccountDelete`'s
deleter table, so unspent tickets are cleaned up by the deletion itself.

### One thing to measure before it is believed

Everything in this section about deletion timing is read from rippled's source, not measured.
The first task of the plan settles it on the standalone stand, where ledgers close on command:
create an account, mint tickets, attempt the deletion, receive `tecTOO_SOON`, drive
`ledger_accept` to the predicted threshold, and confirm the deletion succeeds exactly there. The
same test confirms the opposite direction, that submitting more transactions only pushes the
threshold further away. The formula reaches the application only after the stand agrees with it.

## 5. Testing

A fake answers questions about our logic. A node answers questions about the protocol. Tickets,
`tecTOO_SOON`, the open ledger's ceiling and the queue's behaviour are all properties of the
node, so they are measured against one. The stand already exists and closes ledgers on command,
which makes the time-based conditions quick to test.

| Level | What it proves | Against |
|---|---|---|
| Strategy | the batch loop, the retry of a failed chunk, each answer in the table | a fake sink with a scripted answer list |
| Strategy, stand | minting, parallel submission, `terPRE_TICKET`, the open-ledger ceiling | the standalone node |
| Session | the estimate, the budget refusal, state, resumption after an interruption | a fake strategy |
| Session, stand | a whole publication on tickets, interrupted and resumed | the standalone node |
| Throwaway account, stand | waiting for funding, the refund, the deletion threshold | the standalone node |
| Writer | the format's order, the read-back | as today |

Resumption is proved by a real interruption. The test publishes, kills the session mid-run,
builds a new one from the state file, finishes, then reads the file back through the reader and
compares byte for byte. Nothing less proves that the state and the reconciliation sweep work
together.

The existing writer tests move onto `SequenceStrategy` with their assertions unchanged. If they
need changing, the sequential path has changed behaviour, which this work does not intend.

## What changes in existing code

| Place | Change |
|---|---|
| `XrplFileWriter` | takes an `ISubmissionStrategy`; loses sequence handling and validation waiting |
| `WriterOptions.BatchSize` | moves to the sequential strategy's options |
| `ILedgerSink` | unchanged. Its `SubmitAsync` already returns on the node's answer without waiting for validation, and calling it concurrently is what parallel submission needs. A ticketed transaction differs only in the dictionary the caller builds |
| `ILedgerSource` | gains what the estimate reads: the open-ledger fee, the reserves from `server_state`, and an account's balance |
| `XrplFileError` | gains `InsufficientBalance` (the account cannot carry the run the estimate describes), `BudgetExceeded` (the worst case is above the caller's ceiling) and `ResumeMismatch` (the state names a different file or a different writer) |
| Tests | writer tests move onto the sequential strategy unchanged |

## Rejected alternatives

- **Ticket logic inside `XrplFileWriter`.** Fewest new types today, at the price of a class that
  holds the format's order, the pacing, the ticket lifecycle, the estimate and the persistence.
  Five reasons to change one file, and a long-running method that an interface cannot pause.
- **An explicit step machine the caller drives.** Maximum control for a graphical interface, at
  the price of moving the publication's order out of the library and into every caller. That
  order is the one thing the library must not let leak.
- **`AccountDelete` for the refund.** Returns the base reserve as well, and is unavailable for
  hours or days after a publication for the reason above. Kept as a later feature in part C,
  where waiting is acceptable.
- **Funding by a payment the application sends.** Would need the user's own key in the store for
  every publication. Showing an address and a QR code needs none, and works with wallets that
  will never hand a key to anything.
