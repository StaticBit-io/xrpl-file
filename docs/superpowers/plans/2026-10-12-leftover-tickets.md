# Leftover Tickets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A publication picks up the tickets this library left on its account, spends any it does not need once it succeeds, and the application says plainly what an interruption leaves behind.

**Architecture:** Tickets are marked at mint (`xf/t` memo on `TicketCreate`) and found on the ledger through a new `ILedgerSource.LiveTicketsAsync` plus one shared classifier (`LeftoverTickets.FindAsync`), used by the ticket strategy's pick-up, the estimate, and a post-publication tidy-up that both strategies implement through a new `ISubmissionStrategy` member. `PublicationSession` runs the tidy-up after the writer succeeds, bounded in time and never failing the publication. The application moves the close-confirmation sentence into `RunModel` and adds a ticket note to the unfinished-runs list.

**Tech Stack:** .NET 10, C# latest, xunit.v3 with the built-in `Assert`, the `Xrpl` SDK (`IXrplClient`, `AccountObjectsRequest`), MAUI Blazor Hybrid for the two UI touch points.

**Spec:** `docs/superpowers/specs/2026-10-12-leftover-tickets-design.md` (decisions I1–I12). Read it before any task; where this plan and the spec disagree, the spec wins.

## Global Constraints

- Repository artifacts are English: code, comments, test names, commit messages, docs.
- Commit messages use Conventional Commits. No AI attribution of any kind: no `Co-Authored-By`, no "Generated with", no trailers.
- Never push, never open a PR.
- Never change a file's encoding. Files are UTF-8; keep line endings as they are.
- Explicit types, no `var`. `async`/`await` for IO, `CancellationToken` passed through. `StringComparison.Ordinal`/`OrdinalIgnoreCase` for string comparisons. `ConfigureAwait(false)` in library code (`src/`), as the surrounding code does.
- Tests: xunit.v3, built-in `Assert` only. No FluentAssertions, no new packages.
- `dotnet test` does not work in this repository. Run a test project as an executable: `dotnet run --project <test project> -- <filters>`; filters are `-class "<Full.Type.Name>"`, `-method "<Full.Type.Name.Method>"`, `-trait "Category=Integration"`, `-trait- "Category=Integration"`.
- Only the node is faked. A fake must never be more forgiving than the node (C3 lesson).
- Application decisions live in `app/XrplFile.App.Core` models, testable on Linux; Razor components and `App.xaml.cs` hold no decisions (C1 E2). No component-test framework (E9).
- The integration stand is the `xrpl-file-ci` compose project. Every docker command passes `-p xrpl-file-ci`, `down` included. Never touch any other container or compose project on this machine.
- The marker is internal to the library (spec 4.1). It must not become public API.
- The close-confirmation text for a ticketed run and the unfinished-runs sentence are copied verbatim from spec 6.1 and 6.2. The close text for a run without tickets stays exactly as it is today.
- Tidy-up deadline default: two minutes (spec 4.6).

## Review Focus

1. **A ticket picked up twice.** A strategy that holds picked-up tickets across passes and then looks again must not enqueue a ticket it already holds, has dispatched, or found gone; a double-dispatched ticket turns one memo into a `tefNO_TICKET` and the other into a silent hole. Pinned in Task 3: held by `Tickets_picked_up_by_one_call_are_never_picked_up_again_by_the_next`, dispatched by `A_dispatched_ticket_still_listed_is_never_picked_up_again`, gone by `A_ticket_that_is_gone_costs_its_memo_no_attempt_and_is_not_picked_up_again`.
2. **A memo that draws several gone tickets in a row.** It must still land, with `MaxAttemptsPerMemo = 1`, without waiting for a sweep of a memo that was never applied. Pinned in Task 3 (`A_memo_that_draws_several_gone_tickets_in_a_row_still_lands`).
3. **A tidy-up with nothing to do.** An account holding no tickets of this library's must see no submission at all after a publication — not one fee for nothing. Pinned in Task 5 (`An_account_with_no_leftovers_sees_no_tidy_up_transaction`).
4. **A cancelled publication.** Cancelling must not start a tidy-up: the leftovers are exactly what a resume will pick up, and spending them would charge fees for a run the person stopped. Pinned in Task 6 (`A_cancelled_publication_does_not_tidy_up`).
5. **A small file on an account full of foreign tickets.** Zero free slots must refuse only a ticketed run; a sequence-sized file must still be publishable. Pinned in Task 7 (`A_small_file_is_still_publishable_when_every_ticket_slot_is_taken`).

---

## File map

| File | Change | Responsibility |
|---|---|---|
| `src/XrplFile.Core/ILedgerSource.cs` | modify | `LiveTicket` record; `LiveTicketsAsync` |
| `src/XrplFile.Core/XrplClientLedgerSource.cs` | modify | `account_objects type=Ticket`, marker pagination, `ParseTickets` |
| `src/XrplFile.Core/MemoLayout.cs` | modify | internal `TicketMarker` / `TicketMarkerHex` |
| `src/XrplFile.Core/XrplFile.Core.csproj` | modify | `InternalsVisibleTo XrplFile.TestSupport` |
| `src/XrplFile.Core/LeftoverTickets.cs` | create | `TicketCensus`, `FindAsync` (classify), `ReleaseAsync` (tidy-up) |
| `src/XrplFile.Core/TicketRelease.cs` | create | public `TicketRelease` record |
| `src/XrplFile.Core/ISubmissionStrategy.cs` | modify | `ReleaseLeftoverTicketsAsync` |
| `src/XrplFile.Core/TicketStrategy.cs` | modify | marked mint, pick-up, gone tickets, sweep on data, tidy-up |
| `src/XrplFile.Core/SequenceStrategy.cs` | modify | optional `ILedgerSource` parameter, tidy-up |
| `src/XrplFile.Core/PublicationSession.cs` | modify | run tidy-up after success; slot refusal; `TicketReleaseTimeout` |
| `src/XrplFile.Core/PublicationReport.cs` | modify | four init-only properties |
| `src/XrplFile.Core/PublicationEstimate.cs` | modify | ours/others arithmetic, tidy-up in worst case, `RequireTicketSlot` |
| `tests/XrplFile.TestSupport/FakeLedgerSource.cs` | modify | live-ticket listing, `TransactionLookups` |
| `tests/XrplFile.TestSupport/FakeLedgerSink.cs` | modify | ticket creators, `tefNO_TICKET`, `AnswersGone`, `LeaveTickets`, mint memos |
| `app/XrplFile.App.Core/PublishPolicy.cs` | modify | pass source to `SequenceStrategy`; `UsesTickets(long)`; `HasNoTicketRoom` |
| `app/XrplFile.App.Core/EstimateModel.cs` | modify | refuse a ticketed run with no free slot |
| `app/XrplFile.App.Core/PublicationRun.cs` | modify | `RunSnapshot.UsesTickets` / `OwnerReserveDrops` |
| `app/XrplFile.App.Core/RunModel.cs` | modify | `CloseConfirmation` |
| `app/XrplFile.App/App.xaml.cs` | modify | use `RunModel.CloseConfirmation` |
| `app/XrplFile.App.Core/UnfinishedModel.cs` | modify | `TicketNote`, `ShowsTicketNote` |
| `app/XrplFile.App/Components/Unfinished.razor` | modify | render the note |
| test decorators in `tests/XrplFile.App.Core.Tests/PublicationRunTests.cs`, `ReadRunTests.cs`, `tests/XrplFile.Core.Tests/XrplFileWriterTests.cs` | modify | delegate the new interface members |

All paths are relative to `E:\GIT\XRPL\xrpl-file`. Run every command from there.

---

### Task 1: Listing an account's live tickets

**Files:**
- Modify: `src/XrplFile.Core/ILedgerSource.cs`
- Modify: `src/XrplFile.Core/XrplClientLedgerSource.cs`
- Modify: `tests/XrplFile.TestSupport/FakeLedgerSource.cs`
- Modify: `tests/XrplFile.TestSupport/FakeLedgerSink.cs`
- Modify (delegation only): `tests/XrplFile.App.Core.Tests/PublicationRunTests.cs` (`ClockAdvancingSource`), `tests/XrplFile.App.Core.Tests/ReadRunTests.cs` (`GatedAccountTransactionsSource`)
- Test: `tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs`, `tests/XrplFile.Core.Tests/FakeLedgerSinkTests.cs`; create `tests/XrplFile.Core.Tests/LiveTicketsStandaloneTests.cs`

**Interfaces:**
- Produces: `public sealed record LiveTicket(uint Sequence, string CreatedBy)` in namespace `XrplFile`; `Task<IReadOnlyList<LiveTicket>> ILedgerSource.LiveTicketsAsync(string account, CancellationToken cancellationToken)`; `internal static IReadOnlyList<LiveTicket> XrplClientLedgerSource.ParseTickets(JsonElement answer)`; `ScriptedNode.Requests` in `XrplClientLedgerSourceTests` (the request objects, in call order); `FakeLedgerSource.SetLiveTickets(string account, IReadOnlyList<LiveTicket> tickets)`, `FakeLedgerSource.TransactionLookups` (int); `FakeLedgerSink.AnswersGone` (`HashSet<uint>`); `FakeLedgerSink.UnknownCreator` (const string). The fake sink answers `tefNO_TICKET` for a ticket it consumed or one below its sequence that it never held, and `terPRE_TICKET` only for one above its sequence.

- [ ] **Step 1: Write the failing tests for the client source**

Append to `tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs`, inside the class, before the `ScriptedNode` nested class:

```csharp
    [Fact]
    public async Task Live_tickets_are_read_page_by_page_with_their_creators()
    {
        ScriptedNode node = new();
        node.Answer("""
            {"account":"rWriter","account_objects":[
              {"LedgerEntryType":"Ticket","TicketSequence":11,"PreviousTxnID":"aa11","Account":"rWriter"},
              {"LedgerEntryType":"Ticket","TicketSequence":12,"PreviousTxnID":"aa11","Account":"rWriter"}],
             "marker":"next-page"}
            """);
        node.Answer("""
            {"account":"rWriter","account_objects":[
              {"LedgerEntryType":"Ticket","TicketSequence":40,"PreviousTxnID":"bb40","Account":"rWriter"}]}
            """);

        IReadOnlyList<LiveTicket> tickets = await new XrplClientLedgerSource(node.HandleAsync)
            .LiveTicketsAsync("rWriter", TestContext.Current.CancellationToken);

        Assert.Equal(
            [new LiveTicket(11, "AA11"), new LiveTicket(12, "AA11"), new LiveTicket(40, "BB40")],
            tickets);
        Assert.Equal(["account_objects", "account_objects"], node.Commands);

        // What was asked matters as much as what came back: only tickets, from the validated
        // ledger, and the second page asked for by the marker the first one carried.
        Xrpl.Models.Methods.AccountObjectsRequest[] asked = [.. node.Requests.Cast<Xrpl.Models.Methods.AccountObjectsRequest>()];
        Assert.All(asked, request =>
        {
            Assert.Equal(Xrpl.Models.LedgerEntryType.Ticket, request.Type);
            Assert.Equal(Xrpl.Models.Ledger.LedgerIndexType.Validated, request.LedgerIndex.LedgerIndexType);
        });
        Assert.Null(asked[0].Marker);
        Assert.Equal("next-page", Assert.IsType<JsonElement>(asked[1].Marker).GetString());
    }

    [Fact]
    public async Task An_account_the_node_does_not_know_holds_no_tickets()
    {
        ScriptedNode answered = new();
        answered.Answer("""{"error":"actNotFound"}""");

        Assert.Empty(await new XrplClientLedgerSource(answered.HandleAsync)
            .LiveTicketsAsync("rNobody", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void An_entry_that_is_not_a_ticket_or_lacks_its_fields_is_not_read_as_one()
    {
        JsonElement answer = JsonDocument.Parse("""
            {"account_objects":[
              {"LedgerEntryType":"Offer","PreviousTxnID":"cc"},
              {"LedgerEntryType":"Ticket","PreviousTxnID":"dd"},
              {"LedgerEntryType":"Ticket","TicketSequence":7}]}
            """).RootElement;

        Assert.Empty(XrplClientLedgerSource.ParseTickets(answer));
    }
```

In the same file's nested `ScriptedNode`, record the request objects as well as their commands: add the property

```csharp
        public List<Xrpl.Models.Methods.BaseRequest> Requests { get; } = [];
```

and, in `HandleAsync`, next to `Commands.Add(request.Command);`:

```csharp
            Requests.Add(request);
```

- [ ] **Step 2: Write the failing tests for the fake sink's ticket answers**

Append to `tests/XrplFile.Core.Tests/FakeLedgerSinkTests.cs`, inside the class:

```csharp
    private static Dictionary<string, object> Ride(uint ticket) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = Wallet.ClassicAddress,
        ["Sequence"] = 0u,
        ["TicketSequence"] = ticket,
        ["Fee"] = "10",
        ["SigningPubKey"] = Wallet.PublicKey,
    };

    private static Dictionary<string, object> Mint(uint sequence, uint count) => new()
    {
        ["TransactionType"] = "TicketCreate",
        ["Account"] = Wallet.ClassicAddress,
        ["Sequence"] = sequence,
        ["TicketCount"] = count,
        ["Fee"] = "10",
        ["SigningPubKey"] = Wallet.PublicKey,
    };

    [Fact]
    public async Task A_ticket_that_was_already_ridden_is_answered_as_gone_the_way_a_node_answers_it()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        await sink.SubmitAsync(Mint(10, 2), Signer, TestContext.Current.CancellationToken);
        SubmitOutcome first = await sink.SubmitAsync(Ride(11), Signer, TestContext.Current.CancellationToken);
        SubmitOutcome second = await sink.SubmitAsync(Ride(11), Signer, TestContext.Current.CancellationToken);

        // Measured on xrpld 3.3.0: "Ticket is not in ledger". terPRE_TICKET here would send a
        // strategy down its "the mint was not awaited" path for a ticket that is simply spent.
        Assert.Equal("tesSUCCESS", first.EngineResult);
        Assert.Equal("tefNO_TICKET", second.EngineResult);
    }

    [Fact]
    public async Task A_ticket_above_the_accounts_sequence_does_not_exist_yet()
    {
        FakeLedgerSink sink = new(new FakeLedgerSource());

        SubmitOutcome outcome = await sink.SubmitAsync(Ride(5_000), Signer, TestContext.Current.CancellationToken);

        Assert.Equal("terPRE_TICKET", outcome.EngineResult);
    }

    [Fact]
    public async Task Live_tickets_are_listed_with_the_mint_that_made_them()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        SubmitOutcome mint = await sink.SubmitAsync(Mint(10, 2), Signer, TestContext.Current.CancellationToken);
        sink.LeakTickets(Wallet.ClassicAddress, 1);

        IReadOnlyList<LiveTicket> tickets = await ledger.LiveTicketsAsync(Wallet.ClassicAddress, TestContext.Current.CancellationToken);

        Assert.Equal(
            [new LiveTicket(11, mint.TxHash), new LiveTicket(12, mint.TxHash), new LiveTicket(1_000_000, FakeLedgerSink.UnknownCreator)],
            tickets);
    }

    [Fact]
    public async Task A_ticket_answered_as_gone_is_still_listed_until_its_consumer_validates()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        await sink.SubmitAsync(Mint(10, 1), Signer, TestContext.Current.CancellationToken);
        sink.AnswersGone.Add(11);

        SubmitOutcome outcome = await sink.SubmitAsync(Ride(11), Signer, TestContext.Current.CancellationToken);

        Assert.Equal("tefNO_TICKET", outcome.EngineResult);
        Assert.Contains(await ledger.LiveTicketsAsync(Wallet.ClassicAddress, TestContext.Current.CancellationToken), ticket => ticket.Sequence == 11);
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.XrplClientLedgerSourceTests" -class "XrplFile.Tests.FakeLedgerSinkTests"`
Expected: build FAILS — `LiveTicket`, `LiveTicketsAsync`, `ParseTickets`, `AnswersGone`, `UnknownCreator` do not exist.

- [ ] **Step 4: Add the record and the interface method**

In `src/XrplFile.Core/ILedgerSource.cs`, after the `AccountState` record:

```csharp
/// <summary>
/// One live ticket on an account, as the validated ledger holds it.
/// </summary>
/// <param name="Sequence">The ticket's number: what a transaction names in <c>TicketSequence</c> to spend it.</param>
/// <param name="CreatedBy">
/// The hash of the transaction that created it, read from the Ticket entry's <c>PreviousTxnID</c>.
/// A ticket is created only by <c>TicketCreate</c> and is not modified until it is spent, so this
/// names its mint — which is how a ticket this library minted is told from anybody else's.
/// </param>
public sealed record LiveTicket(uint Sequence, string CreatedBy);
```

In the `ILedgerSource` interface, after `AccountStateAsync`:

```csharp
    /// <summary>
    /// Every live ticket the account holds in the latest validated ledger, with the transaction
    /// that created each one, followed through the node's pagination until the list is complete.
    /// An account the node does not know holds none, which is an answer and not a failure.
    /// </summary>
    Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken);
```

- [ ] **Step 5: Implement it on the client source**

In `src/XrplFile.Core/XrplClientLedgerSource.cs`, after `AccountStateAsync`:

```csharp
    public async Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken)
    {
        List<LiveTicket> tickets = [];
        object? marker = null;

        while (true)
        {
            // Validated, not current: a ticket a strategy picks up or a tidy-up spends has to be one
            // the ledger has committed to, and a ticket an open ledger shows may yet vanish.
            JsonElement? page = await AskOrAbsentAsync(
                new AccountObjectsRequest(account)
                {
                    Type = Xrpl.Models.LedgerEntryType.Ticket,
                    LedgerIndex = new LedgerIndex(LedgerIndexType.Validated),
                    Limit = _pageSize,
                    Marker = marker,
                },
                "account_objects",
                XrplErrorCodes.ActNotFound,
                cancellationToken).ConfigureAwait(false);

            if (page is not { } answer) return tickets;

            tickets.AddRange(ParseTickets(answer));

            // The marker goes back to the node as it came; its shape is the node's business.
            marker = answer.TryGetProperty("marker", out JsonElement next) ? next.Clone() : null;

            if (marker is null) return tickets;
        }
    }

    /// <summary>The Ticket entries of one account_objects page. Anything else, or a ticket missing a field this needs, is skipped.</summary>
    internal static IReadOnlyList<LiveTicket> ParseTickets(JsonElement answer)
    {
        if (!answer.TryGetProperty("account_objects", out JsonElement objects) || objects.ValueKind != JsonValueKind.Array) return [];

        List<LiveTicket> parsed = [];

        foreach (JsonElement entry in objects.EnumerateArray())
        {
            if (!string.Equals(Text(entry, "LedgerEntryType"), "Ticket", StringComparison.Ordinal)) continue;
            if (Number(entry, "TicketSequence") is not { } sequence || Text(entry, "PreviousTxnID") is not { } creator) continue;

            parsed.Add(new LiveTicket((uint)sequence, creator.ToUpperInvariant()));
        }

        return parsed;
    }
```

In the same file, `SendAsync` dispatches by request type and throws for anything it does not list. Without this the real source never sends `account_objects` at all, and the unit tests above cannot notice: they drive the internal delegate, not `SendAsync`. Add the case after the `AccountInfoRequest` arm:

```csharp
            AccountObjectsRequest objects => _client!.GRequest<object, AccountObjectsRequest>(objects, cancellationToken),
```

and change the default arm's message to `"this source sends tx, account_tx, server_info, server_state, account_info, account_objects and ledger, nothing else"`.

- [ ] **Step 6: Implement the listing on the fake source**

In `tests/XrplFile.TestSupport/FakeLedgerSource.cs`, add fields after `_alsoAffects`:

```csharp
    private readonly Dictionary<string, IReadOnlyList<LiveTicket>> _liveTickets = new(StringComparer.Ordinal);

    private int _transactionLookups;
```

Add public members after the `TicketCounts` property:

```csharp
    /// <summary>
    /// How many times <see cref="TransactionAsync"/> was asked. Lets a test hold the library to one
    /// creator lookup per mint rather than one per ticket.
    /// </summary>
    public int TransactionLookups => Volatile.Read(ref _transactionLookups);

    /// <summary>
    /// Replaces what <see cref="LiveTicketsAsync"/> answers for an account. <see cref="FakeLedgerSink"/>
    /// calls it whenever it mints or spends a ticket; a test that has no sink sets it directly.
    /// </summary>
    public void SetLiveTickets(string account, IReadOnlyList<LiveTicket> tickets)
    {
        lock (_gate) _liveTickets[account] = tickets;
    }

    public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_liveTickets.TryGetValue(account, out IReadOnlyList<LiveTicket>? tickets) ? tickets : []);
        }
    }
```

In `TransactionAsync`, add as the first statement of the method body (before `lock (_gate)`):

```csharp
        Interlocked.Increment(ref _transactionLookups);
```

- [ ] **Step 7: Make the fake sink list ticket creators and answer like a node**

In `tests/XrplFile.TestSupport/FakeLedgerSink.cs`:

Add after the `_signatures` field:

```csharp
    /// <summary>Which transaction created each ticket this fake has ever minted or been given.</summary>
    private readonly Dictionary<uint, string> _creators = [];

    /// <summary>Tickets a transaction has ridden. A node answers tefNO_TICKET for any of them afterwards.</summary>
    private readonly HashSet<uint> _consumed = [];
```

Add after the `LiveTickets` property:

```csharp
    /// <summary>
    /// What the listing names as the creator of a ticket that has none on this fake ledger — a hash
    /// nothing here holds, which is exactly what a node with truncated history shows.
    /// </summary>
    public const string UnknownCreator = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    /// <summary>
    /// Tickets the node answers <c>tefNO_TICKET</c> for while the validated ledger still lists them:
    /// consumed in an open ledger by a transaction that has not validated. Spec I5's harder case.
    /// </summary>
    public HashSet<uint> AnswersGone { get; } = [];
```

In `LeakTickets`, replace `ledger.TicketCounts[account] = LiveTickets.Count;` with:

```csharp
            PublishTickets(account);
```

Add this private method at the end of the class:

```csharp
    /// <summary>Keeps the fake source's two views of this account's tickets — the count and the listing — in step. Called under <see cref="_gate"/>.</summary>
    private void PublishTickets(string account)
    {
        ledger.TicketCounts[account] = LiveTickets.Count;
        ledger.SetLiveTickets(account, [.. LiveTickets.Order().Select(ticket => new LiveTicket(ticket, _creators.GetValueOrDefault(ticket, UnknownCreator)))]);
    }
```

In `Answer`, in the `TicketCreate` branch, replace

```csharp
                if (!mintFailed)
                {
                    for (uint ticket = at + 1; ticket <= at + count; ticket++) LiveTickets.Add(ticket);
                }

                _nextSequence = mintFailed ? at + 1 : at + count + 1;
                ledger.TicketCounts[(string)transaction["Account"]] = LiveTickets.Count;
```

with

```csharp
                if (!mintFailed)
                {
                    for (uint ticket = at + 1; ticket <= at + count; ticket++)
                    {
                        LiveTickets.Add(ticket);
                        _creators[ticket] = hash;
                    }
                }

                _nextSequence = mintFailed ? at + 1 : at + count + 1;
                PublishTickets((string)transaction["Account"]);
```

In the `TicketSequence` branch, replace

```csharp
                // A node refuses a ticket it does not hold, which is what terPRE_TICKET says.
                if (!LiveTickets.Remove(ticket)) return new SubmitOutcome("terPRE_TICKET", hash);

                ledger.TicketCounts[(string)transaction["Account"]] = LiveTickets.Count;
```

with

```csharp
                if (AnswersGone.Contains(ticket)) return new SubmitOutcome("tefNO_TICKET", hash);

                // rippled's own split for a ticket it does not hold: one that was consumed, or that
                // lies below the account's sequence, is gone (tefNO_TICKET, measured on xrpld 3.3.0);
                // one at or above the sequence has not been created yet (terPRE_TICKET).
                if (!LiveTickets.Remove(ticket))
                {
                    return new SubmitOutcome(
                        _consumed.Contains(ticket) || ticket < _nextSequence ? "tefNO_TICKET" : "terPRE_TICKET", hash);
                }

                _consumed.Add(ticket);
                PublishTickets((string)transaction["Account"]);
```

- [ ] **Step 8: Delegate the new member in the two source decorators**

In `tests/XrplFile.App.Core.Tests/PublicationRunTests.cs`, inside `ClockAdvancingSource`, and in `tests/XrplFile.App.Core.Tests/ReadRunTests.cs`, inside `GatedAccountTransactionsSource`, add:

```csharp
        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) =>
            inner.LiveTicketsAsync(account, cancellationToken);
```

- [ ] **Step 9: Run the tests to verify they pass, then both suites**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.XrplClientLedgerSourceTests" -class "XrplFile.Tests.FakeLedgerSinkTests"`
Expected: PASS.

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` and `dotnet run --project tests/XrplFile.App.Core.Tests`
Expected: PASS. If a test outside this task now fails because the fake answers `tefNO_TICKET` where it used to answer `terPRE_TICKET`, stop and report it: that test was relying on the fake being more forgiving than the node.

- [ ] **Step 10: Prove the listing against a node**

The unit tests above go through the internal request delegate and cannot see whether the public constructor's `SendAsync` sends `account_objects` at all. Create `tests/XrplFile.Core.Tests/LiveTicketsStandaloneTests.cs`:

```csharp
using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>The ticket listing against a node: the request shape, the pagination and the creator field are the node's, not a script's.</summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class LiveTicketsStandaloneTests
{
    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    [Fact]
    public async Task A_minted_ticket_is_listed_with_the_mint_that_made_it()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet wallet = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);
            NetworkFees fees = await source.FeesAsync(token);

            Assert.Empty(await source.LiveTicketsAsync(wallet.ClassicAddress, token));

            await StandaloneFixture.MintTicketsAsync(sink, source, wallet, 3, fees.OpenLedgerFeeDrops, token);

            IReadOnlyList<LiveTicket> tickets = await source.LiveTicketsAsync(wallet.ClassicAddress, token);

            Assert.Equal(3, tickets.Count);
            Assert.Single(tickets.Select(ticket => ticket.CreatedBy).Distinct());

            LedgerTransaction? mint = await source.TransactionAsync(tickets[0].CreatedBy, token);
            Assert.NotNull(mint);
            Assert.Equal(wallet.ClassicAddress, mint!.Account);
            Assert.True(mint.Succeeded);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
```

Run (stand up — see Task 10, Step 1): `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.LiveTicketsStandaloneTests"`
Expected: PASS. Without the `SendAsync` case it fails with `ArgumentOutOfRangeException`.

- [ ] **Step 11: Commit**

```bash
git add src/XrplFile.Core/ILedgerSource.cs src/XrplFile.Core/XrplClientLedgerSource.cs tests/XrplFile.TestSupport/FakeLedgerSource.cs tests/XrplFile.TestSupport/FakeLedgerSink.cs tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs tests/XrplFile.Core.Tests/FakeLedgerSinkTests.cs tests/XrplFile.Core.Tests/LiveTicketsStandaloneTests.cs tests/XrplFile.App.Core.Tests/PublicationRunTests.cs tests/XrplFile.App.Core.Tests/ReadRunTests.cs
git commit -m "feat(core): list an account's live tickets with the mint that made each"
```

---

### Task 2: Marking this library's mints and telling its tickets apart

**Files:**
- Modify: `src/XrplFile.Core/MemoLayout.cs`
- Modify: `src/XrplFile.Core/XrplFile.Core.csproj`
- Create: `src/XrplFile.Core/LeftoverTickets.cs`
- Modify: `src/XrplFile.Core/TicketStrategy.cs` (the `TicketCreate` dictionary only)
- Modify: `tests/XrplFile.TestSupport/FakeLedgerSink.cs`
- Test: create `tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs`; modify `tests/XrplFile.Core.Tests/TicketStrategyTests.cs`, `tests/XrplFile.Core.Tests/FakeLedgerSinkTests.cs`

**Interfaces:**
- Consumes: `LiveTicket`, `ILedgerSource.LiveTicketsAsync`, `FakeLedgerSource.SetLiveTickets`, `FakeLedgerSource.TransactionLookups`, `FakeLedgerSink.UnknownCreator` (Task 1).
- Produces: `internal const string MemoLayout.TicketMarker = "xf/t"`, `internal static string MemoLayout.TicketMarkerHex`; `internal sealed record TicketCensus(IReadOnlyList<uint> Ours, int Others)` with `TicketCensus.Empty`; `internal static Task<TicketCensus> LeftoverTickets.FindAsync(ILedgerSource source, string account, IDictionary<string, bool>? verdicts, CancellationToken cancellationToken)` — `Ours` sorted ascending; `verdicts` is an optional per-caller cache of creator hash → "is ours". `FakeLedgerSink.LeaveTickets(string account, int count, bool marked)` returning `IReadOnlyList<uint>` (ascending; the leftover mint takes the fake's next sequence, so the tickets are numbered as a real mint's would be and the sequence moves past them). The fake sink records a `TicketCreate`'s first memo on the ledger copy.

- [ ] **Step 1: Write the failing census tests**

Create `tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// Which of an account's live tickets this library may touch (spec I2, I3). Everything here errs
/// toward leaving a ticket alone: the cost of a wrong "ours" is somebody else's prepared
/// transaction, the cost of a wrong "foreign" is one owner reserve until the account is closed.
/// </summary>
public class LeftoverTicketsTests
{
    private const string Account = "rWriter";

    private static string Hash(char fill) => new(fill, 64);

    private static LedgerTransaction Mint(string hash, string account = Account, string? memoType = null, bool succeeded = true, bool validated = true) =>
        new(hash, 100, account, succeeded, validated, succeeded ? "tesSUCCESS" : "tecINSUFFICIENT_RESERVE", memoType, null, null);

    [Fact]
    public async Task Only_tickets_from_a_marked_mint_by_this_account_are_ours()
    {
        FakeLedgerSource ledger = new();
        ledger.Add(Mint(Hash('A'), memoType: MemoLayout.TicketMarkerHex));
        ledger.Add(Mint(Hash('B')));
        ledger.Add(Mint(Hash('C'), account: "rSomebodyElse", memoType: MemoLayout.TicketMarkerHex));
        ledger.Add(Mint(Hash('D'), memoType: MemoLayout.TicketMarkerHex, succeeded: false));
        ledger.Add(Mint(Hash('E'), memoType: MemoLayout.TicketMarkerHex, validated: false));
        ledger.SetLiveTickets(Account,
        [
            new LiveTicket(12, Hash('A')), new LiveTicket(11, Hash('A')),
            new LiveTicket(20, Hash('B')),
            new LiveTicket(30, Hash('C')),
            new LiveTicket(40, Hash('D')),
            new LiveTicket(50, Hash('E')),
            new LiveTicket(60, FakeLedgerSink.UnknownCreator),
        ]);

        TicketCensus census = await LeftoverTickets.FindAsync(ledger, Account, null, TestContext.Current.CancellationToken);

        Assert.Equal([11u, 12u], census.Ours);
        Assert.Equal(5, census.Others);
    }

    [Fact]
    public async Task One_mint_is_looked_up_once_however_many_tickets_it_made()
    {
        FakeLedgerSource ledger = new();
        ledger.Add(Mint(Hash('A'), memoType: MemoLayout.TicketMarkerHex));
        ledger.SetLiveTickets(Account, [.. Enumerable.Range(1, 5).Select(i => new LiveTicket((uint)i, Hash('A')))]);

        await LeftoverTickets.FindAsync(ledger, Account, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, ledger.TransactionLookups);
    }

    [Fact]
    public async Task A_verdict_already_in_the_cache_is_not_looked_up_again()
    {
        FakeLedgerSource ledger = new();
        ledger.Add(Mint(Hash('A'), memoType: MemoLayout.TicketMarkerHex));
        ledger.SetLiveTickets(Account, [new LiveTicket(1, Hash('A'))]);
        Dictionary<string, bool> verdicts = new(StringComparer.OrdinalIgnoreCase);

        await LeftoverTickets.FindAsync(ledger, Account, verdicts, TestContext.Current.CancellationToken);
        TicketCensus second = await LeftoverTickets.FindAsync(ledger, Account, verdicts, TestContext.Current.CancellationToken);

        Assert.Equal(1, ledger.TransactionLookups);
        Assert.Equal([1u], second.Ours);
    }

    [Fact]
    public async Task An_account_with_no_tickets_has_an_empty_census_and_costs_no_lookup()
    {
        FakeLedgerSource ledger = new();

        TicketCensus census = await LeftoverTickets.FindAsync(ledger, Account, null, TestContext.Current.CancellationToken);

        Assert.Empty(census.Ours);
        Assert.Equal(0, census.Others);
        Assert.Equal(0, ledger.TransactionLookups);
    }
}
```

- [ ] **Step 2: Write the failing tests for the marked mint**

Append to `tests/XrplFile.Core.Tests/TicketStrategyTests.cs`, inside the class (add `using System.Text.Json;` at the top of the file):

```csharp
    [Fact]
    public async Task Every_mint_carries_the_librarys_mark_and_nothing_else()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        await strategy.SubmitAllAsync(Memos(2), null, TestContext.Current.CancellationToken);

        Dictionary<string, object> mint = sink.Submitted.Single(tx => (string)tx["TransactionType"] == "TicketCreate");
        JsonElement memo = Assert.Single(JsonSerializer.SerializeToElement(mint["Memos"]).EnumerateArray()).GetProperty("Memo");

        Assert.Equal(MemoLayout.TicketMarkerHex, memo.GetProperty("MemoType").GetString());
        Assert.False(memo.TryGetProperty("MemoData", out _));
        Assert.False(memo.TryGetProperty("MemoFormat", out _));
    }
```

Append to `tests/XrplFile.Core.Tests/FakeLedgerSinkTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task Tickets_a_marked_mint_made_are_found_as_ours()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        Dictionary<string, object> mint = Mint(10, 2);
        mint["Memos"] = new List<object>
        {
            new Dictionary<string, object> { ["Memo"] = new Dictionary<string, object> { ["MemoType"] = MemoLayout.TicketMarkerHex } },
        };
        await sink.SubmitAsync(mint, Signer, TestContext.Current.CancellationToken);

        TicketCensus census = await LeftoverTickets.FindAsync(ledger, Wallet.ClassicAddress, null, TestContext.Current.CancellationToken);

        Assert.Equal([11u, 12u], census.Ours);
    }

    [Fact]
    public async Task Tickets_left_marked_are_ours_and_tickets_left_unmarked_are_not()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        IReadOnlyList<uint> marked = sink.LeaveTickets(Wallet.ClassicAddress, 3, marked: true);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: false);

        TicketCensus census = await LeftoverTickets.FindAsync(ledger, Wallet.ClassicAddress, null, TestContext.Current.CancellationToken);

        Assert.Equal(marked, census.Ours);
        Assert.Equal(2, census.Others);
        Assert.Equal(5, ledger.TicketCounts[Wallet.ClassicAddress]);
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.LeftoverTicketsTests" -class "XrplFile.Tests.TicketStrategyTests" -class "XrplFile.Tests.FakeLedgerSinkTests"`
Expected: build FAILS — `MemoLayout.TicketMarkerHex`, `LeftoverTickets`, `TicketCensus`, `LeaveTickets` do not exist.

- [ ] **Step 4: Add the marker**

In `src/XrplFile.Core/MemoLayout.cs`, after `public const string ManifestMarker = "xf/m";`:

```csharp
    /// <summary>
    /// What every TicketCreate this library submits carries, and what tells its tickets from
    /// anybody else's (spec I2). Internal on purpose: no reader and no other component needs it.
    /// </summary>
    internal const string TicketMarker = "xf/t";
```

After `public static string ManifestMarkerHex { get; } = Utf8Hex(ManifestMarker);`:

```csharp
    internal static string TicketMarkerHex { get; } = Utf8Hex(TicketMarker);
```

In `src/XrplFile.Core/XrplFile.Core.csproj`, next to `<InternalsVisibleTo Include="XrplFile.Core.Tests" />`:

```xml
    <InternalsVisibleTo Include="XrplFile.TestSupport" />
```

- [ ] **Step 5: Mark the mint**

In `src/XrplFile.Core/TicketStrategy.cs`, `EnsureTicketsAsync`, add one entry to the `TicketCreate` dictionary after `["SigningPubKey"] = _signer.PublicKey,`:

```csharp
                // The mark is how a later publication — or this one's tidy-up — knows these
                // tickets are this library's to spend (spec I2). It carries no data.
                ["Memos"] = new List<object>
                {
                    new Dictionary<string, object> { ["Memo"] = new Dictionary<string, object> { ["MemoType"] = MemoLayout.TicketMarkerHex } },
                },
```

- [ ] **Step 6: Write the census**

Create `src/XrplFile.Core/LeftoverTickets.cs`:

```csharp
namespace XrplFile;

/// <summary>An account's live tickets, split into the ones this library minted and everybody else's.</summary>
/// <param name="Ours">This library's tickets, ascending.</param>
/// <param name="Others">Every other live ticket: unmarked, minted by another account, or minted by a transaction the node cannot show.</param>
internal sealed record TicketCensus(IReadOnlyList<uint> Ours, int Others)
{
    public static TicketCensus Empty { get; } = new([], 0);
}

/// <summary>
/// Finding the tickets this library left on an account (spec 4.2), shared by the ticket
/// strategy's pick-up, the estimate and the tidy-up so the three cannot disagree about which
/// tickets are whose.
/// </summary>
internal static class LeftoverTickets
{
    /// <summary>
    /// Lists the account's live tickets and keeps the ones whose creating transaction carries this
    /// library's mark. One lookup per creator, so one per mint of up to 250 tickets.
    /// </summary>
    /// <param name="verdicts">
    /// Creator hash → whether it is this library's mint, kept by a caller that looks more than once.
    /// Null for a caller that wants every creator checked afresh.
    /// </param>
    public static async Task<TicketCensus> FindAsync(
        ILedgerSource source, string account, IDictionary<string, bool>? verdicts, CancellationToken cancellationToken)
    {
        IReadOnlyList<LiveTicket> live = await source.LiveTicketsAsync(account, cancellationToken).ConfigureAwait(false);

        if (live.Count == 0) return TicketCensus.Empty;

        List<uint> ours = [];
        int others = 0;

        foreach (IGrouping<string, LiveTicket> mint in live.GroupBy(ticket => ticket.CreatedBy, StringComparer.OrdinalIgnoreCase))
        {
            if (verdicts is null || !verdicts.TryGetValue(mint.Key, out bool isOurs))
            {
                LedgerTransaction? creator = await source.TransactionAsync(mint.Key, cancellationToken).ConfigureAwait(false);
                isOurs = IsOurMint(creator, account);

                if (verdicts is not null) verdicts[mint.Key] = isOurs;
            }

            if (isOurs) ours.AddRange(mint.Select(ticket => ticket.Sequence));
            else others += mint.Count();
        }

        ours.Sort();

        return new TicketCensus(ours, others);
    }

    /// <summary>
    /// A creator the node cannot return, or cannot show as a validated success signed by this
    /// account and carrying the mark, makes its tickets somebody else's (spec I3).
    /// </summary>
    private static bool IsOurMint(LedgerTransaction? creator, string account) =>
        creator is { Validated: true, Succeeded: true }
        && string.Equals(creator.Account, account, StringComparison.Ordinal)
        && string.Equals(creator.MemoTypeHex, MemoLayout.TicketMarkerHex, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 7: Let the fake sink record a mint's memo and leave tickets behind**

In `tests/XrplFile.TestSupport/FakeLedgerSink.cs`:

Add after the `_consumed` field:

```csharp
Add after `LeakTickets`:

```csharp
    /// <summary>
    /// Puts tickets on the account from a mint already validated in an earlier ledger, the way a
    /// stopped run leaves them: <paramref name="marked"/> says whether that mint carried this
    /// library's mark. The mint takes the account's next sequence exactly as a real one does, so
    /// its tickets lie below the sequence afterwards, as a node's would. Closes a ledger, as the
    /// mint's validation did, so a sweep anchored after it cannot meet the mint's own ledger.
    /// </summary>
    public IReadOnlyList<uint> LeaveTickets(string account, int count, bool marked)
    {
        lock (_gate)
        {
            uint at = _nextSequence;
            uint first = at + 1;
            _nextSequence = at + (uint)count + 1;

            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"fake:leftover:{at}")));

            ledger.Add(new LedgerTransaction(
                hash, firstLedger + _submitted.Count / 100 + _ledgersClosed, account,
                Succeeded: true, Validated: true, Result: "tesSUCCESS",
                MemoTypeHex: marked ? MemoLayout.TicketMarkerHex : null, MemoDataHex: null, MemoFormatHex: null));

            List<uint> tickets = new(count);

            for (uint ticket = first; ticket < first + (uint)count; ticket++)
            {
                LiveTickets.Add(ticket);
                _creators[ticket] = hash;
                tickets.Add(ticket);
            }

            _ledgersClosed++;
            PublishTickets(account);

            return tickets;
        }
    }
```

In `Answer`, move the memo parsing into a helper so the `TicketCreate` branch can use it. Add this private static method at the end of the class:

```csharp
    private static (string? Type, string? Data, string? Format) FirstMemo(Dictionary<string, object> transaction)
    {
        if (!transaction.TryGetValue("Memos", out object? memos)) return (null, null, null);

        JsonElement first = JsonSerializer.SerializeToElement(memos)[0].GetProperty("Memo");

        return (
            first.TryGetProperty("MemoType", out JsonElement t) ? t.GetString() : null,
            first.TryGetProperty("MemoData", out JsonElement d) ? d.GetString() : null,
            first.TryGetProperty("MemoFormat", out JsonElement f) ? f.GetString() : null);
    }
```

Replace the existing block

```csharp
            string? memoType = null, memoData = null, memoFormat = null;
            if (transaction.TryGetValue("Memos", out object? memos))
            {
                JsonElement first = JsonSerializer.SerializeToElement(memos)[0].GetProperty("Memo");
                memoType = first.TryGetProperty("MemoType", out JsonElement t) ? t.GetString() : null;
                memoData = first.TryGetProperty("MemoData", out JsonElement d) ? d.GetString() : null;
                memoFormat = first.TryGetProperty("MemoFormat", out JsonElement f) ? f.GetString() : null;
            }
```

with

```csharp
            (string? memoType, string? memoData, string? memoFormat) = FirstMemo(transaction);
```

In the `TicketCreate` branch, in the `ledger.Add(new LedgerTransaction(...))` call, replace `MemoTypeHex: null, MemoDataHex: null, MemoFormatHex: null));` with:

```csharp
                    MemoTypeHex: FirstMemo(transaction).Type, MemoDataHex: null, MemoFormatHex: null));
```

- [ ] **Step 8: Run the tests to verify they pass, then the suite**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.LeftoverTicketsTests" -class "XrplFile.Tests.TicketStrategyTests" -class "XrplFile.Tests.FakeLedgerSinkTests"`
Expected: PASS.

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/XrplFile.Core/MemoLayout.cs src/XrplFile.Core/XrplFile.Core.csproj src/XrplFile.Core/LeftoverTickets.cs src/XrplFile.Core/TicketStrategy.cs tests/XrplFile.TestSupport/FakeLedgerSink.cs tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs tests/XrplFile.Core.Tests/TicketStrategyTests.cs tests/XrplFile.Core.Tests/FakeLedgerSinkTests.cs
git commit -m "feat(core): mark every ticket mint and tell this library's tickets from others"
```

---

### Task 3: Picking up leftovers and dropping tickets that are gone

**Files:**
- Modify: `src/XrplFile.Core/TicketStrategy.cs`, `src/XrplFile.Core/XrplClientLedgerSink.cs` (one condition)
- Test: `tests/XrplFile.Core.Tests/TicketStrategyTests.cs`, `tests/XrplFile.Core.Tests/XrplClientLedgerSinkTests.cs`

**Interfaces:**
- Consumes: `LeftoverTickets.FindAsync`, `TicketCensus` (Task 2); `FakeLedgerSink.LeaveTickets`, `FakeLedgerSink.AnswersGone`, `FakeLedgerSink.LeakTickets` (Tasks 1–2).
- Produces: no new public surface. Internal behaviour later tasks rely on: a `TicketStrategy` instance never rides a ticket it has seen answer `tefNO_TICKET`; picked-up tickets are ridden in ascending order.

- [ ] **Step 1: Write the failing tests**

Append to `tests/XrplFile.Core.Tests/TicketStrategyTests.cs`, inside the class:

```csharp
    private static uint[] Ridden(FakeLedgerSink sink) =>
        [.. sink.Submitted.Where(tx => tx.ContainsKey("TicketSequence")).Select(tx => Convert.ToUInt32(tx["TicketSequence"]))];

    private static uint[] Minted(FakeLedgerSink sink) =>
        [.. sink.Submitted.Where(tx => (string)tx["TransactionType"] == "TicketCreate").Select(tx => Convert.ToUInt32(tx["TicketCount"]))];

    [Fact]
    public async Task A_strategy_spends_this_librarys_leftovers_before_it_mints()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> leftovers = sink.LeaveTickets(Wallet.ClassicAddress, 3, marked: true);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(5), null, TestContext.Current.CancellationToken);

        Assert.Equal(5, landed.Count);
        Assert.All(leftovers, ticket => Assert.Contains(ticket, Ridden(sink)));
        // The mint covered only the shortfall.
        Assert.Equal([2u], Minted(sink));
        Assert.Empty(sink.LiveTickets);
    }

    [Fact]
    public async Task Unmarked_tickets_and_tickets_of_unknown_origin_are_left_alone()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> unmarked = sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: false);
        sink.LeakTickets(Wallet.ClassicAddress, 2);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);

        uint[] ridden = Ridden(sink);
        Assert.All(unmarked, ticket => Assert.DoesNotContain(ticket, ridden));
        Assert.DoesNotContain(1_000_000u, ridden);
        Assert.DoesNotContain(1_000_001u, ridden);
        Assert.Equal([3u], Minted(sink));
        Assert.Equal(4, sink.LiveTickets.Count);
    }

    [Fact]
    public async Task Tickets_picked_up_by_one_call_are_never_picked_up_again_by_the_next()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 5, marked: true);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        // Picks up all five, spends two, and keeps three in hand.
        await strategy.SubmitAllAsync(Memos(2), null, TestContext.Current.CancellationToken);

        // A stopped run's mint that validated after this instance first looked (spec I4).
        sink.LeaveTickets(Wallet.ClassicAddress, 4, marked: true);

        await strategy.SubmitAllAsync(Memos(7, from: 2), null, TestContext.Current.CancellationToken);

        uint[] ridden = Ridden(sink);
        Assert.Equal(9, ridden.Length);
        Assert.Equal(ridden.Length, ridden.Distinct().Count());
        Assert.Empty(Minted(sink));
        Assert.Empty(sink.LiveTickets);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_ticket_that_is_gone_costs_its_memo_no_attempt_and_is_not_picked_up_again()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> leftovers = sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        sink.AnswersGone.Add(leftovers[0]);

        // One attempt per memo, and a batch timeout far beyond the test's own: a strategy that
        // counted the gone ticket as an attempt fails, and one that swept for the memo the gone
        // ticket never carried times the test out.
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            TicketsPerBatch = 100,
            MaxAttemptsPerMemo = 1,
            SweepPoll = TimeSpan.FromMilliseconds(1),
            BatchTimeout = TimeSpan.FromMinutes(1),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(1), null, TestContext.Current.CancellationToken);
        Assert.Single(landed);

        // The gone ticket is still listed, so the next call's look finds it again — and must not ride it.
        sink.CloseLedger();
        await strategy.SubmitAllAsync(Memos(1, from: 1), null, TestContext.Current.CancellationToken);

        Assert.Equal(1, Ridden(sink).Count(ticket => ticket == leftovers[0]));
    }

    [Fact(Timeout = 10_000)]
    public async Task A_memo_that_draws_several_gone_tickets_in_a_row_still_lands()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> leftovers = sink.LeaveTickets(Wallet.ClassicAddress, 4, marked: true);
        sink.AnswersGone.UnionWith(leftovers.Take(3));

        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            TicketsPerBatch = 100,
            MaxAttemptsPerMemo = 1,
            SweepPoll = TimeSpan.FromMilliseconds(1),
            BatchTimeout = TimeSpan.FromMinutes(1),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(1), null, TestContext.Current.CancellationToken);

        Assert.Single(landed);
        Assert.Equal([.. leftovers], Ridden(sink));
        Assert.Empty(Minted(sink));
    }

    [Fact]
    public async Task A_dispatched_ticket_still_listed_is_never_picked_up_again()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> leftovers = sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);

        // Position 0 is the first memo, riding the first leftover: the node answers terQUEUED and
        // never applies it, so that ticket is dispatched and still listed.
        sink.Refusals[0] = "terQUEUED";

        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            TicketsPerBatch = 100,
            SweepPoll = TimeSpan.FromMilliseconds(1),
            BatchTimeout = TimeSpan.FromMilliseconds(100),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

        await strategy.SubmitAllAsync(Memos(1), null, TestContext.Current.CancellationToken);

        // One more leftover makes the ledger hold more than the instance accounts for, so the next
        // call looks again — and finds the dispatched ticket listed among this library's.
        sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        await strategy.SubmitAllAsync(Memos(1, from: 1), null, TestContext.Current.CancellationToken);

        Assert.Equal(1, Ridden(sink).Count(ticket => ticket == leftovers[0]));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.TicketStrategyTests"`
Expected: five of the six new tests FAIL, each on its assertions about which tickets were ridden or minted: before this task nothing is picked up and a mint covers every memo. `Unmarked_tickets_and_tickets_of_unknown_origin_are_left_alone` already PASSES — it is a regression guard for the pick-up this task adds, not a test of something missing.

- [ ] **Step 3: Add the gone set, the verdict cache and the outcome type**

In `src/XrplFile.Core/TicketStrategy.cs`, after the `_dispatched` field:

```csharp
    /// <summary>
    /// Tickets the node answered <c>tefNO_TICKET</c> for. They never go back in the queue, and a
    /// later look never picks them up again, even while the validated ledger still lists them
    /// (spec I5): that is what keeps the strategy from cycling on a ticket consumed in an open
    /// ledger by a transaction that has not validated. The tidy-up decides what became of them.
    /// </summary>
    private readonly HashSet<uint> _gone = [];

    /// <summary>Which mints this instance has already judged ours or somebody else's, so a second look costs no lookups.</summary>
    private readonly Dictionary<string, bool> _creatorVerdicts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What one submission did to the ticket it rode.</summary>
    private enum TicketFate
    {
        /// <summary>The node refused it before any ledger saw it: the ticket is untouched and still ours.</summary>
        Intact,

        /// <summary>The node took it: the ticket is presumed spent until the sweep says otherwise.</summary>
        Spent,

        /// <summary>The node answered <c>tefNO_TICKET</c>: somebody else's transaction has the ticket.</summary>
        Gone,
    }

    /// <summary>Which ticket each memo of a batch rode, and which memos met a ticket that was gone.</summary>
    private sealed record BatchSend(IReadOnlyDictionary<int, uint> Ridden, IReadOnlyCollection<int> Gone);
```

Replace the `SweepResult` record with:

```csharp
    /// <summary>What a sweep learned about one batch: which markers wrote, and which reached a ledger at all.</summary>
    private sealed record SweepResult(IReadOnlyDictionary<string, string> Landed, IReadOnlySet<string> Seen)
    {
        public static SweepResult Empty { get; } = new(new Dictionary<string, string>(), new HashSet<string>());
    }
```

Add after `ForgetTicket`:

```csharp
    /// <summary>Drops a ticket the node said is gone, for good (spec I5).</summary>
    private void DropGoneTicket(uint ticket)
    {
        lock (_ticketLock)
        {
            _dispatched.Remove(ticket);
            _gone.Add(ticket);
        }
    }

    /// <summary>
    /// Takes into the queue this library's live tickets that this instance does not already hold,
    /// has not dispatched and has not found gone (spec I4), lowest first. Returns how many it took.
    /// </summary>
    private async Task<int> AdoptLeftoversAsync(string account, CancellationToken cancellationToken)
    {
        TicketCensus census = await LeftoverTickets.FindAsync(_source, account, _creatorVerdicts, cancellationToken).ConfigureAwait(false);

        lock (_ticketLock)
        {
            HashSet<uint> known = [.. _tickets, .. _dispatched, .. _gone];
            int adopted = 0;

            foreach (uint ticket in census.Ours)
            {
                if (known.Contains(ticket)) continue;

                _tickets.Enqueue(ticket);
                adopted++;
            }

            return adopted;
        }
    }
```

In `DisposeAsync`, inside the lock, add `_gone.Clear();`, and replace its comment with:

```csharp
        // Dropping the queue is all this can do. A ticket still in it stays live on the account,
        // costing an owner reserve, because no transaction cancels a ticket. It is not lost: it
        // carries this library's mark, so the next publication from this account picks it up or
        // spends it in its tidy-up (spec I4, I6) — and deleting the account removes it regardless.
```

- [ ] **Step 4: Pick up before minting**

In `EnsureTicketsAsync`, replace

```csharp
            AccountState state = await _source.AccountStateAsync(account, cancellationToken).ConfigureAwait(false);
            int live = Math.Max(state.TicketCount, LiveTicketCount);
```

with

```csharp
            AccountState state = await _source.AccountStateAsync(account, cancellationToken).ConfigureAwait(false);

            // The ledger holds more live tickets than this instance accounts for: some of them may be
            // this library's, left by a run that stopped, or minted by one whose mint validated after
            // this instance last looked (spec I4). Taking them first means the mint below covers only
            // the shortfall. Nothing new taken means the rest are somebody else's, and the mint goes on.
            if (state.TicketCount > LiveTicketCount
                && await AdoptLeftoversAsync(account, cancellationToken).ConfigureAwait(false) > 0)
            {
                continue;
            }

            int live = Math.Max(state.TicketCount, LiveTicketCount);
```

- [ ] **Step 5: Report a gone ticket and take it out of circulation**

Replace `SendOneAsync`'s signature, doc comment and its two `return`-relevant parts so the method reads:

```csharp
    /// <summary>
    /// Sends one memo on one ticket and says what became of the ticket: spent when the node took
    /// the submission, gone when the node says another transaction already has it, intact when the
    /// node refused it before any ledger saw it.
    /// </summary>
    private async Task<TicketFate> SendOneAsync(MemoSubmission memo, uint ticket, CancellationToken cancellationToken)
```

Keep the body up to and including the `terPRE_TICKET` check unchanged. Immediately after that check add:

```csharp
        // The ticket is not in the ledger and lies below the account's sequence: some other
        // transaction consumed it — a stopped run's straggler, or this strategy's own earlier
        // submission whose answer was lost. A tef is never applied, so this costs no fee (spec I5).
        if (string.Equals(outcome.EngineResult, "tefNO_TICKET", StringComparison.Ordinal)) return TicketFate.Gone;
```

Replace the final `return` expression with:

```csharp
        return outcome.EngineResult.StartsWith("tes", StringComparison.Ordinal)
            || outcome.EngineResult.StartsWith("tec", StringComparison.Ordinal)
            || string.Equals(outcome.EngineResult, "terQUEUED", StringComparison.Ordinal)
            ? TicketFate.Spent
            : TicketFate.Intact;
```

In the comment above it, change "Everything else — tem, tef, tel," to "Everything else — tem, every tef but tefNO_TICKET, tel,".

Replace `SendBatchAsync` with:

```csharp
    /// <summary>Sends one batch in parallel and reports which ticket each memo rode, and which memos met a gone ticket.</summary>
    private async Task<BatchSend> SendBatchAsync(
        IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> batch, CancellationToken cancellationToken)
    {
        using SemaphoreSlim gate = new(_options.MaxInFlight, _options.MaxInFlight);
        ConcurrentDictionary<int, uint> ridden = new();
        ConcurrentBag<int> gone = [];

        IEnumerable<Task> sending = batch.Select(async index =>
        {
            if (!TryTakeTicket(out uint ticket)) return;

            TicketFate fate = TicketFate.Intact;
            bool held = false;

            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                held = true;

                fate = await SendOneAsync(memos[index], ticket, cancellationToken).ConfigureAwait(false);

                if (fate == TicketFate.Spent) ridden[index] = ticket;
                else if (fate == TicketFate.Gone) gone.Add(index);
            }
            finally
            {
                if (held) gate.Release();

                // Everything between the dequeue and a submission the node actually took is
                // covered here: a cancellation while waiting for the gate, a throw out of the node
                // call, a refusal the node made before any ledger saw it. In every one of those
                // the ticket is intact and alive on the account, and nothing on the XRPL will ever
                // cancel it, so the queue is the only place it can go. A gone ticket is the one
                // exception: returning it would circulate it for ever (spec I5).
                if (fate == TicketFate.Gone) DropGoneTicket(ticket);
                else if (fate == TicketFate.Intact) ReturnTicket(ticket);
            }
        });

        await Task.WhenAll(sending).ConfigureAwait(false);

        return new BatchSend(ridden, [.. gone]);
    }
```

- [ ] **Step 6: Give a gone memo its attempt back and keep it out of the sweep**

In `SubmitAllAsync`, replace

```csharp
            IReadOnlyDictionary<int, uint> ridden = await SendBatchAsync(memos, batch, cancellationToken).ConfigureAwait(false);

            SweepResult swept = await SweepAsync(wanted.Keys, sweepFrom.Value, batchFrom, cancellationToken).ConfigureAwait(false);
```

with

```csharp
            BatchSend sent = await SendBatchAsync(memos, batch, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<int, uint> ridden = sent.Ridden;

            // A memo whose ticket was gone was never applied: there is nothing of it to sweep for,
            // and it did nothing to deserve an attempt (spec I5). It stays pending and goes out
            // again on the next ticket.
            foreach (int index in sent.Gone)
            {
                attempts[index]--;
                wanted.Remove(memos[index].MemoTypeHex);
            }

            SweepResult swept = wanted.Count == 0
                ? SweepResult.Empty
                : await SweepAsync(wanted.Keys, sweepFrom.Value, batchFrom, cancellationToken).ConfigureAwait(false);
```

(Task 4 changes the `SweepAsync` call's first argument; leave it as `wanted.Keys` here.)

- [ ] **Step 7: Update the class summary**

In `TicketStrategy`'s class summary, replace the paragraph beginning "Minting is exact:" with:

```csharp
/// Minting is exact: a batch asks for one ticket per memo it is about to send, because nothing on
/// the XRPL cancels a ticket and an unspent one locks an owner reserve for as long as the account
/// lives. Before minting, a batch takes this library's leftover tickets already on the account —
/// all of them, not only as many as it needs — so the queue can carry a surplus from one call to
/// the next; whatever is still unspent when the publication succeeds goes in the tidy-up.
```

- [ ] **Step 8: Let the real sink recognise its own landed blob behind `tefNO_TICKET`**

`XrplClientLedgerSink.SubmitAsync` resubmits the same signed blob after a timeout and already asks the node about its own hash when the answer is `tefPAST_SEQ` or `tefALREADY`. For a ticketed blob whose first copy landed, rippled's preclaim checks the ticket before the duplicate, so the answer can be `tefNO_TICKET`; without the same lookup that memo would be classified gone and sent again, and a tidy-up spend counted already gone instead of released.

Append to `tests/XrplFile.Core.Tests/XrplClientLedgerSinkTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task A_ticket_gone_because_this_very_blob_landed_reports_what_the_blob_did()
    {
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("submit timed out"));
        node.Answer("""{"error":"txnNotFound"}""");
        node.Answer("""{"engine_result":"tefNO_TICKET"}""");
        node.Answer("""{"validated":true,"meta":{"TransactionResult":"tesSUCCESS"}}""");

        SubmitOutcome outcome = await Submit(node);

        Assert.Equal("tesSUCCESS", outcome.EngineResult);
        Assert.Equal(["submit", "tx", "submit", "tx"], node.Requests.Select(r => r.Command).ToArray());
    }

    [Fact]
    public async Task A_ticket_somebody_else_consumed_stays_gone()
    {
        ScriptedNode node = new();
        node.Answer("""{"engine_result":"tefNO_TICKET"}""");
        node.Answer("""{"error":"txnNotFound"}""");

        SubmitOutcome outcome = await Submit(node);

        Assert.Equal("tefNO_TICKET", outcome.EngineResult);
    }
```

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.XrplClientLedgerSinkTests"`
Expected: both new tests FAIL (the sink returns `tefNO_TICKET` without asking about its hash).

In `src/XrplFile.Core/XrplClientLedgerSink.cs`, change `if (outcome is "tefPAST_SEQ" or "tefALREADY")` to:

```csharp
            if (outcome is "tefPAST_SEQ" or "tefALREADY" or "tefNO_TICKET")
```

and extend the comment above it: "A ticketed copy that already landed is answered tefNO_TICKET rather than tefALREADY — the ticket is checked first — so that answer is asked about too; a ticket somebody else consumed leaves the hash unknown and the answer stands."

- [ ] **Step 9: Run the tests to verify they pass, then the suite**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.TicketStrategyTests" -class "XrplFile.Tests.XrplClientLedgerSinkTests"`
Expected: PASS, both classes.

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/XrplFile.Core/TicketStrategy.cs src/XrplFile.Core/XrplClientLedgerSink.cs tests/XrplFile.Core.Tests/TicketStrategyTests.cs tests/XrplFile.Core.Tests/XrplClientLedgerSinkTests.cs
git commit -m "feat(core): spend this library's leftover tickets before minting, drop tickets that are gone"
```

---

### Task 4: A landed memo is matched on its data too

**Files:**
- Modify: `src/XrplFile.Core/TicketStrategy.cs` (`SweepAsync` and its call)
- Test: `tests/XrplFile.Core.Tests/TicketStrategyTests.cs`

**Interfaces:**
- Consumes: the `SubmitAllAsync` loop as left by Task 3 (`wanted` is `Dictionary<string, int>` marker → memo index).
- Produces: no new surface.

- [ ] **Step 1: Write the failing test**

Append to `tests/XrplFile.Core.Tests/TicketStrategyTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task A_transaction_with_the_right_marker_and_other_data_is_not_taken_for_the_memo()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        // Position 0 is the mint, position 1 the memo: what lands under the memo's marker carries
        // other data — a straggler of another file's publication, as far as the sweep can tell.
        // Memos(1) carries "00", so the planted copy must carry anything else.
        sink.StoredAs[1] = _ => "FF";

        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            TicketsPerBatch = 100,
            SweepPoll = TimeSpan.FromMilliseconds(1),
            BatchTimeout = TimeSpan.FromMilliseconds(200),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

        IReadOnlyList<MemoSubmission> memos = Memos(1);
        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(memos, null, TestContext.Current.CancellationToken);

        LedgerTransaction? reported = await ledger.TransactionAsync(Assert.Single(landed).TxHash, TestContext.Current.CancellationToken);
        Assert.Equal(memos[0].MemoDataHex, reported!.MemoDataHex);

        // The copy carrying other data was neither recorded nor counted as seen, so the memo went out again.
        Assert.Equal(2, Ridden(sink).Length);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -method "XrplFile.Tests.TicketStrategyTests.A_transaction_with_the_right_marker_and_other_data_is_not_taken_for_the_memo"`
Expected: FAIL — `reported.MemoDataHex` is `"FF"` and only one memo was ridden.

- [ ] **Step 3: Match marker and data**

In `SubmitAllAsync`, change the `SweepAsync` call to:

```csharp
                : await SweepAsync(
                    wanted.ToDictionary(pair => pair.Key, pair => memos[pair.Value].MemoDataHex, StringComparer.Ordinal),
                    sweepFrom.Value, batchFrom, cancellationToken).ConfigureAwait(false);
```

Replace `SweepAsync`'s signature, summary and the start of its body up to the `while` with:

```csharp
    /// <summary>
    /// Reads the account forward from this call's first possible ledger until every marker has
    /// been seen or the batch's deadline passes. A transaction counts for a memo only when it
    /// carries both the memo's marker and its data (spec I12): a marker names an index, not a
    /// file, and a straggler of another file's publication can wear it. <paramref name="batchFrom"/>
    /// is the same window narrowed to this batch, and it decides only whether a ticket was spent:
    /// an earlier attempt's tec sits below it and was accounted for when that attempt was swept.
    /// </summary>
    /// <param name="markers">Marker → the data the memo wearing it carries.</param>
    private async Task<SweepResult> SweepAsync(
        IReadOnlyDictionary<string, string> markers, long from, long batchFrom, CancellationToken cancellationToken)
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.BatchTimeout;
```

Inside the `await foreach`, replace

```csharp
                if (!tx.Validated || tx.MemoTypeHex is null) continue;
                if (!wanted.Contains(tx.MemoTypeHex)) continue;
```

with

```csharp
                if (!tx.Validated || tx.MemoTypeHex is null) continue;
                if (!markers.TryGetValue(tx.MemoTypeHex, out string? data)) continue;
                if (!string.Equals(tx.MemoDataHex, data, StringComparison.OrdinalIgnoreCase)) continue;
```

and replace `if (found.Count == wanted.Count) return new SweepResult(found, seen);` with:

```csharp
            if (found.Count == markers.Count) return new SweepResult(found, seen);
```

In `SubmitAllAsync`, update the comment above the duplicate-marker refusal: "A memo is recognised in the ledger by its marker and its data, so two memos wearing the same marker could still be told apart by data — but not if their data is equal, and the writer never sends two such memos. Refusing here keeps the ambiguity off the ledger — and keeps a raw ArgumentException out of the contract."

- [ ] **Step 4: Run the tests to verify they pass, then the suite**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.TicketStrategyTests"`
Expected: PASS.

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core/TicketStrategy.cs tests/XrplFile.Core.Tests/TicketStrategyTests.cs
git commit -m "fix(core): a landed memo is matched on its data as well as its marker"
```

---

### Task 5: Spending leftovers after a publication — the strategy member

**Files:**
- Create: `src/XrplFile.Core/TicketRelease.cs`
- Modify: `src/XrplFile.Core/LeftoverTickets.cs` (add `ReleaseAsync`)
- Modify: `src/XrplFile.Core/ISubmissionStrategy.cs`
- Modify: `src/XrplFile.Core/TicketStrategy.cs`
- Modify: `src/XrplFile.Core/SequenceStrategy.cs`
- Modify: `app/XrplFile.App.Core/PublishPolicy.cs` (`Create` only)
- Modify (delegation only): `tests/XrplFile.Core.Tests/XrplFileWriterTests.cs` (`ProbingStrategy`)
- Test: `tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs`, `tests/XrplFile.App.Core.Tests/PublishPolicyTests.cs`

**Interfaces:**
- Consumes: `LeftoverTickets.FindAsync`, `TicketCensus` (Task 2); fake knobs from Tasks 1–2; `FakeLedgerSink.Refusals`, `FailsAs`, `QueueEverything` (existing).
- Produces: `public sealed record TicketRelease(int Released, int AlreadyGone, int NotReleased)` with `TicketRelease.None`; `Task<TicketRelease> ISubmissionStrategy.ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken)` — returns normally when cancelled, counting every unsettled ticket as not released; throws only on a non-cancellation failure. `SequenceStrategy(ILedgerSink sink, ITransactionSigner signer, SequenceStrategyOptions? options = null, ILedgerSource? source = null)`. `internal static Task<TicketRelease> LeftoverTickets.ReleaseAsync(ILedgerSink sink, ILedgerSource source, ITransactionSigner signer, long feeDrops, TimeSpan poll, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs` (add `using Xrpl.Wallet;` at the top), inside the class:

```csharp
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);

    private static TicketStrategy Strategy(FakeLedgerSink sink, FakeLedgerSource ledger) =>
        new(sink, ledger, Signer, new TicketStrategyOptions
        {
            SweepPoll = TimeSpan.FromMilliseconds(1),
            BatchTimeout = TimeSpan.FromSeconds(5),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

    [Fact]
    public async Task The_tidy_up_spends_this_librarys_tickets_on_transactions_that_do_nothing()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 3, marked: true);
        IReadOnlyList<uint> unmarked = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: false);
        sink.LeakTickets(Wallet.ClassicAddress, 2);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(3, 0, 0), release);
        Assert.Equal([unmarked[0], 1_000_000u, 1_000_001u], sink.LiveTickets.Order().ToArray());

        Assert.Equal(3, sink.Submitted.Count);
        Assert.All(sink.Submitted, tx =>
        {
            Assert.Equal("AccountSet", tx["TransactionType"]);
            Assert.Contains(Convert.ToUInt32(tx["TicketSequence"]), ours);
            Assert.False(tx.ContainsKey("Memos"));
            Assert.False(tx.ContainsKey("SetFlag"));
            Assert.False(tx.ContainsKey("ClearFlag"));
        });
    }

    [Fact]
    public async Task An_account_with_no_leftovers_sees_no_tidy_up_transaction()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeakTickets(Wallet.ClassicAddress, 2);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TicketRelease.None, release);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_ticket_the_node_calls_gone_is_counted_as_already_gone()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        sink.AnswersGone.Add(ours[0]);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(1, 1, 0), release);
    }

    [Fact]
    public async Task A_spend_that_validates_as_failed_is_not_a_release_but_the_ticket_is_gone()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.FailsAs.Add(0);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // A tec consumes the ticket without succeeding: nothing short of tesSUCCESS is "released",
        // and a ticket the ledger no longer lists holds no reserve however it went (spec 4.6).
        Assert.Equal(new TicketRelease(0, 1, 0), release);
        Assert.Empty(sink.LiveTickets);
    }

    [Fact]
    public async Task A_spend_the_node_refuses_leaves_the_ticket_not_released()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Refusals[0] = "telINSUF_FEE_P";
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(0, 0, 1), release);
        Assert.Contains(ours[0], sink.LiveTickets);
    }

    [Fact(Timeout = 10_000)]
    public async Task The_tidy_up_stops_at_its_deadline_and_counts_what_did_not_settle()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger) { QueueEverything = true };
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using TicketStrategy strategy = Strategy(sink, ledger);
        using CancellationTokenSource deadline = new(TimeSpan.FromMilliseconds(200));

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(deadline.Token);

        Assert.Equal(new TicketRelease(0, 0, 2), release);
    }

    [Fact]
    public async Task A_sequence_strategy_tidies_up_too_when_it_can_read_the_ledger()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) }, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(2, 0, 0), release);
        Assert.Empty(sink.LiveTickets);
    }

    [Fact]
    public async Task A_sequence_strategy_without_a_ledger_source_releases_nothing()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using SequenceStrategy strategy = new(sink, Signer);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TicketRelease.None, release);
        Assert.Empty(sink.Submitted);
    }
```

Append to `tests/XrplFile.App.Core.Tests/PublishPolicyTests.cs` (add `using Xrpl.Wallet;` and `using XrplFile.Tests;` at the top — `FakeConnectionFactory`'s types and, in Task 9, `FakeLedgerSource` live in `XrplFile.Tests`), inside the class:

```csharp
    [Fact]
    public async Task A_sequence_strategy_built_by_the_policy_tidies_up_leftovers()
    {
        FakeConnectionFactory connections = new();
        XrplWallet wallet = XrplWallet.Generate();
        connections.Sink.LeaveTickets(wallet.ClassicAddress, 2, marked: true);
        ILedgerConnection connection = await connections.ConnectAsync("1", TestContext.Current.CancellationToken);

        await using ISubmissionStrategy strategy = PublishPolicy.Create(Estimate(chunks: 3, lists: 0), connection, new WalletSigner(wallet));

        Assert.IsType<SequenceStrategy>(strategy);
        Assert.Equal(2, (await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken)).Released);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.LeftoverTicketsTests"`
Expected: build FAILS — `TicketRelease`, `ReleaseLeftoverTicketsAsync` and the four-argument `SequenceStrategy` constructor do not exist.

- [ ] **Step 3: Add the result record and the interface member**

Create `src/XrplFile.Core/TicketRelease.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// What spending this library's leftover tickets came to (spec 4.6). Each ticket the tidy-up
/// found is in exactly one count.
/// </summary>
/// <param name="Released">Spent by the tidy-up's own transaction, validated with tesSUCCESS. Nothing short of validation counts.</param>
/// <param name="AlreadyGone">
/// Consumed by something else: the node answered tefNO_TICKET, or the ledger no longer listed the
/// ticket when the results were checked. Its reserve is free either way.
/// </param>
/// <param name="NotReleased">Any other outcome, or no outcome before the tidy-up's time ran out. Still live and still locking an owner reserve.</param>
public sealed record TicketRelease(int Released, int AlreadyGone, int NotReleased)
{
    /// <summary>Nothing found, or nothing tried.</summary>
    public static TicketRelease None { get; } = new(0, 0, 0);
}
```

In `src/XrplFile.Core/ISubmissionStrategy.cs`, in the interface summary, replace the words `/// until something spends them or the account is deleted.` with:

```csharp
/// until a later publication from the account picks them up or spends them in its tidy-up, or the
/// account is deleted.
```

Add after `SubmitAllAsync`:

```csharp
    /// <summary>
    /// Spends this library's live tickets on the account, each on an AccountSet that sets nothing,
    /// and waits for the results (spec I6). Called after a publication succeeds, from either
    /// strategy (spec I7): leftovers belong to the account, not to the run that left them.
    ///
    /// Cancellation ends it early and is not an error: what settled is counted and everything else
    /// is <see cref="TicketRelease.NotReleased"/>. Any other failure is thrown.
    /// </summary>
    Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken);
```

- [ ] **Step 4: Write the shared tidy-up**

In `src/XrplFile.Core/LeftoverTickets.cs`, add `using System.Collections.Concurrent;` and `using System.Globalization;` at the top, and add inside `LeftoverTickets`:

```csharp
    /// <summary>How many tidy-up spends are in the air at once, the same ceiling the ticket strategy's own batches use by default.</summary>
    private const int MaxInFlight = 25;

    /// <summary>
    /// Finds this library's tickets afresh from the validated ledger, spends each on an AccountSet
    /// that sets nothing, and waits for the outcomes (spec 4.6). Cancellation is the deadline: it
    /// ends the wait and leaves every unsettled ticket counted as not released.
    /// </summary>
    public static async Task<TicketRelease> ReleaseAsync(
        ILedgerSink sink, ILedgerSource source, ITransactionSigner signer, long feeDrops, TimeSpan poll, CancellationToken cancellationToken)
    {
        string account = signer.Account;
        TicketCensus census;

        try
        {
            census = await FindAsync(source, account, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TicketRelease.None;
        }

        if (census.Ours.Count == 0) return TicketRelease.None;

        ConcurrentDictionary<uint, string> waiting = new();
        ConcurrentDictionary<uint, bool> gone = new();
        HashSet<uint> released = [];

        try
        {
            await Parallel.ForEachAsync(
                census.Ours,
                new ParallelOptions { MaxDegreeOfParallelism = MaxInFlight, CancellationToken = cancellationToken },
                async (ticket, token) =>
                {
                    SubmitOutcome outcome = await sink
                        .SubmitAsync(Spend(account, signer.PublicKey, ticket, feeDrops), signer, token).ConfigureAwait(false);

                    if (string.Equals(outcome.EngineResult, "tefNO_TICKET", StringComparison.Ordinal)) gone[ticket] = true;
                    else if (Reached(outcome.EngineResult)) waiting[ticket] = outcome.TxHash;
                }).ConfigureAwait(false);

            // One listing a round rather than one lookup per ticket: a spend settles when its ticket
            // leaves the validated ledger, and only then is its own transaction asked what it did.
            // A spend that loses its ticket to a straggler and never validates still settles — as
            // gone — instead of holding the report until the deadline, and a public node sees one
            // account_objects a round, not two hundred and fifty tx requests.
            while (true)
            {
                IReadOnlyList<LiveTicket> still = await source.LiveTicketsAsync(account, cancellationToken).ConfigureAwait(false);
                HashSet<uint> listed = [.. still.Select(ticket => ticket.Sequence)];

                foreach ((uint ticket, string hash) in waiting.ToArray())
                {
                    if (listed.Contains(ticket)) continue;

                    LedgerTransaction? tx = await source.TransactionAsync(hash, cancellationToken).ConfigureAwait(false);
                    waiting.TryRemove(ticket, out _);

                    // Nothing short of its own validated tesSUCCESS is a release; a ticket that left
                    // the ledger any other way — a tec on the spend, a straggler first — is gone,
                    // and its reserve is free either way.
                    if (tx is { Validated: true, Succeeded: true }) released.Add(ticket);
                    else gone[ticket] = true;
                }

                if (waiting.IsEmpty)
                {
                    // A spend the node refused left its ticket alone — unless something else took
                    // it in the meantime, which the same listing shows.
                    foreach (uint ticket in census.Ours)
                    {
                        if (!released.Contains(ticket) && !listed.Contains(ticket)) gone[ticket] = true;
                    }

                    break;
                }

                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The deadline, or the caller's own cancellation: the counts stand as they are, and
            // everything not yet settled is not released (spec 4.6).
        }

        int alreadyGone = gone.Keys.Count(ticket => !released.Contains(ticket));

        return new TicketRelease(released.Count, alreadyGone, census.Ours.Count - released.Count - alreadyGone);
    }

    /// <summary>The answers that put a submission in front of a ledger: it may yet spend its ticket.</summary>
    private static bool Reached(string engineResult) =>
        engineResult.StartsWith("tes", StringComparison.Ordinal)
        || engineResult.StartsWith("tec", StringComparison.Ordinal)
        || string.Equals(engineResult, "terQUEUED", StringComparison.Ordinal);

    /// <summary>An AccountSet that sets nothing, riding one ticket: the cheapest transaction that frees its owner reserve.</summary>
    private static Dictionary<string, object> Spend(string account, string publicKey, uint ticket, long feeDrops) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = account,
        ["Sequence"] = 0u,
        ["TicketSequence"] = ticket,
        ["Fee"] = feeDrops.ToString(CultureInfo.InvariantCulture),
        ["SigningPubKey"] = publicKey,
    };
```

- [ ] **Step 5: Implement the member on both strategies**

In `src/XrplFile.Core/TicketStrategy.cs`, after `SubmitAllAsync`:

```csharp
    public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken)
    {
        // Whatever this instance still holds is about to be spent by the tidy-up, so nothing here
        // may ride it afterwards.
        lock (_ticketLock) _tickets.Clear();

        return LeftoverTickets.ReleaseAsync(_sink, _source, _signer, _options.FeeDrops, _options.ValidationPoll, cancellationToken);
    }
```

In `src/XrplFile.Core/SequenceStrategy.cs`, change the primary constructor to:

```csharp
public sealed class SequenceStrategy(
    ILedgerSink sink, ITransactionSigner signer, SequenceStrategyOptions? options = null, ILedgerSource? source = null) : ISubmissionStrategy
```

Add after `_options`:

```csharp
    /// <summary>
    /// What the tidy-up reads the account's tickets from (spec I7). Optional so that a caller with
    /// no source keeps compiling, but without it this strategy cannot find a leftover ticket and
    /// releases none — <c>PublishPolicy</c> in the application always passes one.
    /// </summary>
    private readonly ILedgerSource? _source = source;
```

Add after `SubmitAllAsync`:

```csharp
    public async Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken) =>
        _source is null
            ? TicketRelease.None
            : await LeftoverTickets.ReleaseAsync(_sink, _source, _signer, _options.FeeDrops, _options.ValidationPoll, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 6: Pass the source from the policy, and delegate in the test double**

In `app/XrplFile.App.Core/PublishPolicy.cs`, `Create`, replace the sequence branch with:

```csharp
            : new SequenceStrategy(connection.Sink, signer, new SequenceStrategyOptions { FeeDrops = fee }, connection.Source);
```

In `tests/XrplFile.Core.Tests/XrplFileWriterTests.cs`, inside `ProbingStrategy`, add:

```csharp
        public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken) =>
            inner.ReleaseLeftoverTicketsAsync(cancellationToken);
```

- [ ] **Step 7: Run the tests to verify they pass, then both suites**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.LeftoverTicketsTests"` and `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublishPolicyTests"`
Expected: PASS.

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` and `dotnet run --project tests/XrplFile.App.Core.Tests`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/XrplFile.Core/TicketRelease.cs src/XrplFile.Core/LeftoverTickets.cs src/XrplFile.Core/ISubmissionStrategy.cs src/XrplFile.Core/TicketStrategy.cs src/XrplFile.Core/SequenceStrategy.cs app/XrplFile.App.Core/PublishPolicy.cs tests/XrplFile.Core.Tests/XrplFileWriterTests.cs tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs tests/XrplFile.App.Core.Tests/PublishPolicyTests.cs
git commit -m "feat(core): both strategies can spend this library's leftover tickets"
```

---

### Task 6: The session tidies up after a successful publication

**Files:**
- Modify: `src/XrplFile.Core/PublicationSession.cs`
- Modify: `src/XrplFile.Core/PublicationReport.cs`
- Test: `tests/XrplFile.Core.Tests/PublicationSessionTests.cs`

**Interfaces:**
- Consumes: `ISubmissionStrategy.ReleaseLeftoverTicketsAsync`, `TicketRelease`, the four-argument `SequenceStrategy` constructor (Task 5); `FakeLedgerSink.LeaveTickets` (Task 2).
- Produces: `SessionOptions.TicketReleaseTimeout` (`TimeSpan`, default 2 minutes); `PublicationReport.TicketsReleased`, `TicketsAlreadyGone`, `TicketsNotReleased` (`int`, init-only) and `TicketReleaseFailure` (`Exception?`, init-only).

- [ ] **Step 1: Write the failing tests**

Append to `tests/XrplFile.Core.Tests/PublicationSessionTests.cs`, inside the class:

```csharp
    /// <summary>A strategy that publishes through <paramref name="inner"/> and tidies up however the test says.</summary>
    private sealed class ScriptedReleaseStrategy(ISubmissionStrategy inner, Func<CancellationToken, Task<TicketRelease>> release) : ISubmissionStrategy
    {
        public bool ReleaseCalled { get; private set; }

        public long FeeDropsPerTransaction => inner.FeeDropsPerTransaction;

        public string Account => inner.Account;

        public Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
            IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken) =>
            inner.SubmitAllAsync(memos, submitted, cancellationToken);

        public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken)
        {
            ReleaseCalled = true;
            return release(cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task A_publication_spends_the_leftovers_it_finds_and_says_so_on_the_report()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = NodeFeeDrops, ValidationPoll = TimeSpan.FromMilliseconds(1) }, ledger);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.TicketsReleased);
        Assert.Equal(0, report.TicketsAlreadyGone);
        Assert.Equal(0, report.TicketsNotReleased);
        Assert.Null(report.TicketReleaseFailure);
        Assert.Empty(sink.LiveTickets);
    }

    [Fact]
    public async Task A_tidy_up_that_throws_leaves_the_publication_successful_and_says_so()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using ScriptedReleaseStrategy strategy = new(
            new SequenceStrategy(sink, Signer, StrategyOptions),
            _ => throw new InvalidOperationException("the tidy-up broke"));

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Equal("the tidy-up broke", Assert.IsType<InvalidOperationException>(report.TicketReleaseFailure).Message);
        Assert.Empty(PublicationSession.Unfinished(_stateDirectory));
    }

    [Fact(Timeout = 10_000)]
    public async Task A_tidy_up_that_does_not_finish_in_time_does_not_hold_the_report_back()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using ScriptedReleaseStrategy strategy = new(
            new SequenceStrategy(sink, Signer, StrategyOptions),
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return TicketRelease.None;
            });

        SessionOptions options = new()
        {
            StateDirectory = _stateDirectory,
            TicketsPerBatch = 100,
            TicketReleaseTimeout = TimeSpan.FromMilliseconds(100),
        };

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", options, TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Null(report.TicketReleaseFailure);
    }

    [Fact]
    public async Task A_cancelled_publication_does_not_tidy_up()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> leftovers = sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);

        // A ticket strategy, because it checks the token before every batch: the writer and the
        // sequence strategy never look at an already-cancelled token against this fake.
        await using ScriptedReleaseStrategy strategy = new(
            new TicketStrategy(sink, ledger, Signer, new TicketStrategyOptions { FeeDrops = NodeFeeDrops }),
            _ => Task.FromResult(TicketRelease.None));

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(null, cancelled.Token));

        // The leftovers are exactly what a resume will pick up; spending them would charge fees
        // for a run the person stopped.
        Assert.False(strategy.ReleaseCalled);
        Assert.All(leftovers, ticket => Assert.Contains(ticket, sink.LiveTickets));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.PublicationSessionTests"`
Expected: build FAILS — `TicketsReleased`, `TicketReleaseFailure`, `TicketReleaseTimeout` do not exist. (`A_cancelled_publication_does_not_tidy_up` is a regression guard: it would pass on the old code if that compiled, since nothing tidies up yet.)

- [ ] **Step 3: Add the report properties**

In `src/XrplFile.Core/PublicationReport.cs`, next to `StateSaveFailure`, add:

```csharp
    /// <summary>Leftover tickets of this library's that the tidy-up after the publication spent (spec 4.6).</summary>
    public int TicketsReleased { get; init; }

    /// <summary>Leftover tickets the tidy-up found already consumed by something else. Their reserve is free.</summary>
    public int TicketsAlreadyGone { get; init; }

    /// <summary>Leftover tickets still live after the tidy-up — refused, or unsettled when its time ran out.</summary>
    public int TicketsNotReleased { get; init; }

    /// <summary>
    /// Why the tidy-up failed, or null. The publication succeeded regardless: the manifest is in the
    /// ledger and the link works (spec I8), the same way a state file that could not be saved does
    /// not cost the report.
    /// </summary>
    public Exception? TicketReleaseFailure { get; init; }
```

- [ ] **Step 4: Add the timeout and run the tidy-up**

In `src/XrplFile.Core/PublicationSession.cs`, `SessionOptions`, after `ReadBack`:

```csharp
    /// <summary>
    /// How long the tidy-up after a successful publication may take before what it has not settled
    /// is left as it is (spec I8). Linked to the publication's own cancellation.
    /// </summary>
    public TimeSpan TicketReleaseTimeout { get; init; } = TimeSpan.FromMinutes(2);
```

In `RunAsync`, after `Advance("done", result.Manifest.Root, result.ChunkHashes);` add:

```csharp
            // After "done" and never before: the manifest is in the ledger, so nothing the tidy-up
            // does — or fails to do — can cost the person their link (spec I8).
            (TicketRelease release, Exception? releaseFailure) = await ReleaseLeftoverTicketsAsync(cancellationToken).ConfigureAwait(false);
```

and extend the report's object initializer:

```csharp
            {
                StateSaveFailure = StateSaveFailure,
                TicketsReleased = release.Released,
                TicketsAlreadyGone = release.AlreadyGone,
                TicketsNotReleased = release.NotReleased,
                TicketReleaseFailure = releaseFailure,
            };
```

Add the private method after `RunAsync`:

```csharp
    /// <summary>
    /// Spends this library's leftover tickets under its own deadline, linked to the publication's
    /// token. A cancellation or the deadline ends it with the counts it reached; an exception is
    /// caught and returned. Neither fails the publication (spec I8).
    /// </summary>
    private async Task<(TicketRelease Release, Exception? Failure)> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.TicketReleaseTimeout);

        try
        {
            // WaitAsync is what makes the bound the session's own (spec I8): a strategy, or a sink
            // under it, that does not observe the token still cannot hold the report back.
            return (await _strategy.ReleaseLeftoverTicketsAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return (TicketRelease.None, null);
        }
        catch (Exception failure)
        {
            return (TicketRelease.None, failure);
        }
    }
```

- [ ] **Step 5: Run the tests to verify they pass, then both suites**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.PublicationSessionTests"`
Expected: PASS.

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` and `dotnet run --project tests/XrplFile.App.Core.Tests`
Expected: PASS.

- [ ] **Step 6: Say it in the README**

In `README.md`, after the paragraph that begins "`TicketStrategy` mints tickets and rides them" (it ends "…cannot carry a batch of tickets."), add:

```markdown
Every ticket this library mints carries a mark, so an interrupted publication's leftovers are not
lost: the next ticketed publication from the same account spends them before it mints, and any
successful publication — on either strategy — spends whatever is still left on transactions that
do nothing, within `SessionOptions.TicketReleaseTimeout` (two minutes by default). The report says
how many were released (`TicketsReleased`), had already gone (`TicketsAlreadyGone`) or are still
live (`TicketsNotReleased`), and why the tidy-up failed if it did (`TicketReleaseFailure`); the
publication itself never fails because of it. Tickets without the mark — minted by anything else,
or by a version of this library from before the mark — are never touched. A `SequenceStrategy`
tidies up only when it is given the ledger source: `new SequenceStrategy(sink, signer, options, source)`.
```

- [ ] **Step 7: Commit**

```bash
git add src/XrplFile.Core/PublicationSession.cs src/XrplFile.Core/PublicationReport.cs tests/XrplFile.Core.Tests/PublicationSessionTests.cs README.md
git commit -m "feat(core): a successful publication spends this library's leftover tickets"
```

---

### Task 7: The estimate agrees with all of it

**Files:**
- Modify: `src/XrplFile.Core/PublicationEstimate.cs`
- Modify: `src/XrplFile.Core/PublicationSession.cs` (`StartAsync`, `ResumeAsync`)
- Modify: `app/XrplFile.App.Core/PublishPolicy.cs`, `app/XrplFile.App.Core/EstimateModel.cs`
- Test: `tests/XrplFile.Core.Tests/PublicationEstimateTests.cs`, `tests/XrplFile.Core.Tests/PublicationSessionTests.cs`, `tests/XrplFile.App.Core.Tests/EstimateModelTests.cs`

**Interfaces:**
- Consumes: `LeftoverTickets.FindAsync` (Task 2); `FakeLedgerSink.LeaveTickets` / `LeakTickets`.
- Produces: `PublicationEstimate.ReusableTickets` (`int`, init-only, default 0) and `PublicationEstimate.TicketSlots` (`int`, init-only, default `TicketStrategy.MaxLiveTickets`); `public static void PublicationEstimator.RequireTicketSlot(PublicationEstimate estimate)` throwing `XrplFileError.SubmitRefused`; `public static bool PublishPolicy.HasNoTicketRoom(PublicationEstimate estimate)`.

- [ ] **Step 1: Update the pinned values and write the failing tests**

In `tests/XrplFile.Core.Tests/PublicationEstimateTests.cs`:

Replace lines 65–68 (the three comment lines and `Assert.Equal(1_800_180, estimate.RequiredDrops);`; line 64, the base-reserve assertion, stays) with:

```csharp
        // A computed literal, not the sum of the instance's own fields: 6 transactions * 3
        // default max attempts * 10 drops, plus a tidy-up of up to 250 transactions * 10 drops,
        // is 2,680 worst-case fee; plus the 800,000-drop peak reserve and the 1,000,000-drop base
        // reserve.
        Assert.Equal(1_802_680, estimate.RequiredDrops);
```

Replace lines 105–107 with:

```csharp
        // 6 transactions * 3 max attempts * 15 drops, plus the tidy-up's 250 transactions * 15 drops.
        Assert.Equal(4_020, estimate.FeeDropsWorstCase);
        Assert.Equal(
            (estimate.Transactions * estimate.MaxAttempts + TicketStrategy.MaxLiveTickets) * estimate.FeeDropsPerTransaction,
            estimate.FeeDropsWorstCase);
```

Replace lines 119–120 with:

```csharp
        // 6 transactions * 5 max attempts * 10 drops, plus the tidy-up's 250 transactions * 10 drops.
        Assert.Equal(2_800, estimate.FeeDropsWorstCase);
```

Replace `(1,800,180 at these defaults)` and `(1,800,180 here)` in the two later comments with `(1,802,680 at these defaults)` and `(1,802,680 here)`.

Append, inside the class:

```csharp
    [Fact]
    public async Task This_librarys_leftovers_stand_in_for_tickets_the_run_would_mint()
    {
        FakeLedgerSource node = Node(100_000_000);
        FakeLedgerSink sink = new(node);
        sink.LeaveTickets("rWriter", 3, marked: true);

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        // Four memos, three of them covered by tickets already on the account: one to mint.
        Assert.Equal(3, estimate.ReusableTickets);
        Assert.Equal(TicketStrategy.MaxLiveTickets, estimate.TicketSlots);
        Assert.Equal(1 * 200_000, estimate.PeakReserveDrops);
        // The count of mints stays the worst case: every pass is assumed to mint (spec 5).
        Assert.Equal(2, estimate.Mints);
    }

    [Fact]
    public async Task Somebody_elses_tickets_take_slots_and_replace_nothing()
    {
        FakeLedgerSource node = Node(100_000_000);
        FakeLedgerSink sink = new(node);
        sink.LeakTickets("rWriter", 3);

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(0, estimate.ReusableTickets);
        Assert.Equal(TicketStrategy.MaxLiveTickets - 3, estimate.TicketSlots);
        Assert.Equal(4 * 200_000, estimate.PeakReserveDrops);
    }

    [Fact]
    public async Task The_peak_is_capped_by_the_slots_somebody_elses_tickets_leave()
    {
        FakeLedgerSource node = Node(500_000_000);
        FakeLedgerSink sink = new(node);
        sink.LeakTickets("rWriter", TicketStrategy.MaxLiveTickets - 2);

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes: 1_024_001, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(2, estimate.TicketSlots);
        Assert.Equal(2 * 200_000, estimate.PeakReserveDrops);
    }

    [Fact]
    public async Task No_free_slot_refuses_a_ticketed_run_with_that_reason()
    {
        FakeLedgerSource node = Node(500_000_000);
        FakeLedgerSink sink = new(node);
        sink.LeakTickets("rWriter", TicketStrategy.MaxLiveTickets);

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes: 1_024_001, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(0, estimate.TicketSlots);
        Assert.Equal(0, estimate.PeakReserveDrops);

        XrplFileException error = Assert.Throws<XrplFileException>(() => PublicationEstimator.RequireTicketSlot(estimate));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Contains("250", error.Message, StringComparison.Ordinal);
    }
```

Append to `tests/XrplFile.Core.Tests/PublicationSessionTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task A_ticketed_run_with_no_free_slot_never_starts()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        sink.LeakTickets(Wallet.ClassicAddress, TicketStrategy.MaxLiveTickets);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { FeeDrops = NodeFeeDrops });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 100_000), "application/octet-stream", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Empty(sink.Submitted);
    }
```

Append to `tests/XrplFile.App.Core.Tests/EstimateModelTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task A_ticketed_file_on_an_account_whose_ticket_slots_are_all_taken_cannot_be_published()
    {
        _connections.Ledger.Balances["rWriter"] = 1_000_000_000;
        _connections.Sink.LeakTickets("rWriter", TicketStrategy.MaxLiveTickets);

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(size: 100_000), TestContext.Current.CancellationToken);

        Assert.NotNull(model.Estimate);
        Assert.False(model.CanPublish);
        Assert.Contains("250", model.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_small_file_is_still_publishable_when_every_ticket_slot_is_taken()
    {
        _connections.Ledger.Balances["rWriter"] = 1_000_000_000;
        _connections.Sink.LeakTickets("rWriter", TicketStrategy.MaxLiveTickets);

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(size: 4096), TestContext.Current.CancellationToken);

        Assert.True(model.CanPublish);
        Assert.Null(model.Message);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.PublicationEstimateTests" -class "XrplFile.Tests.PublicationSessionTests"`
Expected: build FAILS — `ReusableTickets`, `TicketSlots`, `RequireTicketSlot` do not exist.

- [ ] **Step 3: Change the estimate**

In `src/XrplFile.Core/PublicationEstimate.cs`:

Update the `PeakReserveDrops` param doc to: "What live tickets the run mints lock up at the worst moment: the memos, capped at the live-ticket slots other tickets leave free (<see cref="TicketSlots"/>), less this library's leftovers already on the account (<see cref="ReusableTickets"/>), which the run spends instead of minting and whose reserve <see cref="OwnerCount"/> already carries."

Update the `FeeDropsWorstCase` param doc to: "The fee if every memo needed every one of <see cref="MaxAttempts"/> tries, plus a tidy-up of up to <see cref="TicketStrategy.MaxLiveTickets"/> transactions (spec I10): (<see cref="Transactions"/> × <see cref="MaxAttempts"/> + <see cref="TicketStrategy.MaxLiveTickets"/>) × <see cref="FeeDropsPerTransaction"/>. The tidy-up is not in <see cref="Transactions"/>: any publication may tidy up, so its cost is covered here rather than shown."

Add inside the record body, before `LockedReserveDrops`:

```csharp
    /// <summary>
    /// This library's live tickets already on the account, left by an earlier run: a ticketed run
    /// spends them before it mints (spec I9).
    /// </summary>
    public int ReusableTickets { get; init; }

    /// <summary>
    /// How many of the protocol's <see cref="TicketStrategy.MaxLiveTickets"/> live-ticket slots the
    /// account's other tickets — not this library's — leave free. Zero refuses a ticketed run
    /// (<see cref="PublicationEstimator.RequireTicketSlot"/>).
    /// </summary>
    public int TicketSlots { get; init; } = TicketStrategy.MaxLiveTickets;
```

In `EstimateAsync`, after the `AccountState state = …` line add:

```csharp
        // Every live ticket takes one of the protocol's slots, but only this library's can stand in
        // for one the run would otherwise mint (spec I9). Counting anybody else's as reusable would
        // approve a run whose mints the account cannot afford.
        TicketCensus census = await LeftoverTickets.FindAsync(source, account, null, cancellationToken).ConfigureAwait(false);
        int slots = Math.Max(0, TicketStrategy.MaxLiveTickets - census.Others);
        long toMint = Math.Max(0, Math.Min(memos, slots) - census.Ours.Count);
```

Replace

```csharp
        long feeWorstCase = transactions * maxAttempts * fees.OpenLedgerFeeDrops;
```

with

```csharp
        // The tidy-up is at most one transaction per live ticket the protocol allows (spec I10).
        long feeWorstCase = (transactions * maxAttempts + TicketStrategy.MaxLiveTickets) * fees.OpenLedgerFeeDrops;
```

Replace the `peakReserve` line and the comment above it with:

```csharp
        // A ticket dispatched whose submission never reaches a ledger stays live for the strategy's
        // lifetime, so the honest worst case is every slot the account has free, not the batch size.
        // This library's leftovers fill some of those slots already and are not minted again.
        long peakReserve = toMint * fees.OwnerReserveDrops;
```

Make the `return` an object-initialised construction:

```csharp
        return new PublicationEstimate(
            chunks, blocks, lists, mints, transactions,
            fees.OpenLedgerFeeDrops, feeTotal, maxAttempts, feeWorstCase, peakReserve,
            fees.BaseReserveDrops, fees.OwnerReserveDrops, required, state.BalanceDrops, state.OwnerCount)
        {
            ReusableTickets = census.Ours.Count,
            TicketSlots = slots,
        };
```

Add to `PublicationEstimator`, after `Require`:

```csharp
    /// <summary>
    /// Refuses a ticketed run on an account whose every live-ticket slot is taken by tickets that
    /// are not this library's: there is nowhere to mint and nothing it may reuse, and the run would
    /// stop at its first batch with fees already spent on nothing (spec 5).
    /// </summary>
    public static void RequireTicketSlot(PublicationEstimate estimate)
    {
        if (estimate.TicketSlots > 0) return;

        throw new XrplFileException(XrplFileError.SubmitRefused,
            $"the account already holds the {TicketStrategy.MaxLiveTickets} live tickets the protocol allows and none of "
            + "them was minted by this library, so a ticketed run has nowhere to mint and nothing it may reuse");
    }
```

- [ ] **Step 4: Refuse in the session**

In `src/XrplFile.Core/PublicationSession.cs`, in both `StartAsync` and `ResumeAsync`, directly after `PublicationEstimator.Require(estimate, options.BudgetDrops);` add:

```csharp
        if (strategy is TicketStrategy) PublicationEstimator.RequireTicketSlot(estimate);
```

- [ ] **Step 5: Refuse in the application**

In `app/XrplFile.App.Core/PublishPolicy.cs`, after `UsesTickets(PublicationEstimate)`:

```csharp
    /// <summary>
    /// Whether the run would use tickets on an account where none can be minted or reused: every
    /// live-ticket slot is taken by tickets this application did not leave. A run that would not
    /// use tickets is unaffected.
    /// </summary>
    public static bool HasNoTicketRoom(PublicationEstimate estimate) => UsesTickets(estimate) && estimate.TicketSlots == 0;
```

In `app/XrplFile.App.Core/EstimateModel.cs`, extend `CanPublish`:

```csharp
    public bool CanPublish =>
        Estimate is not null
        && Estimate.BalanceCovers
        && Estimate.FeeDropsPerTransaction <= int.MaxValue
        && !PublishPolicy.HasNoTicketRoom(Estimate);
```

and between the fee check and the `!Estimate.BalanceCovers` check in `EstimateAsync` add:

```csharp
        if (PublishPolicy.HasNoTicketRoom(Estimate))
        {
            Message =
                $"This account already holds the {TicketStrategy.MaxLiveTickets} tickets the XRP Ledger allows, none of them "
                + "left by this application, so a file this size has nowhere to put its own. Publish a smaller file, "
                + "which needs no tickets, or use another account.";
            return;
        }
```

- [ ] **Step 6: Run the tests to verify they pass, then both suites**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.PublicationEstimateTests" -class "XrplFile.Tests.PublicationSessionTests"` and `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.EstimateModelTests"`
Expected: PASS.

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` and `dotnet run --project tests/XrplFile.App.Core.Tests`
Expected: PASS. A test elsewhere that pins a figure derived from `FeeDropsWorstCase` or `RequiredDrops` (rather than reading it off the estimate) must be updated by recomputing it with the tidy-up term, and the recomputation written in its comment, as above.

- [ ] **Step 7: Commit**

```bash
git add src/XrplFile.Core/PublicationEstimate.cs src/XrplFile.Core/PublicationSession.cs app/XrplFile.App.Core/PublishPolicy.cs app/XrplFile.App.Core/EstimateModel.cs tests/XrplFile.Core.Tests/PublicationEstimateTests.cs tests/XrplFile.Core.Tests/PublicationSessionTests.cs tests/XrplFile.App.Core.Tests/EstimateModelTests.cs
git commit -m "feat(core): the estimate counts reusable leftovers, occupied slots and the tidy-up"
```

---

### Task 8: The close confirmation says what an interruption leaves

**Files:**
- Modify: `app/XrplFile.App.Core/PublicationRun.cs`
- Modify: `app/XrplFile.App.Core/RunModel.cs`
- Modify: `app/XrplFile.App/App.xaml.cs`
- Test: `tests/XrplFile.App.Core.Tests/RunModelTests.cs`, `tests/XrplFile.App.Core.Tests/PublicationRunTests.cs`

**Interfaces:**
- Consumes: `PublishPolicy.UsesTickets(PublicationEstimate)`, `PublicationEstimate.OwnerReserveDrops`.
- Produces: `RunSnapshot.UsesTickets` (`bool`, init-only) and `RunSnapshot.OwnerReserveDrops` (`long`, init-only), set once the run's estimate is taken; `public static string RunModel.CloseConfirmation(RunSnapshot snapshot)`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/XrplFile.App.Core.Tests/RunModelTests.cs`, inside the class:

```csharp
    [Fact]
    public void Closing_over_a_ticketed_run_says_the_tickets_stay_and_what_each_one_holds()
    {
        RunSnapshot snapshot = new(PublicationRunState.Running, null, null, null, "big.bin", "1", null)
        {
            UsesTickets = true,
            OwnerReserveDrops = 2_000_000,
        };

        Assert.Equal(
            "\"big.bin\" is still publishing. Closing now stops the run; it can be resumed later. The tickets it "
            + "reserved stay on this account, each holding 2,000,000 drops of owner reserve, until a later "
            + "publication from this account spends them or the account is closed. Close anyway?",
            RunModel.CloseConfirmation(snapshot));
    }

    [Fact]
    public void Closing_over_a_run_without_tickets_reads_as_it_always_did()
    {
        RunSnapshot snapshot = new(PublicationRunState.Running, null, null, null, "small.txt", "1", null);

        Assert.Equal(
            "\"small.txt\" is still publishing. Closing now will stop the run; it can be resumed later. Close anyway?",
            RunModel.CloseConfirmation(snapshot));
    }

    [Fact]
    public void A_run_with_no_name_yet_is_called_the_file()
    {
        RunSnapshot snapshot = new(PublicationRunState.Running, null, null, null, null, null, null);

        Assert.StartsWith("\"the file\" is still publishing.", RunModel.CloseConfirmation(snapshot), StringComparison.Ordinal);
    }
```

Append to `tests/XrplFile.App.Core.Tests/PublicationRunTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task A_running_ticketed_publication_says_so_on_its_snapshot()
    {
        GatedSubmitSink gate = new(_connections.Sink, gateAt: 1);
        GatedConnectionFactory connections = new(_connections, gate);
        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, size: 100_000, connections: connections);

        run.Start(request);
        await WaitForGateAsync(gate, run);

        RunSnapshot snapshot = run.Snapshot;
        run.Cancel();
        gate.Release();
        await run.Completion;

        Assert.True(snapshot.UsesTickets);
        Assert.Equal(200_000, snapshot.OwnerReserveDrops);
    }

    [Fact]
    public async Task A_running_publication_without_tickets_says_so_on_its_snapshot()
    {
        GatedSubmitSink gate = new(_connections.Sink, gateAt: 1);
        GatedConnectionFactory connections = new(_connections, gate);
        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, connections: connections);

        run.Start(request);
        await WaitForGateAsync(gate, run);

        RunSnapshot snapshot = run.Snapshot;
        run.Cancel();
        gate.Release();
        await run.Completion;

        Assert.False(snapshot.UsesTickets);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.RunModelTests" -class "XrplFile.App.Core.Tests.PublicationRunTests"`
Expected: build FAILS — `UsesTickets`, `OwnerReserveDrops`, `CloseConfirmation` do not exist. (`A_running_publication_without_tickets_says_so_on_its_snapshot` is a regression guard: `UsesTickets` defaults to false, so it passes as soon as it compiles.)

- [ ] **Step 3: Carry the two facts on the snapshot**

In `app/XrplFile.App.Core/PublicationRun.cs`, give `RunSnapshot` a body:

```csharp
public sealed record RunSnapshot(
    PublicationRunState State,
    PublishProgress? Progress,
    PublicationReport? Report,
    string? Failure,
    string? Name,
    string? Network,
    string? HistoryNote)
{
    /// <summary>
    /// Whether the run publishes on tickets, as the estimate it was approved against decided.
    /// False until that estimate is taken — and until then the run has minted nothing.
    /// </summary>
    public bool UsesTickets { get; init; }

    /// <summary>What one ticket locks, in drops, from the same estimate. Zero until it is taken.</summary>
    public long OwnerReserveDrops { get; init; }
}
```

Add fields after `_historyNote`:

```csharp
    private bool _usesTickets;
    private long _ownerReserveDrops;
```

In `Snapshot`, return:

```csharp
                return new RunSnapshot(_state, _progress, _report, _failure, _name, _network, _historyNote)
                {
                    UsesTickets = _usesTickets,
                    OwnerReserveDrops = _ownerReserveDrops,
                };
```

In `Reset` and in `Begin`, next to `_historyNote = null;`, add:

```csharp
            _usesTickets = false;
            _ownerReserveDrops = 0;
```

In `RunAsync`, directly after the `if (estimate.FeeDropsPerTransaction > int.MaxValue) { … }` block, add:

```csharp
                // Pinned to this run's generation, like every other write after Begin: a newer run's
                // snapshot must not inherit this one's answer.
                lock (_gate)
                {
                    if (_generation == generation)
                    {
                        _usesTickets = PublishPolicy.UsesTickets(estimate);
                        _ownerReserveDrops = estimate.OwnerReserveDrops;
                    }
                }
```

- [ ] **Step 4: Decide the sentence in the model**

In `app/XrplFile.App.Core/RunModel.cs`, add `using System.Globalization;` at the top and add to `RunModel`:

```csharp
    /// <summary>
    /// What the window asks before closing over a running publication (spec 6.1, I11). A ticketed
    /// run names the only two ways its tickets ever go, and promises neither: a later publication
    /// tries, and closing the account always removes them. A run without tickets reads as it always did.
    /// </summary>
    public static string CloseConfirmation(RunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string name = snapshot.Name ?? "the file";

        return snapshot.UsesTickets
            ? string.Create(CultureInfo.InvariantCulture,
                $"\"{name}\" is still publishing. Closing now stops the run; it can be resumed later. The tickets it "
                + $"reserved stay on this account, each holding {snapshot.OwnerReserveDrops:N0} drops of owner reserve, "
                + $"until a later publication from this account spends them or the account is closed. Close anyway?")
            : $"\"{name}\" is still publishing. Closing now will stop the run; it can be resumed later. Close anyway?";
    }
```

- [ ] **Step 5: Use it in the window's close handler**

In `app/XrplFile.App/App.xaml.cs`, replace

```csharp
						string name = snapshot.Name ?? "the file";
						bool confirmed = window.Page is null || await window.Page.DisplayAlertAsync(
							"Publication in progress",
							$"\"{name}\" is still publishing. Closing now will stop the run; it can be resumed later. Close anyway?",
							"Close",
							"Cancel");
```

with

```csharp
						bool confirmed = window.Page is null || await window.Page.DisplayAlertAsync(
							"Publication in progress",
							RunModel.CloseConfirmation(snapshot),
							"Close",
							"Cancel");
```

(The file is tab-indented; keep tabs.)

- [ ] **Step 6: Run the tests to verify they pass, then build the app**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.RunModelTests" -class "XrplFile.App.Core.Tests.PublicationRunTests"`
Expected: PASS.

Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj -c Debug`
Expected: Build succeeded, no new warnings.

- [ ] **Step 7: Commit**

```bash
git add app/XrplFile.App.Core/PublicationRun.cs app/XrplFile.App.Core/RunModel.cs app/XrplFile.App/App.xaml.cs tests/XrplFile.App.Core.Tests/RunModelTests.cs tests/XrplFile.App.Core.Tests/PublicationRunTests.cs
git commit -m "feat(app): the close confirmation says what an interrupted ticketed run leaves"
```

---

### Task 9: The unfinished-runs list says what deleting leaves

**Files:**
- Modify: `app/XrplFile.App.Core/PublishPolicy.cs`
- Modify: `app/XrplFile.App.Core/UnfinishedModel.cs`
- Modify: `app/XrplFile.App/Components/Unfinished.razor`
- Test: `tests/XrplFile.App.Core.Tests/PublishPolicyTests.cs`, `tests/XrplFile.App.Core.Tests/UnfinishedModelTests.cs`

**Interfaces:**
- Consumes: `PublishPolicy.UsesTickets(PublicationEstimate)`, `PublishPolicy.TicketThreshold`; `UnfinishedRow`.
- Produces: `public static bool PublishPolicy.UsesTickets(long fileBytes)`; `public const string UnfinishedModel.TicketNote`; `public static bool UnfinishedModel.ShowsTicketNoteFor(IEnumerable<UnfinishedRow> rows)`; `public bool UnfinishedModel.ShowsTicketNote`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/XrplFile.App.Core.Tests/PublishPolicyTests.cs`, inside the class:

```csharp
    [Theory]
    [InlineData(1L)]
    [InlineData(98_000L)]
    [InlineData(98_001L)]
    [InlineData(99_000L)]
    [InlineData(1_024_001L)]
    [InlineData(3_145_728L)]
    public async Task The_size_alone_decides_tickets_exactly_as_the_estimate_does(long fileBytes)
    {
        FakeLedgerSource node = new();
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes, PublishPolicy.TicketsPerBatch, TestContext.Current.CancellationToken);

        Assert.Equal(PublishPolicy.UsesTickets(estimate), PublishPolicy.UsesTickets(fileBytes));
    }

    [Fact]
    public void The_threshold_falls_between_98000_and_98001_bytes()
    {
        Assert.False(PublishPolicy.UsesTickets(98_000L));
        Assert.True(PublishPolicy.UsesTickets(98_001L));
    }
```

Append to `tests/XrplFile.App.Core.Tests/UnfinishedModelTests.cs`, inside the class:

```csharp
    private static UnfinishedRow Row(long size, string? unreadable = null) =>
        new("state.json", "1", "rWriter", "ref", "application/octet-stream", "file.bin", size, "chunks", 0, unreadable);

    [Fact]
    public void A_ticketed_run_in_the_list_brings_the_ticket_note()
    {
        Assert.True(UnfinishedModel.ShowsTicketNoteFor([Row(4096), Row(3_145_728)]));
    }

    [Fact]
    public void A_list_without_a_ticketed_run_has_no_ticket_note()
    {
        Assert.False(UnfinishedModel.ShowsTicketNoteFor([Row(4096), Row(0, unreadable: "newer format")]));
        Assert.False(UnfinishedModel.ShowsTicketNoteFor([]));
    }

    [Fact]
    public void The_ticket_note_reads_as_the_design_says()
    {
        Assert.Equal(
            "Deleting a run leaves the tickets it reserved on this account, each holding an owner reserve, until a "
            + "later publication from this account spends them or the account is closed.",
            UnfinishedModel.TicketNote);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublishPolicyTests" -class "XrplFile.App.Core.Tests.UnfinishedModelTests"`
Expected: build FAILS — `UsesTickets(long)`, `ShowsTicketNoteFor`, `TicketNote` do not exist. (`A_list_without_a_ticketed_run_has_no_ticket_note` is a regression guard against a note shown too eagerly.)

- [ ] **Step 3: Decide tickets from the size alone**

In `app/XrplFile.App.Core/PublishPolicy.cs`, after `UsesTickets(PublicationEstimate)`:

```csharp
    /// <summary>
    /// The same decision from a file's size alone, for a run whose estimate is gone — an unfinished
    /// one in the list. Exact, not approximate: a block list exists only past 1,024 chunks, far
    /// above the threshold, so below it the chunks and the manifest are every memo there is, and
    /// above it both forms are already true.
    /// </summary>
    public static bool UsesTickets(long fileBytes) =>
        FileTree.ChunkCount(fileBytes, MemoLayout.ChunkBytes) + 1 >= TicketThreshold;
```

- [ ] **Step 4: Decide the note in the model**

In `app/XrplFile.App.Core/UnfinishedModel.cs`, add to `UnfinishedModel`:

```csharp
    /// <summary>What the list adds when a run in it uses tickets (spec 6.2).</summary>
    public const string TicketNote =
        "Deleting a run leaves the tickets it reserved on this account, each holding an owner reserve, until a "
        + "later publication from this account spends them or the account is closed.";

    /// <summary>Whether <see cref="TicketNote"/> belongs under the current rows.</summary>
    public bool ShowsTicketNote => ShowsTicketNoteFor(Rows);

    /// <summary>
    /// True when any readable row is a ticketed run, decided by the threshold <see cref="PublishPolicy"/>
    /// applies rather than a copy of it. An unreadable row knows no size and says nothing either way.
    /// </summary>
    public static bool ShowsTicketNoteFor(IEnumerable<UnfinishedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return rows.Any(row => row.Unreadable is null && PublishPolicy.UsesTickets(row.Size));
    }
```

- [ ] **Step 5: Render it**

In `app/XrplFile.App/Components/Unfinished.razor`, directly after the existing `<p class="text-body-secondary">Deleting does not undo …</p>` paragraph, still inside the same `@if (Model.Rows.Count > 0)` block:

```razor
        @if (Model.ShowsTicketNote)
        {
            <p class="text-body-secondary">@UnfinishedModel.TicketNote</p>
        }
```

- [ ] **Step 6: Run the tests to verify they pass, then build the app**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublishPolicyTests" -class "XrplFile.App.Core.Tests.UnfinishedModelTests"`
Expected: PASS.

Run: `dotnet run --project tests/XrplFile.App.Core.Tests` and `dotnet build app/XrplFile.App/XrplFile.App.csproj -c Debug`
Expected: PASS; Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add app/XrplFile.App.Core/PublishPolicy.cs app/XrplFile.App.Core/UnfinishedModel.cs app/XrplFile.App/Components/Unfinished.razor tests/XrplFile.App.Core.Tests/PublishPolicyTests.cs tests/XrplFile.App.Core.Tests/UnfinishedModelTests.cs
git commit -m "feat(app): the unfinished-runs list says what deleting a ticketed run leaves"
```

---

### Task 10: Proving it on the standalone node

**Files:**
- Create: `tests/XrplFile.Core.Tests/LeftoverTicketsStandaloneTests.cs`

**Interfaces:**
- Consumes: everything above; `StandaloneFixture.FundedAccountAsync`, `MintTicketsAsync`, `CurrentLedgerAsync`, `CloseLedgersUntilAsync`; `VectorStream.Bytes`; `XrplFileReader`.
- Produces: nothing new.

- [ ] **Step 1: Make sure the stand is up**

Run: `docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml ps`
Expected: the `xrpl-file-ci` services listed as running. If not: `docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml up -d`. Never start, stop or touch any other compose project.

The tests default to `ws://localhost:6006`. Another stand on this machine may hold that port; if the `xrpl-file-ci` stand was raised with `.ci-config/docker-compose.local-ports.yml` or any other port override, set `XRPL_FILE_NODE_URL` to its admin WebSocket before every test run below, or the tests run against somebody else's node.

- [ ] **Step 2: Write the tests**

Create `tests/XrplFile.Core.Tests/LeftoverTicketsStandaloneTests.cs`:

```csharp
using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// What spec section 1 did by hand, against a node: a ticketed publication stopped part-way leaves
/// tickets on its account, and what comes after spends every one of them while leaving the
/// account's own ticket alone. The account's own ticket is minted before the stopped run rather
/// than after it, as spec 7 orders it; the two are equivalent, and minting first keeps
/// <see cref="StandaloneFixture.MintTicketsAsync"/>'s owner-count check true.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class LeftoverTicketsStandaloneTests : IDisposable
{
    /// <summary>A hundred-and-twenty-chunk file is one batch of a hundred tickets and then twenty.</summary>
    private const int FileBytes = 120_000;

    /// <summary>How many memos of the first batch reach the node before the run is stopped. The other sixty tickets stay live.</summary>
    private const int ForwardedBeforeStop = 40;

    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "xrpl-file-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    /// <summary>
    /// Forwards the first <paramref name="forwarded"/> memo submissions, then stops the run: every
    /// later one is cancelled before it reaches the node, so its ticket is certainly still live.
    /// Mints pass through untouched.
    /// </summary>
    private sealed class StopAfterSink(ILedgerSink inner, int forwarded, CancellationTokenSource stop) : ILedgerSink
    {
        private int _memos;

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            if (transaction.ContainsKey("TicketSequence") && Interlocked.Increment(ref _memos) > forwarded)
            {
                stop.Cancel();
                throw new OperationCanceledException(stop.Token);
            }

            return inner.SubmitAsync(transaction, signer, cancellationToken);
        }
    }

    /// <summary>Counts the tickets the mints passing through it ask for.</summary>
    private sealed class MintCountingSink(ILedgerSink inner) : ILedgerSink
    {
        private int _minted;

        public int Minted => Volatile.Read(ref _minted);

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            if ((string)transaction["TransactionType"] == "TicketCreate")
            {
                Interlocked.Add(ref _minted, (int)Convert.ToUInt32(transaction["TicketCount"]));
            }

            return inner.SubmitAsync(transaction, signer, cancellationToken);
        }
    }

    /// <summary>
    /// A funded account holding one ticket of its own, minted without the library's mark, and the
    /// leftovers of a ticketed publication stopped part-way through its first batch.
    /// </summary>
    private static async Task<(XrplWallet Writer, uint Foreign, string StatePath, int Fee)> StrandedAsync(
        IXrplClient client, XrplClientLedgerSink sink, XrplClientLedgerSource source, byte[] file, SessionOptions options, CancellationToken token)
    {
        XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 700_000_000, token);
        ITransactionSigner signer = new WalletSigner(writer);

        PublicationEstimate priced = await PublicationSession.EstimateAsync(source, writer.ClassicAddress, file.Length, options, token);
        int fee = (int)priced.FeeDropsPerTransaction;

        // First, while the account owns nothing, so the fixture's owner-count check holds.
        await StandaloneFixture.MintTicketsAsync(sink, source, writer, 1, fee, token);
        uint foreign = Assert.Single(await source.LiveTicketsAsync(writer.ClassicAddress, token)).Sequence;

        string statePath;

        using (CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            await using TicketStrategy first = new(
                new StopAfterSink(sink, ForwardedBeforeStop, stop), source, signer,
                new TicketStrategyOptions { TicketsPerBatch = 100, FeeDrops = fee });

            PublicationSession session = await PublicationSession.StartAsync(
                first, source, file, "application/octet-stream", "stopped.bin",
                writer.ClassicAddress, "key-standalone", "1", options, token);

            statePath = session.StatePath;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(null, stop.Token));
        }

        // Let the forty forwarded memos validate, so what is listed next is what is really left.
        await StandaloneFixture.CloseLedgersUntilAsync(client, await StandaloneFixture.CurrentLedgerAsync(client, token) + 2, token);

        return (writer, foreign, statePath, fee);
    }

    [Fact]
    public async Task A_resumed_run_spends_what_the_stopped_one_left_and_leaves_a_foreign_ticket_alone()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            SessionOptions options = new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };
            byte[] file = VectorStream.Bytes("leftovers-resume", FileBytes);

            (XrplWallet writer, uint foreign, string statePath, int fee) = await StrandedAsync(client, sink, source, file, options, token);

            // At least sixty: a counted submission that was cancelled before it reached the socket
            // leaves its ticket live too. What is invariant is that every ticket of the first batch
            // either carried a chunk that landed or is still here.
            TicketCensus stranded = await LeftoverTickets.FindAsync(source, writer.ClassicAddress, null, token);
            Assert.InRange(stranded.Ours.Count, 100 - ForwardedBeforeStop, 100);
            Assert.Equal(1, stranded.Others);

            MintCountingSink counting = new(sink);
            await using TicketStrategy second = new(
                counting, source, new WalletSigner(writer), new TicketStrategyOptions { TicketsPerBatch = 100, FeeDrops = fee });

            PublicationSession resumed = await PublicationSession.ResumeAsync(second, source, file, statePath, options, token);
            PublicationReport report = await resumed.RunAsync(null, token);

            ReadResult read = await new XrplFileReader(source).ReadAsync(report.Link, token);
            Assert.Equal(file, read.Bytes);

            // The resume needs a ticket for every chunk that did not land, and one for the manifest.
            // Every ticket of the first batch either carried a chunk that landed or is still on the
            // account, so landed + stranded = 100, and what is left to mint is 120 - 100 + 1 = 21
            // however many submissions the stop actually caught.
            Assert.Equal(21, counting.Minted);

            TicketCensus after = await LeftoverTickets.FindAsync(source, writer.ClassicAddress, null, token);
            Assert.Empty(after.Ours);
            Assert.Contains(await source.LiveTicketsAsync(writer.ClassicAddress, token), ticket => ticket.Sequence == foreign);
            Assert.Equal(0, report.TicketsNotReleased);
            Assert.Null(report.TicketReleaseFailure);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_small_publication_after_a_stopped_large_one_releases_what_it_left()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            SessionOptions options = new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };
            byte[] large = VectorStream.Bytes("leftovers-large", FileBytes);

            (XrplWallet writer, uint foreign, _, int fee) = await StrandedAsync(client, sink, source, large, options, token);

            TicketCensus stranded = await LeftoverTickets.FindAsync(source, writer.ClassicAddress, null, token);
            Assert.NotEmpty(stranded.Ours);

            // A different, small file on sequence numbers (spec I7): its tidy-up is what clears them.
            byte[] small = VectorStream.Bytes("leftovers-small", 2_500);
            await using SequenceStrategy sequence = new(sink, new WalletSigner(writer), new SequenceStrategyOptions { FeeDrops = fee }, source);

            PublicationSession session = await PublicationSession.StartAsync(
                sequence, source, small, "text/plain", "small.txt",
                writer.ClassicAddress, "key-standalone", "1", options, token);
            PublicationReport report = await session.RunAsync(null, token);

            Assert.Equal(stranded.Ours.Count, report.TicketsReleased);
            Assert.Equal(0, report.TicketsAlreadyGone);
            Assert.Equal(0, report.TicketsNotReleased);
            Assert.Null(report.TicketReleaseFailure);

            TicketCensus after = await LeftoverTickets.FindAsync(source, writer.ClassicAddress, null, token);
            Assert.Empty(after.Ours);
            Assert.Contains(await source.LiveTicketsAsync(writer.ClassicAddress, token), ticket => ticket.Sequence == foreign);

            // The reserve those tickets locked is free: the account owns its one ticket and nothing else.
            AccountState state = await source.AccountStateAsync(writer.ClassicAddress, token);
            Assert.Equal(1, state.OwnerCount);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
```

- [ ] **Step 3: Run the new integration tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.LeftoverTicketsStandaloneTests"`
Expected: PASS, both. If the first fails on the stranded range or the minted count, report the observed numbers before touching an assertion: the invariant they rest on (a first-batch ticket either carried a landed chunk or is still live) is the thing under test.

- [ ] **Step 4: Run every suite**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration"`
Expected: PASS (the stand must be up; the whole integration set takes several minutes).

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`, `dotnet run --project tests/XrplFile.App.Core.Tests`, `dotnet run --project tests/XrplFile.Keys.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/XrplFile.Core.Tests/LeftoverTicketsStandaloneTests.cs
git commit -m "test(core): a stopped publication's tickets are spent by what follows it, on a node"
```
