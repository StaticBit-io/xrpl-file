# Publishing from the application — design

Part C2 of the desktop application. C1 built a shell and every screen the vault needs, and it
deliberately spends no money and talks to no node. This is the part where both of those stop being
true: a file is chosen, a key signs for it, a node is asked what it will cost, and a run writes the
file into the ledger.

| Part | What it is | State |
|---|---|---|
| A | tickets and the publication session | shipped |
| B | the key vault | shipped |
| C1 | the shell and the vault screens | shipped |
| **C2** | **publishing: a file, a key, an estimate, a run — this document** | **shipped** |
| C3 | the throwaway account's life, including reclaiming its reserve | not started |
| C4 | reading a link back into a file | not started |

C2 adds nothing to the format and nothing to the publication machinery. Part A already publishes,
resumes, estimates and reports. What is missing is every decision a library refuses to make for a
person: which node, which strategy, what to show while it runs, what to say when it fails, and what
happens to a run when the window closes on it.

## 1. Scope

**In.** A Publish tab beside Keys and Settings. Choosing a file and a key. Asking a node what the
publication will cost and showing that. Running it with progress, and cancelling it. Surviving
navigation away from the run screen. Listing runs that did not finish and resuming them. Listing
publications that did succeed, with their links. Network settings: which node URL each network is
reached at. One sentence for every failure the library can report.

Also in, because publishing cannot be honest without it:

- fixing C1's window-close handler, which locks the vault and would therefore kill a running
  publication at its next signature (section 9);
- touching the idle lock on progress as well as on signatures, because the read-back stage signs
  nothing and can outlast the idle window (section 10);
- extracting the ledger doubles out of `XrplFile.Core.Tests` into a test-support library, so the
  application's tests drive real `PublicationSession` code against the same fake ledger the
  library's own tests use (section 13).

**Out.** Creating or funding a throwaway account, and reclaiming its reserve — that is C3, and this
part publishes only from a key the vault already holds and an account that is already funded.
Reading a link back into a file — that is C4. Publishing to a network the vault entry does not name.
Any file source other than the local disk.

## 2. Decisions

| # | Decision | Why |
|---|---|---|
| F1 | The application chooses the submission strategy; the person never sees the word "ticket" | The choice turns on protocol arithmetic — reserve, sequence, the `AccountDelete` clock — that a person cannot be asked to weigh at the moment they want to publish a file. Section 6 argues the rule |
| F2 | The threshold is one hundred memos, the library's own batch unit | Below one batch, minting locks a batch's worth of owner reserve to run what a single sequence pass runs with none. At or above it, minting is what the machinery exists for |
| F3 | The network is the key's, not a separate choice | A vault entry already records the network its address belongs to. A second control could only disagree with it, and a disagreement here spends real money on the wrong chain |
| F4 | A node URL may be overridden per network, and a network `Endpoints.For` does not know needs one | Without this there is no way to publish against a standalone stand, which is the only place the whole path can be exercised without spending anything |
| F5 | The run lives in a singleton service, not in a component | A publication outlives the screen that started it: the person navigates to the key list, comes back, and the run must still be there with the same progress and the same cancellation token |
| F6 | The run does not survive the window closing | Nothing in the application is a background service, and a run that continued past the window would sign against a vault the close handler has locked. Closing stops the run; the state file makes it resumable |
| F7 | Closing the window during a run asks first, and locks only after the run has stopped | Owner's decision. A close that killed a run mid-batch would leave tickets live and money spent with nothing to show. Section 9 covers what "asks first" can and cannot mean on this platform |
| F8 | Every `XrplFileError` gets its own sentence, in a model with tests | `NodeUnreachable`, `InsufficientBalance` and a node refusing a transaction are three different problems with three different remedies, and a screen that conflates them sends the person to fix the wrong thing |
| F9 | The estimate shown is the library's, unaltered, even when the run will use the sequence strategy | The estimate is ticket-shaped: it counts mints and a peak ticket reserve a sequence run never pays. Showing it unchanged overstates the cost, never understates it, and the alternative is a second estimator nothing validates |
| F10 | The connection is the test seam, not the session | A fake `ILedgerSource` and `ILedgerSink` leave `PublicationSession`, the strategy choice and the progress reporting inside the tested path. A faked session would leave all of it outside |
| F11 | Successful publications are appended to a plain JSON file beside the run state, and can be forgotten | A link nobody recorded is a file nobody can find again. It is the person's own history, so removing a row and clearing the file are both offered |
| F12 | Run state lives in the application's data directory, never beside the vault | The vault file is portable by design (C1's E5) and may sit on removable media or in a synchronised folder. A half-finished run's state is neither portable nor something to synchronise |
| F13 | Screens learn about the run by polling once a second | The same mechanism, and the same interval, that C1's shell already uses to notice the vault being locked behind it. A second notification mechanism for a second kind of change is a second thing that can disagree |
| F14 | Cancelling and closing are the same door | Both stop the run at the first point the token is observed, both leave the state file, and both put the run in the unfinished list. One path, tested once |

## 3. The shape of it

`XrplFile.Core` gains two public members. `XrplFileLink.TryNormalizeNetwork`, because the publish
screen has to ask whether a key's network is one a link can carry — a vault entry records its
network as free text, and without it the refusal arrived from inside a run the person had already
confirmed, as `InvalidLink`, instead of from the screen where the key is chosen. And
`Endpoints.KnownNetworks`, because the settings screen needs to enumerate the built-in networks,
and `Endpoints` previously exposed only a lookup, so the application had retyped the list, where a
fourth built-in network would have silently gone missing. Nothing is added to `XrplFile.Keys`.
Everything else lands in the application projects C1 created, plus one new test-support library.

```
app/XrplFile.App.Core/            (net10.0, no MAUI — everything testable)
  PublishModel.cs                 the file and the key
  EstimateModel.cs                the estimate and the confirmation
  RunModel.cs                     the run screen's view of PublicationRun
  UnfinishedModel.cs              runs that did not finish
  HistoryModel.cs                 publications that did
  PublicationRun.cs               the singleton: one run, its token, its progress, its outcome
  PublishRequest.cs               file bytes, type, name, key reference, network
  PublishPolicy.cs                the strategy rule and its threshold
  PublishErrorMessages.cs         one sentence per XrplFileError
  IConnectionFactory.cs           network -> (ILedgerSource, ILedgerSink), disposable
  XrplConnectionFactory.cs        the real one, over IXrplClient
  INodeSettingsStore.cs           per-network node URL override
  FileNodeSettingsStore.cs        the file behind it
  PublicationHistory.cs           the publications file: append, read, forget
  PublicationRecord.cs            one row of it
  RunPaths.cs                     where run state and history live

app/XrplFile.App/Components/
  Publish.razor                   file + key
  Estimate.razor                  the estimate and the confirmation
  Run.razor                       progress, cancel, outcome
  Unfinished.razor                the unfinished list
  History.razor                   the publications list
  (Shell.razor gains a Publish tab; Settings.razor gains the node URLs)

tests/XrplFile.TestSupport/       (net10.0 library, new)
  FakeLedgerSource.cs             moved from XrplFile.Core.Tests
  FakeLedgerSink.cs               moved from XrplFile.Core.Tests
  SynchronousProgress.cs          moved from XrplFile.Core.Tests
```

`XrplFile.App.Core` gains a reference to `Xrpl` (the SDK) for `XrplConnectionFactory` only. It
remains a plain `net10.0` library with no MAUI in it, so all of the above is tested on Linux in CI
exactly as C1's models are.

## 4. The screens

The Publish tab is one flow with three stops and two lists. There is no router — C1 chose one
window and a switch on a model's own stage, and this continues it: a `PublishStage` on
`PublishModel` says which of the three is showing, and the component switches on it without
deciding anything (C1's E2).

### 4.1 The file and the key

A file picker (C1's `IFilePickerService`), and the vault's keys as a list. Each key shows its label,
its address, its network and its kind. The file shows its name and its size.

What the model refuses, each with its own message: no file chosen; no key chosen; a file of zero
bytes; a file the application will not load into memory, because `PublicationSession` takes the
whole file as a `ReadOnlyMemory<byte>` and there is no streaming path into it; the vault locked
between opening the screen and pressing the button; a key whose network has no node URL and is not
one `Endpoints.For` knows (F4).

One thing it warns about rather than refuses. `ReaderOptions.MaxBytes` defaults to 20,000,000, so a
larger file publishes correctly and then produces a link the library's own reader refuses unless the
caller raises that ceiling. The person is told before they spend anything, and may go ahead: the
limit is the reader's default, not the format's, and the writer's read-back lifts it for itself.

The media type is taken from the file's extension, with `application/octet-stream` when nothing
matches. The name recorded in the manifest is the file's own name.

### 4.2 The estimate

Pressing Estimate connects to the node for that network, calls `PublicationSession.EstimateAsync`,
and shows what came back: chunks, transactions, the node's price per transaction, the total at that
price, the worst case the library actually requires, what the account holds, and what it can spend
once its existing owner reserve is subtracted. `PublicationEstimate.BalanceCovers` decides whether
the confirm button is live; when it is not, the screen says what is held and what is needed rather
than only refusing.

The figures are the library's, unaltered (F9). The screen adds one line naming the network and the
node URL it will publish to, because that is the fact a person most needs to check before spending.

Nothing is submitted by this screen. It may be repeated: the node's price moves, and a fresh
estimate is how a person gets the current one.

### 4.3 The run

Confirming hands a `PublishRequest` to `PublicationRun` (section 8) and shows its progress.

Progress is `PublishProgress(Stage, Done, Total)` straight from the writer, with the five stages the
library reports — `chunks`, `lists`, `read-back`, `manifest`, `done` — given readable names. The
`read-back` stage is worth naming explicitly on screen: it submits nothing, so a person watching a
transaction counter would think the run had stalled.

Cancel stops it (F14). The outcome is one of three: the report, with the link and a copy button; a
cancellation, with the run named in the unfinished list; or a failure, with its sentence (F8) and
the same pointer to the unfinished list when the state file survived.

### 4.4 Unfinished runs

`PublicationSession.Unfinished(stateDirectory)` returns each run's path with either its state or the
reason it could not be read. Both cases are shown: a state file that cannot be read still names, by
its path, a run that spent money.

For a readable one the list shows the writer address, the network, the file name and size, the stage
it stopped in, and how far it got. Resuming needs the same file back: the state carries the Merkle
root, so a resume with different bytes is refused by the library, and the screen says that before
the person picks a file rather than after.

The screen also states the two refusals part A makes that this screen cannot pre-empt: a resume
signed by a different key than the run started with, and a node whose price has moved since the run
began. Both come back as typed errors with their own sentences.

Deleting a state file is offered, with what it means said plainly: the money that run spent is spent
either way, and what is lost is the ability to finish it without paying again.

### 4.5 Publications

The rows of the publications file (section 12), newest first: the link, the file name, the size, the
network, the account, when it finished, and the fee it was estimated at. Copying a link is one
button. Forgetting a row, and clearing the file, are offered and say that they change nothing in the
ledger — the file is still there, and the link still works for anyone who kept it.

## 5. The node and the network

The network comes from the chosen key's vault entry (F3). The node URL is the first of
`Endpoints.For(network)` unless Settings holds an override for that network, in which case the
override wins. A network with neither is refused on the first screen, naming the network and saying
that Settings is where a URL is added.

The override exists for the standalone stand. `Endpoints.For` answers only `mainnet`, `testnet` and
`devnet`; a vault entry may name anything, and a locally run rippled is how the whole path gets
exercised without spending anything real.

Settings stores them in a small JSON file beside C1's `vault-location.txt`, in the application data
directory: a map of network name to URL. It is read the way C1's location store reads its own file —
an unreadable or malformed file means "nothing has been chosen", never a crash on startup.

Each screen that connects opens its own connection through `IConnectionFactory` and disposes it when
it is done. There is no shared, long-lived client: the estimate screen's connection is not the run's,
and a WebSocket that has been idle since an estimate the person made ten minutes ago is not one to
start a publication on.

## 6. Choosing the strategy

The application picks (F1). The rule is one line — tickets at or above one hundred memos, sequence
below (F2) — and it is one line in one place, `PublishPolicy`, with its own tests.

The memo count is `Chunks + Lists + 1`, not `PublicationEstimate.Transactions`: the latter includes
the `TicketCreate` mints, which exist only under the strategy being chosen.

Three costs pull in different directions, and they do not cross at the same point.

**Tickets cost funding.** `PeakReserveDrops` is the live-ticket count times the owner reserve the
node reports, capped at the protocol's live-ticket ceiling of 250. Whatever that reserve currently
is, up to 250 of them must be in the account before the run starts — against fees, for a
hundred-memo run at twelve drops, of about 1200 drops. The two are not the same order of magnitude:
the reserve is what decides how much XRP has to be moved into a throwaway account, and the fees are
rounding. It does come back as the tickets are spent, so the cost is temporary; it still has to be
funded first.

**Sequence costs time that does not come back.** Every sequence-consuming transaction advances the
account's `Sequence` by one, and `AccountDelete` is refused while `Sequence + 255` exceeds the
validated ledger index. A run of N sequence transactions therefore pushes the earliest ledger at
which the account's base reserve can be reclaimed out by N — about four seconds each. The 255-ledger
floor is about seventeen minutes whatever happens; a hundred transactions add about seven minutes on
top of it, a thousand would add over an hour. Ticketed transactions do not move `Sequence` at all,
which is the measured fact part A was built on.

**Sequence costs robustness as it grows.** A ticketed memo whose submission never reached a ledger
gets its ticket back and is retried on its own. A sequence gap stalls everything behind it. One
stall in a five-transaction run is a moment; one in a thousand-transaction run is the run.

A hundred memos is where the funding argument stops dominating: below it, minting locks a full
batch's reserve to run what one sequence pass runs with none. The mints themselves tell the same
story. The writer batches three times over — chunks, block list, manifest — and each pass mints from
an empty queue, so a run pays at minimum three extra transactions and thereafter one per hundred
memos per pass. On fifty memos that is three transactions on top of fifty, six per cent; on a
thousand it is twelve on top of a thousand, and the overhead settles towards the one per cent that a
batch of a hundred costs by construction.

The asymmetry we do not exploit: an imported key has no `AccountDelete` in its future, so the refund
clock argument does not apply to it at all, and the threshold could be higher for imported keys than
for generated ones. It is not, for two reasons. Publishing a thousand sequential transactions from
an account a person also uses for other things monopolises that account's sequence numbers for the
duration, and any transaction they send from elsewhere mid-run collides. And a rule that behaves
differently for two kinds of key is a rule nobody can predict from the screen, for a benefit neither
kind actually asked for.

The strategy is built with `FeeDrops` set from `PublicationEstimate.FeeDropsPerTransaction`, because
`PublicationSession.StartAsync` re-estimates and refuses a strategy priced differently
(`XrplFileError.FeeMismatch`). That field is an `int` and the estimate's is a `long`: a price that
does not fit an `int` is reported as a node price the application cannot use, rather than wrapped.

## 7. Errors

`PublishErrorMessages` maps every `XrplFileError` value to one sentence, and has a test that every
value is covered — a new value added to the library fails that test rather than reaching a screen as
a number.

Three must never be conflated (F8), because their remedies are different and unrelated:

- `NodeUnreachable` — the node did not answer. Nothing was spent. Check the URL, check the network,
  try again.
- `InsufficientBalance` — the node answered and the account cannot cover the run. Fund it, or
  publish a smaller file.
- `SubmitRefused` — the node answered and refused a transaction. Neither of the above fixes it.

The rest, by what they tell a person:

- `BudgetExceeded` — the run would cost more than it was started with. It is stopped, not overspent.
- `FeeMismatch` — the node's price moved between the estimate and the start. Estimate again.
- `ValidationTimeout` — submitted, not seen validated in time. What landed is in the state file; the
  run is resumable, and resuming does not pay twice for what already landed.
- `ResumeMismatch` — this is not the file that run started with, or not the key that started it.
- `SessionAlreadyRun` — a session object is good for one run. This is a defect if a person ever sees
  it, and the sentence says so rather than pretending it is a situation they caused.
- `ChunkMissing`, `ListMismatch`, `BlockMismatch`, `CorruptManifest`, `ManifestNotFound`,
  `NodeLacksHistory`, `InvalidLink` — reading errors. During a publication they can only come from
  the read-back stage, and they mean the ledger did not return what was just written to it. The
  manifest is not written, so no link is published that does not resolve.
- `TooLarge` — a reading error too, and the one that cannot occur while publishing at all: the
  writer's read-back builds its reader with the ceiling lifted. Its sentence therefore names the
  reader's limit rather than the file, and section 4.1 is where a person hears about that limit at
  the only moment it is still cheap to act on.

Two failures are not library errors and still need sentences: the vault being locked mid-run
(`VaultException` with `VaultError.Locked` — unlock and resume), and the entry having been deleted
under the run (`NoSuchEntry`).

## 8. The run outlives the screen

`PublicationRun` is a singleton registered beside the vault (F5). It holds at most one run.

- `PublicationRunState State` — `Idle`, `Running`, `Succeeded`, `Failed`, `Cancelled`.
- `PublishProgress? Progress` — the last one reported.
- `PublicationReport? Report`, `string? Failure` — the outcome, whichever it was.
- `StartAsync(PublishRequest)` — refuses when a run is already going, starts one otherwise.
- `ResumeAsync(UnfinishedRun, file)` — the same, from a state file.
- `Cancel()` — signals the token.
- `StopAsync(TimeSpan)` — cancels and waits for the run to actually stop. What the window-close
  handler calls (section 9).

It owns the `CancellationTokenSource`, the connection, the strategy and the session, and disposes all
of them when the run ends however it ends. Screens read it and never hold any of it themselves:
navigating away from the run screen and back shows the same run at its current progress (F13 says
how the screen notices).

The state is read from the UI thread and written from the run's own task. Screens take a consistent
snapshot under the service's lock rather than reading fields one at a time — a screen that read
`State` and `Report` separately could see `Succeeded` with no report, or `Running` with the previous
run's one.

## 9. The window closing during a run

C1's `App.xaml.cs` handles `Window.Destroying` by locking the vault and disposing the idle lock. With
a run in flight that is a collision: the lock lands, the next signature comes back
`VaultError.Locked`, and the run dies in the middle of a batch with tickets live and money spent.

The close therefore stops the run before it locks (F6, F7):

```
OnWindowDestroying:
    await run.StopAsync(timeout)   // cancel, then wait for the run to actually stop
    await vault.LockAsync()
    await autoLock.DisposeAsync()
```

That ordering is what makes the close correct on its own, whatever else is wired. The run stops at
the first cancellation point, its state file is already on disk, and it appears in the unfinished
list the next time the application opens. The timeout is bounded — a run that will not stop must not
hold a closing window forever — and when it expires the lock proceeds anyway, which is exactly the
old behaviour and no worse than it.

Asking first (F7) needs an event that can refuse the close, and `Window.Destroying` is not one: MAUI
raises it when the window is already going. The cancellable event exists only on the platform —
WinUI's `AppWindow.Closing`, whose argument carries a `Cancel` flag — reached through the MAUI
window's handler. The application targets Windows only today, so hooking it is honest rather than a
platform-specific shortcut around a cross-platform API that exists. The implementation therefore:

1. hooks the platform closing event, and when a run is going, cancels the close and shows the
   confirmation;
2. on confirmation, stops the run and closes for real;
3. keeps the `Destroying` ordering above regardless, as the backstop for every path that does not go
   through the ask — the platform hook failing to attach, a shutdown that skips it, a second window.

If the platform hook cannot be attached on the installed SDK, step 3 alone is still a correct
application; what is lost is the question, not the safety. The plan verifies the hook against the
installed Windows App SDK before relying on it, and says so if it is not there.

## 10. The idle lock during a run

`VaultSigner` already touches `VaultAutoLock` on every signature, so a run that signs continuously
does not lock itself out. Two stages sign nothing: `read-back`, which reads every block back through
the reader, and the wait for validation at the end of a batch. Either can outlast the idle window on
a large file, and the manifest signature that follows read-back would then find a locked vault — at
the very last transaction of an otherwise complete run.

`PublicationRun` therefore touches the idle lock on every progress report as well. Progress is
reported per submitted memo and per read-back block, so the window is reset by work in every stage
that takes time. This is not a weakening of the idle policy: the vault is being used, continuously,
by something the person started and can see running on screen.

## 11. Resuming

`PublicationSession.ResumeAsync` takes the strategy, the source, the file bytes and the state path.
The strategy is chosen by the same rule against a fresh estimate (section 6), so a resumed run may
legitimately use a different strategy than the run that started it — the state file records the
chunks that landed, not how they were submitted.

What the screen must get right is which failures are the person's to fix:

- a different file — refused by the library against the recorded Merkle root, and pre-empted by the
  screen where it can be;
- a different key — the state records the key reference and the writer address;
- a moved fee — `FeeMismatch`, fixed by estimating again;
- a state file from an older format version — `PublicationState.Load` refuses version 1 files, and
  `Unfinished` lists them as unreadable. The screen shows them as such, and they can be deleted.

## 12. The publications file

`publications.json` in the application's data directory, beside the run state (F12). One array of
records, appended after a run succeeds, written the way the vault writes: to a temporary file, then
moved into place, so a crash mid-write cannot leave a truncated file.

Each record: the link, the file name, the size, the media type, the network, the writer address, the
transaction count, the fee the run was estimated at, and when it finished. It holds no secret — the
key reference is recorded, never a seed, which is the same rule part A's state file follows.

It is the person's own history and they may not want one: removing a single record and clearing the
file are both offered, and both say that nothing in the ledger changes. A file that cannot be read is
reported as an empty history with the reason shown, never as a crash and never by overwriting it.

## 13. The shared ledger doubles

`FakeLedgerSource`, `FakeLedgerSink` and `SynchronousProgress<T>` live in `XrplFile.Core.Tests`
today. C2's tests need the same fake ledger to drive real `PublicationSession` code (F10), and a
second copy of a fake ledger is two fake ledgers that will drift.

They move to a new `tests/XrplFile.TestSupport` library, and `XrplFile.Core.Tests` switches onto it
in the same commit, so at no point do two copies exist. `FakeLedgerSinkTests` stays in
`XrplFile.Core.Tests`: the double needs testing, and `TestSupport` is a library with no runner of its
own — adding one to host a single test class buys nothing.

`XrplFile.TestSupport` joins `XrplFile.Libraries.slnf` so CI restores and builds it.

## 14. Testing

Everything in `XrplFile.App.Core` is tested on Linux in CI, as C1's models are. The fake
`IConnectionFactory` hands out `FakeLedgerSource` and `FakeLedgerSink`, which means the tests
exercise the real `PublicationSession`, the real strategies, the real state files and the real
progress reporting — the only thing faked is the node.

What must be covered, beyond the per-model tests:

- the strategy rule at, below and above the threshold, and that the memo count it uses excludes
  mints;
- the file rules: zero bytes refused, a file past the memory ceiling refused, and a file past
  `ReaderOptions.MaxBytes`'s default warned about and still allowed;
- a full publication through `PublicationRun`, from request to report, with progress observed;
- a cancelled run: the state file exists, the run appears in `Unfinished`, the service reports
  `Cancelled`;
- `StopAsync` returning only after the run has actually stopped, and returning on its timeout when
  the run does not;
- a run started while one is going, refused;
- every `XrplFileError` value having a sentence, by enumerating the enum;
- the three sentences that must differ actually differing;
- the node settings store: absent file, unreadable file, override wins over `Endpoints.For`, a
  network with neither;
- the history file: append, read back, forget one, clear, and an unreadable file read as empty;
- the idle lock being touched by progress, not only by signatures, using a `VaultAutoLock` over a
  controlled `TimeProvider`.

The MAUI project keeps its compile-only job on Windows. The platform close hook is not testable in
CI and is therefore kept to the smallest possible piece of code, with every decision it might make
living in `PublicationRun` where tests can reach it.

## 15. What this part does not do

It does not create an account, fund one, or delete one. It publishes from a key the vault already
holds, whose account already has a balance, and the estimate screen says plainly when that balance is
not enough. Making a throwaway account, funding it, and reclaiming its base reserve afterwards —
including the `AccountDelete` that `XrplFile.Core` does not implement at all yet — is C3, and the
`AccountDelete` clock this document argues the strategy threshold from is exactly why C3 comes next.

It does not read a link back into a file. C4.
