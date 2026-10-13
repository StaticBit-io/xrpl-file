# Leftover tickets — design

A ticketed publication that is interrupted leaves tickets on its account, and each one locks an
owner reserve. Nothing in the library or the application spends them again, and nothing on screen
says they are there. This document fixes both: a publication picks up the tickets this library
left behind, spends any it does not need once it finishes, and the application says plainly what
an interruption costs until then.

**Amended the same day for loaded public nodes** (decisions I13–I18). The tidy-up was proven on a
standalone node, whose admin connection is never rate-limited or overloaded. The amendment covers
what a public node does instead — answers that it is busy, warns a client that nears its rate limit,
drops a queued transaction, closes the socket — and makes the report say what the tidy-up cost.
Sections 2, 3, 4.6, 4.7, 4.8, 4.9, 5, 7 and 8 carry the changes.

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
else's. `LastLedgerSequence` on memo submissions; the tidy-up's own spends carry one (I16). Running
two writers on one account at once.

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
| I13 | A node's error answer to `submit` is an outcome, `error:<code>`, whichever shape it arrives in; the tidy-up sends `tooBusy`, `notSynced`, `noCurrent`, `noNetwork` and `noClosed` again like a passing `tel` | Against a real node the SDK throws `RippledException` for a `status: error` answer, and only the sink's scripted seam ever produced `error:<code>`. A real `tooBusy` therefore escaped the tidy-up's parallel loop and ended the whole tidy-up — the seam was more forgiving than the node. The five codes are the universal errors that name a passing server condition: the universal errors page names `noCurrent` and `noNetwork`, but rippled folds them and `noClosed` into `notSynced` for an API v2 client, which this library's client is, so `notSynced` is what a node that has lost sync answers it with, and the other three come from API v1 nodes; an error answer is never applied, so resending it costs no fee |
| I14 | A transport failure — a closed or never-made connection, a dropped socket — is "the node did not answer": on `submit` it is handled exactly as a timeout already is, on a read it is `NodeUnreachable` as other failed reads already are. A signer's failure and the caller's own cancellation are not transport failures | The SDK rejects requests in flight with `DisconnectedException` when the server closes the socket (1008 "threshold exceeded" included) and with an `OperationCanceledException` no caller asked for when the network drops. The first escaped the sink raw; the second escaped the sink and the source both, and reached the application as a cancellation the person never made. The tidy-up skips a round only on `NodeUnreachable`, so neither could be survived |
| I15 | The connection classes count the node's `"warning": "load"`; the tidy-up halves its parallelism and doubles its pause after any round that showed strain, and does not speed up again | The warning is the only notice a public node gives before it disconnects a client and refuses its IP for a while — which would cost the person their next action, not only the tidy-up. rippled attaches it at most once a second to whichever answer is next, so every answer has to be watched, not only the submits. The tidy-up's own traffic is small; a warning means load already on the IP, which lasts the decay window, so staying slow for the rest of a two-minute tidy-up is the cheap side of the trade |
| I16 | Every tidy-up spend carries `LastLedgerSequence` ten ledgers past a validated index read no more than ten seconds before the spend is sent; a spend whose ticket is still listed once that ledger has validated is sent again, and `tefMAX_LEDGER` is a passing refusal | Without it a spend accepted as `terQUEUED` that later drops out of the queue holds its ticket in "waiting" until the deadline, and nothing short of the deadline can tell. With it, a spend is expired when the last validated index a round read before its listing is at least its `LastLedgerSequence` and that listing still shows the ticket (4.6): the listing is read after the index, so the ticket was consumed by nothing up to that ledger, and no later ledger can include the spend — whichever index the spend's own deadline was taken from. An expired spend never applied and never will, so the ticket is free to send again. The index is read again inside a round once it is ten seconds old because a throttled round outlasts ten ledgers: a deadline taken from the round's first read would send its later spends already expired. Memo submissions stay without one (section 8): a spend carries no data and its only job is to consume a ticket, so re-signing it is free of the bookkeeping a memo's retry would need |
| I17 | The tidy-up counts, per ticket, whether a spend certainly claimed a fee or may have; the report prices both, and says "unknown" when the tidy-up failed | The approval covered the tidy-up's worst case (I10), but the report showed none of what it actually spent. Only one transaction can consume a ticket, so each ticket costs at most one fee however often its spend was sent. A submission is in doubt until something proves it can never apply — a refusal, its ticket consumed by something else, its expiry — because a spend still unsettled at the deadline, cancelled mid-send, or refused only after an unanswered transmission of the same blob may still land; a tidy-up that threw took its counts with it, so zero would be a false figure |
| I18 | On a fresh run the report counts the mints the strategy made during that session; on a resume it keeps the estimate's | Picked-up tickets mean fewer mints than the estimate assumed, and a fresh run's session is its whole publication, so the strategy's own count is the publication's. A strategy may be handed to more than one session, so the session counts the difference between what the strategy reports before and after its run, not the strategy's lifetime total. A resume's earlier sessions minted too, and nothing this session can read records how many, so its figure stays the estimate's, as the report already says |

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
and waits for the results to validate. It works in rounds: read the validated ledger's index, send
every spend still to be sent, list the account's tickets once, settle what the listing decides,
pause.

Every spend carries `LastLedgerSequence` set ten ledgers past the validated index the tidy-up last
read, and that read is never more than ten seconds old when the spend is sent (I16). Ten ledgers,
because the node's queue refuses a transaction whose `LastLedgerSequence` is less than two past the
open ledger, the four xrpl.org suggests leaves a queued spend little room under load, and the twenty
some libraries fill in would give a spend only two chances inside the two-minute deadline; ten,
some thirty-five seconds, gives it three.

**A fresh deadline for every spend.** The index the round read at its start goes stale while the
round sends. After strained rounds a round has three spends in flight, or one (back-off, below), and
a loaded node takes 0.2 to 1 second to answer each, longer when a submission waits out unanswered
transmissions (4.8): a round of up to 250 spends then outlasts the ten ledgers' window. Every spend
sent after the window closed would carry a deadline that has already passed — refused
`tefMAX_LEDGER`, one more request against an IP the node has already warned, one of its ticket's
five submissions burnt — and since a round sends in ticket order, it would always be the same tail
of tickets, until their submissions ran out and they ended not released. So before each spend is
sent, the round reads the validated index again when the last read is ten seconds old or older, and
that spend and the ones after it take their deadline from the new index. Ten seconds is about three
ledgers at the usual three to four seconds a ledger, and leaves at least half the window even at two
seconds a ledger, well above the queue's floor. Spends in flight at once share one re-read rather
than each making its own; at one spend in flight answering in a second, that is one `ledger`
request per ten spends. A re-read the node does not answer sends nothing more that round: the spends
not yet sent stay to be sent in the next round, and the round shows strain (back-off, below).

The other remedy considered was capping a round at a multiple of its spends in flight, so that each
round's start reads a fresh index. It bounds a round by count, not by time, so a node slow to answer
— or one submission waiting out three unanswered transmissions — still outlasts it; and every extra
round adds a listing, the costliest request the tidy-up makes, and a pause of up to sixteen polls.

A **submission** here is one call to the sink: one spend, signed once, carrying one
`LastLedgerSequence`. The sink may put that identical blob on the wire up to three times inside one
call when the node does not answer (4.8); those are transmissions of one submission, not further
submissions. Each ticket gets at most five submissions.

A spend the node refused for a passing reason before any ledger saw it left its ticket intact and
claimed no fee, so it is sent again in the next round, while submissions and time remain. The
passing reasons are a `tel`; a `ter` other than `terQUEUED`; `tefMAX_LEDGER`, which the node checks
only after it has found the ticket live; and the error answers `error:tooBusy`, `error:notSynced` (what an
API v2 client receives for a node that is not synced) and, from API v1 nodes, `error:noCurrent`,
`error:noNetwork` and `error:noClosed` (I13). A burst of up to 250 spends is exactly what a loaded node answers with
`telCAN_NOT_QUEUE` and its kin, and giving up on the first refusal would leave most of an
interrupted run's tickets locked. A `tem`, any other `tef` and any other error answer would be
refused the same way every time and are not sent again.

A spend the node took — `tes`, `tec` or `terQUEUED` — waits for the listing. So does a spend whose
refusal followed an unanswered transmission of the same blob (4.8), whatever the refusal: the
earlier copy may still be in the node, so sending a new spend would only compete with it, and the
listing or the spend's expiry will say which it was. When a later round's listing no longer shows
the ticket of a waiting spend, that spend's own transaction is asked what it did.

**Expiry** (I16). One predicate decides it everywhere: a spend is **expired** when the last
validated index a round read before its listing — the read at its start, or a later re-read while it
sent — is at least the spend's `LastLedgerSequence` and that round's listing still shows its ticket.
The index is read before the listing, so the listing comes from that ledger or a later one; no
ledger after the `LastLedgerSequence` can include the spend, and none up to it did, or the ticket
would be gone. The argument uses nothing about where the spend's own `LastLedgerSequence` came
from, so it holds alike for a spend that took its deadline from the round's first read, from a
re-read, or from an earlier round. An expired spend never applied and cannot newly apply. A waiting
spend that expires sends its ticket back to be sent again with a fresh `LastLedgerSequence`, within
the same five submissions. Expiry says only that; it says nothing about a ticket the listing no
longer shows. The predicate assumes the index and the listing come from one node's consistent
view; a connection that switches to a lagging backend between the two reads could judge a spend
expired early, and nothing here guards against it.

A round in which the node could not be read is skipped rather than ending the tidy-up and losing
what it had settled: when the index cannot be read nothing is sent that round, since no
`LastLedgerSequence` can be set — and when a re-read cannot be read, nothing more — and when the
listing cannot be read nothing is settled. The census
that starts the tidy-up is retried the same way, round after round, until it is read or time runs
out: one dropped answer before anything was sent is no reason to leave every ticket locked. A spend
whose submission ended in `NodeUnreachable` counts as a submission and is sent again.

Each submission's answer is recorded against its ticket as soon as it arrives, not when the whole
round's sends have finished, so a cancellation part-way through a round keeps every answer already
given; a submission cancelled before it answered is treated as unanswered.

**Back-off** (I15). A round **shows strain** when the sum of the sink's and the source's
`LoadWarnings` is higher after the round than before it (4.8), when any spend was answered with one
of the five passing error answers, or when anything in the round — a submission, the index read,
the listing, a lookup — ended in `NodeUnreachable`. After such a round:

```
inFlight = max(1, inFlight / 2)          integer division: 25, 12, 6, 3, 1, 1, ...
pause    = min(16 × poll, pause × 2)     poll is the strategy's validation poll
```

starting from 25 and `poll` — one second to sixteen with the defaults. A round without strain
changes neither, and nothing speeds the tidy-up up again. The adjustment is made between rounds;
the lookups a round's settling makes are sequential and are not paced. The tidy-up's own traffic
is small next to the threshold: a round of 250 submissions and 250 lookups at 20 units each, plus a
listing at 400, adds about 10,400 units, which rippled divides over its 32-second window into a
balance near 325 against a warning at 5,000. A warning therefore means load already on the
client's IP — the publication's own sweeps of `account_tx` at 400 units each, or neighbours behind
one address — and that load decays over the same window, so the rest of a two-minute tidy-up is
better spent slow than disconnected. A tidy-up that slows down too far for its deadline reports the
rest as not released, which a later publication picks up again.

Each ticket ends in exactly one of three counts:

- **released** — its own `AccountSet` was looked up and validated with `tesSUCCESS`. Nothing short
  of validation counts.
- **already gone** — the ticket was no longer listed when the results were checked, and no spend of
  the tidy-up's was confirmed to have validated with `tesSUCCESS`. Who consumed it is not claimed:
  the tidy-up's own spend validating with a `tec`, a straggler, or a spend of its own whose answer
  was lost. The reserve is free either way. A `tefNO_TICKET` answer alone is not enough: a ticket
  consumed in an open ledger by a transaction that never validates is still live (I5), and while the
  validated ledger lists it, it counts as not released.
- **not released** — any other outcome, or no outcome before the tidy-up's time ran out.

Whether a ticket is held by a transaction still in the node's queue is not predicted; it is
whatever the outcome shows. Every spend carries a `LastLedgerSequence`, so no spend of the tidy-up's
can newly apply more than ten ledgers after its last submission.

Beside the three counts the tidy-up says what its spends cost (I17), in two more counts, kept per
ticket across all its submissions:

- **charged** — one of its spends was looked up and validated, with `tesSUCCESS` or a `tec`: a fee
  was claimed. Every released ticket is charged; an already-gone ticket is charged when its own
  spend's `tec` consumed it.
- **in doubt** — not charged, and at least one of its submissions is still **open**. A submission
  opens when it is sent and closes in one of three ways: the node answered a refusal (a passing one
  or not) that is not marked as following an unanswered transmission of the same blob (4.8); its
  hash was looked up and found not validated after its ticket left the listing — the ticket was
  consumed in a validated ledger by something else, so it can never apply; or it expired. A lookup
  that finds nothing closes nothing: a node that cannot show the transaction — a lagging backend
  after a reconnect, truncated history — has not shown that it did not apply. Every
  other submission stays open: the node took it and it has not settled, its refusal followed an
  unanswered transmission and it has not settled — both are waiting spends, with a known hash — or
  it ended in `NodeUnreachable` or was cancelled before it answered, and has no known hash. An open
  submission may still apply, and claim its fee, until its `LastLedgerSequence` passes. A waiting
  spend is looked up when its ticket leaves the listing, so only a submission without a known hash
  leaves the tidy-up unable to tell whether it or something else consumed an already-gone ticket.

The three outcome counts partition the tickets found. Charged and in doubt are disjoint; every
charged ticket is released or already gone, and no released ticket is in doubt. Only one transaction
can consume a ticket, so each ticket is charged at most one fee however often its spend was sent,
and a ticket whose every submission closed on a refusal costs nothing.

`PublicationSession` runs it after the writer returns successfully, on a fresh run and on a resume
alike, with its own deadline of two minutes linked to the publication's cancellation token. A
cancellation or the deadline ends the tidy-up and leaves its counts as they stand; an exception is
caught. Neither fails the publication (I8). The three counts, and the exception if there was one,
are recorded on `PublicationReport` as init-only properties — the shape `StateSaveFailure` already
established — so the record's positional constructor does not change. The cost is recorded the same
way, in drops (4.9).

### 4.7 What this adds to the public surface

`XrplFile.Core` has not been released, but its public surface becomes a commitment once it is, so
the additions are listed here: one method on `ILedgerSource` and the record it returns; one member
on `ISubmissionStrategy` and the record it returns; one optional constructor parameter on
`SequenceStrategy`; init-only properties on `PublicationReport`; `SessionOptions.TicketReleaseTimeout`,
the tidy-up's deadline; two init-only properties on `PublicationEstimate` — the reusable tickets and
the free slots of section 5 — and `PublicationEstimator.RequireTicketSlot`, the refusal that goes
with them. Every implementation of the two interfaces in this repository, test doubles included,
gains the new member.

The amendment adds: `LoadWarnings` on `ILedgerSink` and on `ILedgerSource`, each with a default of
zero (4.8); `TicketMints` on `ISubmissionStrategy`, with no default, for the reason the tidy-up
member has none — a strategy written later has to say what it minted (4.9); two init-only counts on
`TicketRelease`, `Charged` and `InDoubt` (4.6); one init-only property on `SubmitOutcome`,
`FollowsUnansweredAttempt` (4.8); and two init-only properties on `PublicationReport`,
`TicketReleaseFeeDrops` and `TicketReleaseFeeDropsInDoubt` (4.9).

### 4.8 What the connection reports

`XrplClientLedgerSink.SubmitAsync` keeps its contract — it returns what the node said — and now
keeps it against a real node too.

**Error answers** (I13). A node that answers `submit` with an error answers with
`status: error`, and the SDK raises that as `RippledException` before the sink sees any JSON. The
sink turns it into `SubmitOutcome("error:<code>", hash)`, the same outcome its scripted seam already
produced for an embedded `error`, so both shapes agree the way `XrplClientLedgerSource` already makes
them agree for an absent account. The callers that read a submit's outcome see this too:

- the tidy-up resends the five passing codes (4.6);
- `TicketStrategy` treats any error answer as a refusal that left the ticket intact — its
  classification for every answer it does not name — and so retries the memo within
  `MaxAttemptsPerMemo`, where before the raw exception failed the publication at once; a mint
  answered that way is refused like any other refused mint;
- `SequenceStrategy` and `ThrowawayAccount` refuse with `SubmitRefused` carrying `error:<code>`,
  where before the raw `RippledException` escaped them. `CloseAccountModel` therefore shows its
  refusal message for it instead of letting an exception it does not catch escape. That message
  says "Nothing was charged" for any answer but a `tec`; when the refusal followed an unanswered
  transmission of the same blob, that sentence was already as uncertain for a `tel` before this
  amendment, and section 8 leaves it so.

**Transport failures** (I14). `DisconnectedException`, `NotConnectedException`,
`ConnectionException`, either `TimeoutException`, and an `OperationCanceledException` the caller's
token did not cause, are all "the node did not answer". The sink handles them as it already handles
a timeout: it looks the hash up, resubmits the identical blob while attempts remain, and after three
attempts throws `NodeUnreachable`. The lookup treats the same failures, and any error answer the
node gives to `tx` in either shape, as "not known yet" — a busy node's `tooBusy` to the lookup says
nothing about the transaction, and reading it as anything else would end the call or invent a
`terQUEUED`. The
signature is made before any of this and is outside it, so a signer that refuses — a locked vault
above all — still propagates its own exception; the caller's own cancellation still propagates as a
cancellation. `XrplClientLedgerSource` already turns every SDK failure but a cancellation into
`NodeUnreachable`; it now does the same for a cancellation its caller's token did not cause.

That last case changes what the publication path reports. The writer names connection failures and
timeouts `NodeUnreachable` at its boundary, but not a cancellation, so a network drop during a
publication — in a submit, a read, or a strategy's wait for validation through the sink's typed
`SequenceAsync` — used to reach `PublicationRun` as an `OperationCanceledException`, and the
application showed the run as cancelled by the person. The writer now names a cancellation its
caller's token did not cause `NodeUnreachable` at the same boundary, so the run fails with the node
message, which is what happened. `SequenceAsync` itself is unchanged; a caller outside the writer
that reads sequences — `ThrowawayAccount` — still sees the SDK's own exception, as before.

**An answer after an unanswered transmission** (I17). When one call has put a blob on the wire and
not heard back — a timeout, a transport failure — and a later transmission of the same blob is then
refused, the refusal is about the later copy only: the earlier one may still be in the node and
apply. `SubmitOutcome` gains the init-only `FollowsUnansweredAttempt`, true in exactly that case — a
refusal, never an answer that took the transaction — and the tidy-up lets such a spend wait under
its hash (4.6). The outcome is otherwise unchanged, so every other
caller reads it as before.

**Load warnings** (I15). `ILedgerSink` and `ILedgerSource` each gain `LoadWarnings`: how many of
the node's answers so far carried `"warning": "load"`, a count that only grows. The SDK reports the
warning on `XrplResponse<T>.Warning`, beside the result rather than inside it, so `XrplClientLedgerSink`
and `XrplClientLedgerSource` count it as each answer arrives — `submit` and `tx` in the sink, every
read in the source. The default of zero is true of any implementation that does not talk to a
rate-limited node; a decorator that wraps one and does not forward the count hides it, and the
tidy-up then backs off only on the other two signs of strain. The tidy-up reads the sum of both
counts before and after each round.

### 4.9 What the report says the run cost

**Mints** (I18). `ISubmissionStrategy.TicketMints` is how many `TicketCreate` transactions the
instance has submitted, over its lifetime, that validated; `SequenceStrategy` mints none and answers
zero. A session started by `StartAsync` reads it when it is created and again after the writer
returns, and counts the difference in `PublicationReport.Transactions` and so in
`FeeDropsEstimated`, instead of the estimate's `Mints`; a session made by `ResumeAsync` keeps the
estimate's, because the earlier sessions' mints are on no record it reads. Both figures stay the
publication's and leave the tidy-up out, as section 5 already says of the count.

**The tidy-up** (I17). `TicketReleaseFeeDrops` is the tidy-up's charged count times the fee the run
was approved at, which is the fee every spend carried; `TicketReleaseFeeDropsInDoubt` is its in-doubt
count times the same fee. Both are null when the tidy-up's counts were lost: it failed or did
not stop within its grace (`TicketReleaseFailure` is set), or it ended by throwing a cancellation
instead of returning them. The most it could have cost is then the
worst case the estimate already covered (I10). What the publication and its tidy-up cost is then
`FeeDropsEstimated + TicketReleaseFeeDrops`, and it may have cost up to `TicketReleaseFeeDropsInDoubt`
more; retries a strategy makes during the publication stay outside every figure here, as they
already were.

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

The worst-case fee gains the tidy-up: at most `MaxLiveTickets` transactions (I10). Sending one
ticket's spend up to five times does not raise that bound: only one transaction can consume a
ticket, and a spend that does not consume it is never applied and claims nothing (4.6). The displayed
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

For the amendment, against the same fakes — which answer `tefMAX_LEDGER` for a spend landing past
its `LastLedgerSequence`, as a node does, and report scripted load warnings — and against the
connection classes' scripted seams:

- a spend answered `error:tooBusy`, `error:notSynced`, `error:noCurrent`, `error:noNetwork`,
  `error:noClosed` or `tefMAX_LEDGER` is sent
  again and released; another error answer is not sent again; no ticket gets more than five
  submissions, and one answered with a passing refusal every time ends not released after exactly
  five;
- every spend carries `LastLedgerSequence` ten past the validated index its round read; a spend
  accepted as `terQUEUED` that never lands is not judged expired while the index is one short of its
  `LastLedgerSequence`, is judged expired in the round whose index equals it, is sent again in the
  round after, and is released long before the deadline;
- a throttled round that runs longer than ten ledgers — one spend in flight, each answered a ledger
  and four seconds after it was sent — reads the index again once it is ten seconds old and sends
  no spend past its deadline: every ticket is released on its round's one submission. A spend that
  took a re-read's deadline and is queued and never lands is judged expired only once an index
  reaches its own `LastLedgerSequence`, not the round's first one. A re-read the node does not
  answer sends nothing more that round, and the round's remaining spends go out in the next;
- after a round with strain — a load warning on a submit's answer, a load warning on a read's, a
  passing error answer, or `NodeUnreachable` from a submission, the index read or the listing — the
  next round's spends in flight and its pause follow the formulas in 4.6, including at their floor
  of one and their ceiling of sixteen polls; a round without strain changes neither;
- the sink returns `error:<code>` for a thrown `RippledException` and for an embedded `error` alike;
  turns `DisconnectedException`, `NotConnectedException` and an `OperationCanceledException` the
  caller did not cause into `NodeUnreachable` after its attempts; marks a refusal that follows a
  timed-out transmission of the same blob, and only a refusal; treats any error answer to its `tx`
  lookup, thrown or embedded, as not known yet; and lets a signer's failure and the caller's
  cancellation through unchanged. The source turns an `OperationCanceledException` the caller did not cause into
  `NodeUnreachable` for a listing, a ledger read and a lookup alike. Both connection classes count
  `"warning": "load"`;
- the tidy-up counts a released ticket and a ticket its own `tec` consumed as charged; a ticket whose
  every submission was refused as neither; a spend still queued at the deadline, a spend whose
  submission ended in `NodeUnreachable`, a spend refused after an unanswered transmission, and a
  spend cancelled part-way through a round as in doubt; an in-doubt spend that then expires as no
  longer in doubt; and a spend refused after an unanswered transmission whose earlier copy landed as
  released and charged. Answers already given in a round that is cancelled part-way stay counted; a
  census the node did not answer once is retried;
- the report counts the mints a fresh run made during its own session — not the estimate's, and not
  an earlier session's on a reused strategy — keeps the estimate's on a resume, prices the tidy-up's
  charged and in-doubt spends, and leaves both null when the tidy-up threw and when it did not stop
  within its grace;
- a network drop during a publication — a cancellation the caller did not cause, from any of the
  writer's calls — fails the run as `NodeUnreachable` rather than reporting it cancelled.

Against the standalone stand: the tidy-up's spends, now carrying `LastLedgerSequence`, release every
marked ticket, and the report prices them; a blob the node cannot parse comes back as
`error:invalidTransaction` rather than as an exception — the real node's error shape, which no fake
can vouch for. The stand's admin connection is never rate-limited, so the back-off is proven against
the fakes alone.

Against the standalone stand, one integration test does what section 1 did by hand: start a
ticketed publication, cancel it part-way, give the account one ticket of its own minted without the
marker, resume with a fresh strategy, and check that the publication finished, that **no ticket
carrying the marker is left on the account**, that the report counts every one released or already
gone, and that the foreign ticket is still there.

## 8. What this does not do

It does not recover tickets minted before this change: they carry no mark, and leaving them is the
price of never touching a ticket that may not be ours. They still go when the account is deleted.

It does not add `LastLedgerSequence` to memo submissions; only the tidy-up's spends carry one
(I16). A memo's retry is a sweep, a fresh ticket and a counted attempt, and a deadline on each memo
would add a second way for a memo to fail without making any memo cheaper; a spend has none of that
bookkeeping, and its deadline is what lets the tidy-up tell a dropped spend from a slow one within
its own two minutes. Stragglers from a stopped run can therefore still land later. What this design settles is that such a straggler cannot be mistaken for a memo
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

It does not back the publication itself off when the node warns. The tidy-up is the part that
sends hundreds of transactions after the file is safe, where slowing down costs nothing but time;
the strategies' own pacing is a separate question. It does not speed the tidy-up up again after a
warning (I15), and it does not see a warning through a decorator that does not forward
`LoadWarnings` (4.8).

It does not make `ThrowawayAccount` or the close screen aware of a refusal that follows an
unanswered transmission. `SubmitOutcome.FollowsUnansweredAttempt` is there for them to read, but the
close screen's "Nothing was charged" was already uncertain in that case for every refusal before
this amendment, and changing what it says is a change to the application, not to the tidy-up.

It does not handle JSON-RPC's `503 Service Unavailable`: the library talks to nodes over WebSocket
only, where exceeding the limit closes the socket with 1008, which is a transport failure (I14).

It does not address accounts that chain prepared transactions with `AccountTxnID`. Every
transaction a publication sends already changes that account's last transaction, the tidy-up
included; this is a property of publishing from such an account at all, not of this change.
