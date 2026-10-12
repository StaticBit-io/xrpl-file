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

Four further facts shape the design, each checked rather than assumed:

- **A ticket can be traced to the transaction that created it.** Every Ticket ledger entry carries
  `PreviousTxnID`, and a ticket is created only by `TicketCreate` and is not modified between
  creation and use, so that field names its creator. A `TicketCreate` carrying a memo is accepted
  by the node, and on the stand two tickets were traced to a marked mint and a third to an unmarked
  one.
- **A spent ticket answers `tefNO_TICKET`.** Measured on xrpld 3.3.0: a transaction riding a ticket
  that another transaction already consumed is refused with `tefNO_TICKET`, "Ticket is not in
  ledger". A `tef` result is never applied, so that refusal claims no fee. A ticket that does not
  exist yet answers `terPRE_TICKET`.
- **The strategy mishandles that answer today.** `SendOneAsync` treats every `tef` as "never
  reached a ledger, so the ticket is intact", and the ticket goes back into the queue. A ticket that
  is gone then circulates for ever, costing each memo that draws it an attempt. The path is already
  reachable in a narrow case — a submission the node accepted but whose answer was lost, so the
  strategy returned a ticket that its own transaction then consumed — and picking up abandoned
  tickets widens it: the stopped run's submissions carry no `LastLedgerSequence`, so one may still
  be in the node's queue and land on a ticket the resumed run has just picked up.
- **A landed memo is recognised by its marker alone.** `TicketStrategy.SweepAsync` matches the
  account's transactions on `MemoTypeHex` and nothing else, although chunk markers carry no
  publication scope: `xf/c/0` is chunk 0 of every file the account has published. A straggler from
  a stopped run of a different file, landing inside a later publication's sweep window, would be
  recorded as that publication's chunk. The writer's read-back, on by default, compares every block
  against the file before the manifest is written, so the publication fails with `BlockMismatch`
  rather than producing a dead link — but it fails, and its fees are spent.

## 2. Scope

**In.** Marking the tickets this library mints. Picking up this library's live tickets when a
strategy needs tickets. Treating `tefNO_TICKET` as a ticket that is gone. Spending this library's
leftover tickets after a publication succeeds. Recognising a landed memo by its data as well as its
marker. Making the estimate agree with all of that. Saying what an interruption leaves behind, in
the window-close confirmation and in the unfinished-runs list.

**Out.** Tickets the account holds for any other purpose, which this design never touches.
Tickets minted before this change, which carry no mark and so cannot be told apart from anybody
else's. `LastLedgerSequence` on submissions. Running two writers on one account at once.

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| I1 | Tickets are found on the ledger when a strategy needs them, not recorded per run | Leftovers belong to the account, not to a run. Recording them in the state file would help only a resume of the same run, change that file's format, and leave every other run's leftovers locked |
| I2 | Only this library's tickets are ever picked up or spent, identified by a mark on the `TicketCreate` that made them | The documented use of tickets is a transaction prepared in advance for signatures that arrive later. Spending somebody's reserved ticket would quietly invalidate a multi-signed transaction they are still assembling, and an imported key may belong to exactly such an account |
| I3 | A ticket whose creator the node cannot show is treated as somebody else's | The safe failure is to leave a ticket alone. A node with truncated history cannot prove a ticket is ours, and the cost of being wrong is somebody else's transaction |
| I4 | A strategy picks up only marked tickets it does not already know, and looks again whenever the ledger holds more live tickets than it accounts for | Looking once is not enough: a stopped run's mint can validate after the new strategy looked, and its tickets would then take slots under the protocol's ceiling without ever entering the queue. Excluding what the instance already holds, has dispatched, or has found gone keeps a second look from dispatching a ticket twice |
| I5 | `tefNO_TICKET` removes the ticket, does not count against the memo, and the instance does not pick that ticket up again | The ticket is gone, so returning it to the queue loops, and the memo did nothing wrong — three such tickets in a row would otherwise fail a publication on which nothing was spent. A ticket consumed in the open ledger by a transaction that never validates is in fact still live; it is left for the tidy-up rather than retried, so the strategy cannot cycle on it |
| I6 | After a publication succeeds, this library's live tickets are spent on transactions that do nothing | Picking up spends only as many as the remaining memos need. A run interrupted late — during the block list, say — would pick up a hundred tickets, spend five and leave the rest. Spending one costs one transaction fee and frees one owner reserve, several thousand times the fee on any network this library addresses |
| I7 | Both strategies tidy up, not only the ticketed one | A small file published after an interrupted large one should clear that one's leftovers too; nothing about the file's size makes the leftovers less locked |
| I8 | Tidying up cannot fail a publication, and is bounded in time | The manifest is in the ledger by then and the link works. A tidy-up that fails, or does not finish in time, is reported on the publication report, the way a state file that could not be saved already is; it never holds the report back |
| I9 | The estimate counts only this library's live tickets as reusable; any other live ticket takes a slot and replaces nothing | Every live ticket is under the same ceiling of 250, but only ours can stand in for a ticket the run would otherwise mint. Counting foreign or unmarked tickets as reusable would let a run be approved whose mints the account cannot afford |
| I10 | The estimate's worst-case fee includes the tidy-up | The approval is meant to cover what the run spends. The tidy-up is at most the protocol's ceiling of live tickets, a few thousand drops at the fees this library uses |
| I11 | Which sentence the close confirmation shows is a model's decision | Whether to mention tickets depends on whether the run uses them, and C1's E2 puts every decision where a test on Linux can reach it. The confirmation is assembled today inside the window's close handler, which no test can run |
| I12 | A landed memo is matched on its data as well as its marker | The marker names an index, not a file. Matching the data too means a straggler from another file's publication is ignored instead of being recorded and failing the read-back |

## 4. The library

### 4.1 Marking

Every `TicketCreate` this library submits carries one memo whose type is a new marker, `xf/t`,
alongside the existing `xf/c/…`, `xf/b/…` and `xf/m`. It carries no data. The fee does not change.
The marker is internal to the library: no reader and no other component needs to know it.

### 4.2 Finding this library's tickets

`ILedgerSource` gains one method listing an account's live tickets from the validated ledger, each
with the hash of the transaction that created it, read from `account_objects` with `type: ticket`
and followed through its `marker` until the list is complete. `FakeLedgerSource` implements it to
the same contract, and answers `tefNO_TICKET` for a consumed ticket the way a node does: a fake
that is more forgiving than the node is how part C3's critical defect stayed hidden.

Deciding which listed tickets are this library's is a separate step, shared by the strategy, the
tidy-up and the estimate: group the tickets by creator, look each creating transaction up once,
and keep the tickets whose creator carries the marker. One lookup per creator, so one per mint of
up to 250 tickets. A creator the node cannot return makes its tickets foreign (I3).

### 4.3 Picking up

Before a `TicketStrategy` instance mints, it compares the ledger's count of live tickets with the
tickets it accounts for. When the ledger holds more, it finds this library's tickets and takes into
its queue those it does not already hold, has not dispatched, and has not found gone (I4). The mint
then covers only the shortfall.

On the instance's first need this always runs, since a new instance accounts for nothing. A
publication makes up to three passes through one instance — chunks, block list, manifest — and the
first to need a ticket is not always the chunks: on a resume where every chunk had already landed,
the writer skips that pass without calling the strategy at all, and the block list asks first.

### 4.4 A ticket that is gone

`SendOneAsync` recognises `tefNO_TICKET` and reports the ticket as gone rather than intact. The
batch loop drops the ticket from its bookkeeping, records it as gone so that a later look does not
pick it up again (I5), and puts the memo back in the pending list without counting that attempt.
Every other result keeps the classification it has today.

This also settles the narrow case that exists before this change: a submission that threw after
the node had accepted it returns its ticket to the queue, and when that ticket is drawn again its
own first transaction has consumed it. The second submission now meets `tefNO_TICKET` and the
ticket leaves the queue, where before it would have circulated. The duplicate work in that case —
the memo sent twice, on two tickets — is unchanged and is paid for as any retry is.

### 4.5 Matching a landed memo

`SweepAsync` matches a transaction against a memo of the current call only when both its
`MemoTypeHex` and its `MemoDataHex` equal that memo's (I12). `LedgerTransaction` already carries
both. A transaction with the right marker and other data is neither recorded as landed nor counted
as seen.

### 4.6 Tidying up

`ISubmissionStrategy` gains one member that spends this library's live tickets, each on an
`AccountSet` that sets nothing. Both strategies implement it; there is no default implementation,
so a strategy written later has to decide what it does with leftovers. `SequenceStrategy` today has
no ledger source, and finding tickets needs one, so it gains one; `PublishPolicy`, which builds
both strategies, already holds the connection that provides it. The parameter is optional, last in
the constructor: some seventy existing call sites, nearly all tests, build a sequence strategy with
no ledger at hand, and one built that way finds no tickets and releases none. The application
always passes it; the README says what leaving it out costs.

It finds this library's tickets afresh from the validated ledger, submits one `AccountSet` on each,
and waits for the results to validate. A spend the node refused for a passing reason before any ledger
saw it — a `tel`, or a `ter` other than `terQUEUED` — left its ticket intact and claimed no fee, so
it is sent again in the next round, up to five times in all, while time remains (a `tem` would be
refused the same way every time and is not sent again): a burst of up to 250
spends is exactly what a loaded node answers with `telCAN_NOT_QUEUE` and its kin, and giving up on
the first refusal would leave most of an interrupted run's tickets locked. A round in which the
node could not be read is skipped rather than ending the tidy-up and losing what it had settled.
Each ticket ends in exactly one of three counts:

- **released** — its `AccountSet` validated with `tesSUCCESS`. Nothing short of validation counts.
- **already gone** — the ticket was no longer listed when the results were checked, and the
  tidy-up's own `AccountSet` did not validate with `tesSUCCESS`. Some other transaction consumed it;
  the reserve is free either way. A `tefNO_TICKET` answer alone is not enough: a ticket consumed in
  an open ledger by a transaction that never validates is still live (I5), and while the validated
  ledger lists it, it counts as not released.
- **not released** — any other outcome, or no outcome before the tidy-up's time ran out.

Whether a ticket is held by a transaction still in the node's queue is not predicted; it is
whatever the outcome shows.

`PublicationSession` runs it after the writer returns successfully, on a fresh run and on a resume
alike, with its own deadline of two minutes linked to the publication's cancellation token. A
cancellation or the deadline ends the tidy-up and leaves its counts as they stand; an exception is
caught. Neither fails the publication (I8). The three counts, and the exception if there was one,
are recorded on `PublicationReport` as init-only properties — the shape `StateSaveFailure` already
established — so the record's positional constructor does not change.

### 4.7 What this adds to the public surface

`XrplFile.Core` has not been released, but its public surface becomes a commitment once it is, so
the additions are listed here: one method on `ILedgerSource` and the record it returns; one member
on `ISubmissionStrategy` and the record it returns; one optional constructor parameter on
`SequenceStrategy`; init-only properties on `PublicationReport`; `SessionOptions.TicketReleaseTimeout`,
the tidy-up's deadline; two init-only properties on `PublicationEstimate` — the reusable tickets and
the free slots of section 5 — and `PublicationEstimator.RequireTicketSlot`, the refusal that goes
with them. Every implementation of the two interfaces in this repository, test doubles included,
gains the new member.

## 5. The estimate

The estimate finds this library's live tickets the same way the strategy does (4.2), from one
listing, and derives two numbers from it: `ours`, the marked tickets, and `others`, every other
live ticket. Then:

```
slots       = MaxLiveTickets − others
peakLive    = min(memos, slots)
toMint      = max(0, peakLive − ours)
PeakReserve = toMint × OwnerReserve
```

When the run uses tickets and `slots` is zero, there is nowhere to mint and nothing to reuse, and
the estimate refuses with that reason instead of approving a run that would stop at its first
batch. Spendable is unchanged: it already subtracts the reserve of every object the account owns,
live tickets included.

The count of mints is left as it is. The estimate goes on assuming every pass mints from an empty
queue in batches of the configured size. That overstates it when tickets are picked up, and
understates it when few slots are free and the run has to mint in more, smaller batches; neither
error is large next to the worst-case fee, which multiplies every transaction by the attempts
allowed and adds the tidy-up, and which is what the approval is checked against.

The worst-case fee gains the tidy-up: at most `MaxLiveTickets` transactions (I10). The displayed
transaction count is the publication's own and does not include the tidy-up. Any publication may
tidy up — an uninterrupted one included, when an earlier run left tickets behind — so the
tidy-up's cost is covered by the worst case rather than shown as part of the count.

## 6. The application

### 6.1 The close confirmation

The sentence moves out of the window's close handler into a model (I11). `RunSnapshot` gains the
two facts that model needs and the handler does not have: whether the run uses tickets, and the
owner reserve per ticket, both already on the estimate the run was approved against.

For a ticketed run:

> "big.bin" is still publishing. Closing now stops the run; it can be resumed later. The tickets it
> reserved stay on this account, each holding 2,000,000 drops of owner reserve, until a later
> publication from this account spends them or the account is closed. Close anyway?

For a run without tickets the sentence stays as it is today.

The sentence names the only two ways those tickets ever go, and promises neither: a later
publication tries, and may not manage — the node may lack the history to prove they are ours, or
its tidy-up may run out of time — while closing the account always removes them.

### 6.2 The unfinished-runs list

The list already warns that deleting a run does not undo what it spent. When any listed run uses
tickets — decided by the same threshold `PublishPolicy` applies, not a copy of it — it adds:

> Deleting a run leaves the tickets it reserved on this account, each holding an owner reserve,
> until a later publication from this account spends them or the account is closed.

## 7. Testing

Against the fake ledger, on Linux, as the rest of the library and the application models are:

- only marked tickets are picked up; a foreign ticket and a ticket whose creator cannot be shown
  are left alone;
- the mint covers only the shortfall;
- a mint that validates after the strategy first looked is picked up on the next need, and a ticket
  already held or dispatched is never picked up twice;
- `tefNO_TICKET` drops the ticket without costing the memo an attempt, the memo still lands, and
  that ticket is not picked up again by the same instance;
- a transaction carrying the right marker and different data is neither recorded nor seen;
- the tidy-up counts a ticket as released only on a validated `tesSUCCESS`, as already gone only
  once the ledger no longer lists it, and as not released otherwise — a `tefNO_TICKET` answer for a
  ticket still listed included; it leaves foreign tickets alone; it stops at its
  deadline; a tidy-up that throws leaves the publication successful and says so on the report;
- the estimate subtracts only this library's tickets, treats every other live ticket as an occupied
  slot, and refuses a ticketed run when no slot is left;
- the close sentence and the unfinished-runs sentence each appear for a ticketed run and not for
  one without tickets.

Against the standalone stand, one integration test does what section 1 did by hand: start a
ticketed publication, cancel it part-way, give the account one ticket of its own minted without the
marker, resume with a fresh strategy, and check that the publication finished, that **no ticket
carrying the marker is left on the account**, that the report counts every one released or already
gone, and that the foreign ticket is still there.

## 8. What this does not do

It does not recover tickets minted before this change: they carry no mark, and leaving them is the
price of never touching a ticket that may not be ours. They still go when the account is deleted.

It does not add `LastLedgerSequence` to submissions. Stragglers from a stopped run can therefore
still land later. What this design settles is that such a straggler cannot be mistaken for a memo
of a different file (I12), and that a ticket it consumes leaves the queue instead of circulating
(I5). What it does not make free is the retry: a memo whose ticket was taken is sent again on
another one, and that costs a fee. The losing side of a collision on a ticket that is already
consumed claims no fee, as measured; whether two transactions contending for a ticket that is still
live always resolve the same way in the node's queue is not something this design relies on.

It does not support two writers publishing from one account at the same time. That was never
supported.

It does not tell the window apart while the tidy-up runs. The tidy-up happens after the manifest,
inside the run, so for up to its deadline the run still counts as publishing, and closing the window
then shows the ticketed close sentence, whose "it can be resumed later" is no longer true: the file
is finished. Closing at that moment costs nothing beyond what the sentence says — the tickets the
tidy-up had not spent stay, and the next publication spends them — so the sentence is left as it is
rather than given a third form for a two-minute window.

The session refuses a ticketed run with no free slot by checking whether the strategy it was given
is a `TicketStrategy`. A strategy that wraps one is not recognised and is not refused there; it
stops at its first batch instead, as it does today. The application builds its strategies
undecorated, and its estimate screen applies the same refusal by the memo count.

It does not address accounts that chain prepared transactions with `AccountTxnID`. Every
transaction a publication sends already changes that account's last transaction, the tidy-up
included; this is a property of publishing from such an account at all, not of this change.
