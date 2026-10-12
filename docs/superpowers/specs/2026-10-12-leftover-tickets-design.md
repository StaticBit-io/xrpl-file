# Leftover tickets — design

A ticketed publication that is interrupted leaves tickets on its account, and each one locks an
owner reserve. Nothing in the library or the application spends them again, and nothing on screen
says they are there. This document fixes both: a publication picks up the tickets this library
left behind, spends any it does not need once it finishes, and the application says plainly what
an interruption costs until then.

## 1. What was found

In a running window, against a standalone node, a 3,145,728-byte publication was stopped by
closing the window part-way through its chunks, then resumed and finished. Afterwards the account
held **100 live tickets**, sequences 970 to 1069: the batch the stopped run had minted and not yet
spent. At the stand's owner reserve of 2 XRP that is 200 XRP locked on an account whose file was
already complete. They went away only when the account was deleted, which removes its tickets with
it.

The cause is stated in the library's own words. A new `TicketStrategy` spends only tickets it
minted itself, and `TicketStrategy.DisposeAsync` says of a ticket left in its queue that "nothing
re-adopts one". A resume builds a new strategy, so it mints fresh tickets over the ones the
interrupted run abandoned.

Three further facts shape the design, each checked rather than assumed:

- **A ticket can be told apart from somebody else's.** Every Ticket ledger entry carries
  `PreviousTxnID`, the `TicketCreate` that made it, and a ticket is not modified between creation
  and use. A `TicketCreate` carrying a memo is accepted by the node, and on the stand two tickets
  were traced to a marked mint and a third to an unmarked one.
- **A spent ticket answers `tefNO_TICKET`.** Measured on xrpld 3.3.0: a transaction riding a ticket
  that another transaction already consumed is refused with `tefNO_TICKET`, "Ticket is not in
  ledger", and a `tef` result is never applied, so no fee is claimed. A ticket that does not exist
  yet answers `terPRE_TICKET`.
- **The strategy mishandles that answer today.** `SendOneAsync` treats every `tef` as "never
  reached a ledger, so the ticket is intact", and the ticket goes back into the queue. A ticket that
  is gone would then circulate for ever, costing each memo that draws it an attempt. Nothing can
  reach that path today, because nothing else spends this strategy's tickets. Picking up
  abandoned tickets makes it reachable: the stopped run's own submissions carry no
  `LastLedgerSequence`, so one may still be in the node's queue and land on a ticket the resumed
  run has just picked up.

## 2. Scope

**In.** Marking the tickets this library mints. Picking up this library's live tickets when a
strategy starts. Treating `tefNO_TICKET` as a ticket that is gone. Spending this library's leftover
tickets after a publication succeeds. Making the estimate agree with all of that. Saying what an
interruption leaves behind, in the window-close confirmation and in the unfinished-runs list.

**Out.** Tickets the account holds for any other purpose, which this design never touches.
Tickets minted before this change, which carry no mark and so are indistinguishable from anybody
else's. `LastLedgerSequence` on submissions. Running two writers on one account at once.

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| I1 | Tickets are picked up when a strategy first needs them, not recorded per run | Leftovers belong to the account, not to a run. Recording them in the state file would help only a resume of the same run, change that file's format, and leave every other run's leftovers locked |
| I2 | Only this library's tickets are ever picked up or spent, identified by a mark on the `TicketCreate` that made them | The documented use of tickets is a transaction prepared in advance for signatures that arrive later. Spending somebody's reserved ticket would quietly invalidate a multi-signed transaction they are still assembling, and an imported key may belong to exactly such an account |
| I3 | A ticket whose creator the node cannot show is treated as somebody else's | The safe failure is to leave a ticket alone. A node with truncated history cannot prove a ticket is ours, and the cost of being wrong is somebody else's transaction |
| I4 | Tickets are picked up once per strategy instance | After that the instance's own bookkeeping is authoritative. Asking again would pick up tickets this same instance has dispatched and is waiting on, and dispatch them twice |
| I5 | `tefNO_TICKET` removes the ticket and does not count against the memo | The ticket is gone, so returning it to the queue loops. The memo did nothing wrong, and three such tickets in a row would otherwise fail a publication on which nothing was spent |
| I6 | After a publication succeeds, this library's live tickets are spent on transactions that do nothing | Picking up spends only as many as the remaining memos need. A run interrupted late — during the block list, say — would pick up a hundred tickets, spend five and leave the rest. Spending one costs one transaction fee and frees one owner reserve, several thousand times the fee on any network this library addresses |
| I7 | Both strategies tidy up, not only the ticketed one | Then "a later publication from this account uses them" is true of every publication, and the unfinished-runs list can say so without a condition on file size |
| I8 | Tidying up cannot fail a publication | The manifest is in the ledger by then and the link works. A tidy-up that fails is reported on the publication report, the way a state file that could not be saved already is |
| I9 | The estimate's peak reserve counts only tickets beyond those already live | Their reserve is already subtracted from what the account can spend. Counting it again overstated the stopped-and-resumed run above by 200 XRP, enough to refuse a resume that would have succeeded |
| I10 | The estimate's worst-case fee includes the tidy-up | The approval is meant to cover what the run spends. The tidy-up is at most the protocol's ceiling of live tickets, a few thousand drops at the fees this library uses |
| I11 | Which sentence the close confirmation shows is a model's decision | Whether to mention tickets depends on whether the run uses them, and C1's E2 puts every decision where a test on Linux can reach it. The confirmation is assembled today inside the window's close handler, which no test can run |

## 4. The library

### 4.1 Marking

Every `TicketCreate` this library submits carries one memo whose type is a new marker, `xf/t`,
alongside the existing `xf/c/…`, `xf/b/…` and `xf/m`. It carries no data. The fee does not change.
The marker is internal to the library: no reader and no other component needs to know it.

### 4.2 Finding this library's tickets

`ILedgerSource` gains one method listing an account's live tickets, each with the hash of the
transaction that created it, read from `account_objects` with `type: ticket` and followed through
its `marker` until the list is complete. `FakeLedgerSource` implements it to the same contract, and
answers `tefNO_TICKET` for a consumed ticket the way a node does: a fake that is more forgiving
than the node is how part C3's critical defect stayed hidden.

Deciding which listed tickets are this library's is a separate step: group them by creator, look
each creating transaction up once, and keep the tickets whose creator is a `TicketCreate` carrying
the marker. One lookup per creator, so one per batch of up to 250 tickets.

### 4.3 Picking up

The first time a `TicketStrategy` instance needs tickets, before it mints anything, it finds this
library's live tickets and puts them in its queue. The mint then covers only the shortfall. This
happens once per instance (I4). A publication makes up to three passes through one instance —
chunks, block list, manifest — and the one that picks up is whichever first needs a ticket. On a
resume that is not always the chunks: if every chunk landed before the interruption, the writer
skips that pass without calling the strategy at all, and the block list is the first to ask.

### 4.4 A ticket that is gone

`SendOneAsync` recognises `tefNO_TICKET` and reports the ticket as gone rather than intact. The
batch loop drops it from its bookkeeping and puts the memo back in the pending list without
counting that attempt (I5). Every other result keeps the classification it has today.

### 4.5 Tidying up

`ISubmissionStrategy` gains one member that spends this library's live tickets, each on an
`AccountSet` that sets nothing, and reports how many it released and how many it could not. Both
strategies implement it; there is no default implementation, so a strategy written later has to
decide what it does with leftovers.

`SequenceStrategy` today has no ledger source, and finding tickets needs one, so it gains one.
`PublishPolicy`, which builds both strategies, already holds the connection that provides it.

`PublicationSession` calls it after the writer returns successfully, on a fresh run and on a resume
alike. A failure is caught and recorded on `PublicationReport` as an init-only property, the shape
`StateSaveFailure` already established, so the record's positional constructor does not change.
A ticket the tidy-up cannot spend because another of its transactions is still queued on it is
left alone and counted as not released.

### 4.6 What this adds to the public surface

`XrplFile.Core` has not been released, but its public surface becomes a commitment once it is, so
the additions are listed here: one method on `ILedgerSource` and the record it returns; one member
on `ISubmissionStrategy`; one constructor parameter on `SequenceStrategy`; one init-only property
on `PublicationReport`. Every implementation of the two interfaces in this repository, test doubles
included, gains the new member.

## 5. The estimate

The peak reserve becomes the reserve for tickets beyond those the account already holds:

```
PeakReserve = max(0, min(memos, MaxLiveTickets) − liveTickets) × OwnerReserve
```

`liveTickets` is `AccountState.TicketCount`, read from the same answer as the balance and the owner
count, so the three numbers still describe one moment. Spendable is unchanged: it already
subtracts the reserve of every object the account owns, live tickets included.

The count of mints is left as it is. The estimate goes on assuming every pass mints from an empty
queue, which overstates the fee by at most a mint or two when tickets are picked up. That keeps it
a worst case, which is what it is named.

The worst-case fee gains the tidy-up: at most `MaxLiveTickets` transactions (I10). The displayed
transaction count does not change, because in a run that is not interrupted the tidy-up spends
nothing.

## 6. The application

### 6.1 The close confirmation

The sentence moves out of the window's close handler into a model (I11). `RunSnapshot` gains the
two facts that model needs and the handler does not have: whether the run uses tickets, and the
owner reserve per ticket, both already on the estimate the run was approved against.

For a ticketed run:

> "big.bin" is still publishing. Closing now stops the run; it can be resumed later. Until it is,
> the tickets it reserved stay on this account, each holding 2,000,000 drops of owner reserve —
> resuming spends them. Close anyway?

For a run without tickets the sentence stays as it is today.

### 6.2 The unfinished-runs list

The list already warns that deleting a run does not undo what it spent. When any listed run uses
tickets — decided by the same threshold `PublishPolicy` applies, not a copy of it — it adds:

> Deleting a run leaves the tickets it reserved on this account, each holding an owner reserve,
> until a later publication from it uses them or the account is closed.

That sentence is true because of I7: every successful publication tidies up.

## 7. Testing

Against the fake ledger, on Linux, as the rest of the library and the application models are:
only this library's tickets are picked up and a foreign one is left alone; a ticket whose creator
cannot be shown is left alone; the mint covers only the shortfall; `tefNO_TICKET` drops the ticket
without costing the memo an attempt and the memo still lands; the tidy-up spends this library's
leftovers and nothing else; a tidy-up that fails leaves the publication successful and says so on
the report; the estimate's peak reserve subtracts live tickets; the close sentence and the
unfinished-runs sentence each appear for a ticketed run and not for one without tickets.

Against the standalone stand, one integration test does what section 1 did by hand: start a
ticketed publication, cancel it part-way, give the account one ticket of its own minted without the
marker, resume with a fresh strategy, and check that the publication finished, that **no ticket
carrying the marker is left on the account**, and that the foreign ticket is still there.

## 8. What this does not do

It does not recover tickets minted before this change: they carry no mark, and leaving them is the
price of never touching a ticket that may not be ours. They still go when the account is deleted.

It does not add `LastLedgerSequence` to submissions. With `tefNO_TICKET` handled, a straggler from a
stopped run landing on a picked-up ticket costs nothing, so expiry is not needed for safety here.

It does not support two writers publishing from one account at the same time. That was never
supported; what changes is that their collisions now cost nothing.
