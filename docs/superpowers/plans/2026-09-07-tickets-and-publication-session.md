# Tickets and the Publication Session Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish a file to the XRP Ledger fast enough for a person to sit through, with an estimate before the money moves, state that survives a crash, and a throwaway-account mode that returns what it did not spend.

**Architecture:** `XrplFileWriter` is split into three. An `ISubmissionStrategy` owns how transactions reach the ledger, with a sequential implementation that reproduces today's behaviour and a ticketed one that mints tickets in batches and submits in parallel. A `PublicationSession` owns the estimate, the budget ceiling, the state file, resumption and the run report. The writer keeps only the format's order. Tickets change nothing about the format: a chunk's index lives in its `MemoType`, so a reader never depended on submission order.

**Tech Stack:** .NET 10 SDK, C# latest, `Xrpl` 11.2.0, `xunit.v3` 4.0.0 with the built-in `Assert`, a standalone `xrpld` in Docker for integration tests.

**Spec:** `docs/superpowers/specs/2026-09-07-tickets-and-publication-session-design.md` — read it alongside this plan.

## Global Constraints

- Repository language is English everywhere: prose, identifiers, error messages, commit messages. **No AI attribution in commits** — no `Co-Authored-By`, no "Generated with" line. This repository is public-facing and its policy overrides any default.
- Conventional commit messages (`feat:`, `fix:`, `test:`, `docs:`, `chore:`).
- C# style: explicit types instead of `var`; `async`/`await` with `CancellationToken` through every I/O chain and `ConfigureAwait(false)` in library code; `StringComparison.Ordinal` for string comparison; `_camelCase` private fields; English comments only where they say something the code does not.
- **`dotnet test` does not work with xunit.v3 on the .NET 10 SDK.** Test projects are executables: `dotnet run --project tests/XrplFile.Core.Tests -- <xunit filters>`, where filters are `-class`, `-method`, `-trait "Category=Integration"`, `-trait- "Category=Integration"`. No Moq, no FluentAssertions.
- The JavaScript suite must stay green and untouched: `cd web && npm test`. This work does not change the format, so `web/` has no reason to change. A change there is a signal that something went wrong.
- Ticket facts fixed by the protocol, verified in rippled at `5ce0b1c2c`: 1 to 250 tickets per `TicketCreate` (`kMinValidCount`, `kMaxValidCount`); at most 250 live per account (`kMaxTicketThreshold`); a `TicketCreate` at sequence `S` with count `N` mints tickets `S+1` through `S+N` and leaves the sequence at `S+1+N`; a ticketed transaction does not move the sequence at all.
- `AccountDelete` refuses with `tecTOO_SOON` while `account.Sequence + 255 > currentLedgerIndex`, and its fee is one owner reserve (`calculateOwnerReserveFee`).
- Reserve and fee figures are read from the node, never hardcoded.
- A `terQUEUED` transaction has spent nothing and may still land. A `tec*` transaction has spent its ticket and written nothing.
- Integration tests carry `[Trait("Category", "Integration")]`. The stand comes up with:
  ```
  docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml up -d
  ```
  and the tests read `XRPL_FILE_NODE_URL` (use `ws://localhost:16006` with that override). **Never touch a container outside the `xrpl-file-ci` compose project** — another project's node runs on this machine. Take the stand down with the same command ending in `down`.

---

## File structure

```
src/XrplFile.Core/
├── ILedgerSource.cs             # + FeesAsync, BalanceDropsAsync, CurrentLedgerAsync (Task 1)
├── NetworkFees.cs               # NEW: the node's fee and reserve figures (Task 1)
├── XrplClientLedgerSource.cs    # + the three new methods (Task 1)
├── ISubmissionStrategy.cs       # NEW: MemoSubmission, SubmittedMemo, the interface (Task 2)
├── SequenceStrategy.cs          # NEW: today's behaviour behind the interface (Task 2)
├── TicketStrategy.cs            # NEW: mint, submit in parallel, reconcile (Task 3)
├── XrplFileWriter.cs            # takes a strategy; loses sequences and validation waits (Task 2)
├── PublicationEstimate.cs       # NEW: the numbers a person is shown (Task 4)
├── PublicationState.cs          # NEW: what survives a crash, and its JSON (Task 5)
├── PublicationSession.cs        # NEW: estimate, budget, run, report (Task 5, 6)
├── PublicationReport.cs         # NEW: the run dataset (Task 5)
├── ThrowawayAccount.cs          # NEW: provision, await funding, refund, deletion arithmetic (Task 7)
└── XrplFileException.cs         # + InsufficientBalance, BudgetExceeded, ResumeMismatch (Task 4, 6)

tests/XrplFile.Core.Tests/
├── FakeLedgerSource.cs          # + the three new members (Task 1)
├── FakeLedgerSink.cs            # + ticket awareness (Task 3)
├── AccountDeleteThresholdTests.cs  # NEW, Integration: the measurement (Task 0)
├── SequenceStrategyTests.cs     # NEW (Task 2)
├── TicketStrategyTests.cs       # NEW (Task 3)
├── TicketStrategyStandaloneTests.cs # NEW, Integration (Task 3)
├── PublicationEstimateTests.cs  # NEW (Task 4)
├── PublicationSessionTests.cs   # NEW (Task 5, 6)
├── PublicationSessionStandaloneTests.cs # NEW, Integration (Task 6)
├── ThrowawayAccountTests.cs     # NEW (Task 7)
├── ThrowawayAccountStandaloneTests.cs # NEW, Integration (Task 7)
└── XrplFileWriterTests.cs       # moves onto SequenceStrategy, assertions unchanged (Task 2)
```

Tasks run in order. Task 0 settles a fact the later tasks state as truth; nothing else depends on its code.

---

### Task 0: Measure the deletion threshold

The spec's section on why the account cannot be deleted is read from rippled's source, not measured. This task settles it on the stand before anything is built on it. Its deliverable is an integration test that encodes the rule and fails if rippled ever changes it.

**Files:**
- Create: `tests/XrplFile.Core.Tests/AccountDeleteThresholdTests.cs`

**Interfaces:**
- Consumes: `XrplClientLedgerSink` (`SequenceAsync`, `SubmitAsync`), `XrplClientLedgerSource` (`TransactionAsync`), `XrplWallet` (`Generate`, `FromSeed`, `ClassicAddress`, `PublicKey`), all as they exist today.
- Produces: the measured rule, quoted in the spec by Task 8. No production code.

- [ ] **Step 1: Write the failing test**

`tests/XrplFile.Core.Tests/AccountDeleteThresholdTests.cs`:
```csharp
using System.Text.Json;
using Xrpl.Client;
using Xrpl.Client.Json;
using Xrpl.Models.Methods;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// What the ledger actually does about deleting an account that minted tickets.
///
/// The design reads three things out of rippled's source: AccountDelete refuses while
/// Sequence + 255 exceeds the current ledger index, TicketCreate moves the sequence by the
/// ticket count plus one, and the fee is one owner reserve. All three decide whether a
/// throwaway account's base reserve can ever come back, so none of them is taken on trust.
/// </summary>
[Trait("Category", "Integration")]
public class AccountDeleteThresholdTests
{
    private const string GenesisSeed = "snoPBrXtMeMyMHUVTgbuqAfg1SUTb";
    private const int SeqDelta = 255;

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    [Fact]
    public async Task Minting_tickets_pushes_the_deletion_threshold_away_and_only_closed_ledgers_bring_it_back()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet funder = XrplWallet.FromSeed(GenesisSeed);
            XrplWallet throwaway = XrplWallet.Generate();

            await FundAsync(client, sink, funder, throwaway.ClassicAddress, 50_000_000, token);

            // A new account's sequence is the index of the ledger that created it.
            uint created = await sink.SequenceAsync(throwaway.ClassicAddress, validated: true, token);
            long createdIn = await CurrentLedgerAsync(client, token);
            Assert.InRange(created, createdIn - 5, createdIn);

            // The mint moves the sequence by the count plus one, and by nothing else.
            const uint mint = 5;
            uint beforeMint = await sink.SequenceAsync(throwaway.ClassicAddress, validated: false, token);
            await MintTicketsAsync(client, sink, throwaway, beforeMint, mint, token);
            uint afterMint = await sink.SequenceAsync(throwaway.ClassicAddress, validated: true, token);

            Assert.Equal(beforeMint + mint + 1, afterMint);

            // Too soon: the threshold sits at Sequence + 255, well past the current index.
            long now = await CurrentLedgerAsync(client, token);
            Assert.True(afterMint + SeqDelta > now, $"the test needs the threshold ahead of ledger {now}");

            string tooSoon = await AttemptDeleteAsync(client, sink, throwaway, funder.ClassicAddress, token);
            Assert.Equal("tecTOO_SOON", tooSoon);

            // Nothing but closed ledgers moves it. Drive them until the threshold is met exactly.
            long threshold = afterMint + SeqDelta;
            await CloseLedgersUntilAsync(client, threshold, token);

            string accepted = await AttemptDeleteAsync(client, sink, throwaway, funder.ClassicAddress, token);
            Assert.True(accepted is "tesSUCCESS" or "terQUEUED", $"at ledger {threshold} the node answered {accepted}");
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task An_ordinary_transaction_moves_the_threshold_by_one_so_it_never_closes_the_gap()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplWallet funder = XrplWallet.FromSeed(GenesisSeed);
            XrplWallet account = XrplWallet.Generate();

            await FundAsync(client, sink, funder, account.ClassicAddress, 50_000_000, token);

            uint before = await sink.SequenceAsync(account.ClassicAddress, validated: true, token);

            // An AccountSet with no fields: the cheapest transaction there is.
            uint sequence = await sink.SequenceAsync(account.ClassicAddress, validated: false, token);
            Dictionary<string, object> noop = new()
            {
                ["TransactionType"] = "AccountSet",
                ["Account"] = account.ClassicAddress,
                ["Sequence"] = sequence,
                ["Fee"] = "12",
                ["SigningPubKey"] = account.PublicKey,
            };
            SubmitOutcome outcome = await sink.SubmitAsync(noop, account, token);
            Assert.True(outcome.EngineResult is "tesSUCCESS" or "terQUEUED", outcome.EngineResult);
            await WaitForSequenceAsync(sink, account.ClassicAddress, sequence + 1, token);

            uint after = await sink.SequenceAsync(account.ClassicAddress, validated: true, token);

            // Every transaction raises the sequence, and so raises the threshold with it. Sending
            // transactions can therefore never close the gap; only ledgers closing can.
            Assert.Equal(before + 1, after);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    private static async Task<long> CurrentLedgerAsync(IXrplClient client, CancellationToken token)
    {
        XrplResponse<object> response = await client.GRequest<object, LedgerRequest>(
            new LedgerRequest { LedgerIndex = new Xrpl.Models.Common.LedgerIndex(Xrpl.Models.Ledger.LedgerIndexType.Validated) }, token);

        return response.Raw.ToJsonElement().GetProperty("ledger_index").GetInt64();
    }

    /// <summary>
    /// Closes ledgers until the validated index reaches the target. The stand's sidecar closes one
    /// every few seconds on its own; this drives them as fast as the node will take them, which is
    /// what makes a 255-ledger wait a matter of seconds rather than a quarter of an hour.
    /// </summary>
    private static async Task CloseLedgersUntilAsync(IXrplClient client, long target, CancellationToken token)
    {
        while (await CurrentLedgerAsync(client, token) < target)
        {
            token.ThrowIfCancellationRequested();
            await client.GRequest<object, LedgerAcceptRequest>(new LedgerAcceptRequest(), token);
        }
    }

    private static async Task MintTicketsAsync(
        IXrplClient client, XrplClientLedgerSink sink, XrplWallet wallet, uint sequence, uint count, CancellationToken token)
    {
        Dictionary<string, object> mint = new()
        {
            ["TransactionType"] = "TicketCreate",
            ["Account"] = wallet.ClassicAddress,
            ["Sequence"] = sequence,
            ["TicketCount"] = count,
            ["Fee"] = "12",
            ["SigningPubKey"] = wallet.PublicKey,
        };

        SubmitOutcome outcome = await sink.SubmitAsync(mint, wallet, token);
        Assert.True(outcome.EngineResult is "tesSUCCESS" or "terQUEUED", outcome.EngineResult);

        await WaitForSequenceAsync(sink, wallet.ClassicAddress, sequence + count + 1, token);
    }

    /// <summary>Submits an AccountDelete and returns what the node said, without asserting on it.</summary>
    private static async Task<string> AttemptDeleteAsync(
        IXrplClient client, XrplClientLedgerSink sink, XrplWallet wallet, string destination, CancellationToken token)
    {
        // The fee is one owner reserve, per AccountDelete::calculateBaseFee. Read it rather than
        // guess: the figure has changed before and a wrong fee is telINSUF_FEE_P, not tecTOO_SOON.
        long ownerReserve = await OwnerReserveDropsAsync(client, token);
        uint sequence = await sink.SequenceAsync(wallet.ClassicAddress, validated: false, token);

        Dictionary<string, object> delete = new()
        {
            ["TransactionType"] = "AccountDelete",
            ["Account"] = wallet.ClassicAddress,
            ["Destination"] = destination,
            ["Sequence"] = sequence,
            ["Fee"] = ownerReserve.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SigningPubKey"] = wallet.PublicKey,
        };

        return (await sink.SubmitAsync(delete, wallet, token)).EngineResult;
    }

    private static async Task<long> OwnerReserveDropsAsync(IXrplClient client, CancellationToken token)
    {
        XrplResponse<object> response = await client.GRequest<object, ServerStateRequest>(new ServerStateRequest(), token);
        JsonElement state = response.Raw.ToJsonElement().GetProperty("state").GetProperty("validated_ledger");

        return state.GetProperty("reserve_inc").GetInt64();
    }

    private static async Task FundAsync(
        IXrplClient client, XrplClientLedgerSink sink, XrplWallet funder, string destination, long drops, CancellationToken token)
    {
        uint sequence = await sink.SequenceAsync(funder.ClassicAddress, validated: false, token);

        Dictionary<string, object> payment = new()
        {
            ["TransactionType"] = "Payment",
            ["Account"] = funder.ClassicAddress,
            ["Destination"] = destination,
            ["Amount"] = drops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Sequence"] = sequence,
            ["Fee"] = "12",
            ["SigningPubKey"] = funder.PublicKey,
        };

        SubmitOutcome outcome = await sink.SubmitAsync(payment, funder, token);
        Assert.True(outcome.EngineResult is "tesSUCCESS" or "terQUEUED", outcome.EngineResult);

        await WaitForSequenceAsync(sink, funder.ClassicAddress, sequence + 1, token);
    }

    private static async Task WaitForSequenceAsync(
        XrplClientLedgerSink sink, string account, uint expected, CancellationToken token)
    {
        while (await sink.SequenceAsync(account, validated: true, token) < expected)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }
}
```

- [ ] **Step 2: Build to see it fail**

Run: `dotnet build XrplFile.sln`
Expected: compile errors naming `LedgerAcceptRequest` and `ServerStateRequest` if the SDK spells them differently. Grep the SDK for the right names before inventing one: `grep -rn "class LedgerAcceptRequest\|class ServerStateRequest" E:/GIT/XRPL/XrplCSharp/Xrpl/Models/Methods/`. If `LedgerAcceptRequest` does not exist, send it as a raw request the way `XrplClientLedgerSource.SendAsync` does, or call the JSON-RPC port with `HttpClient` (`http://127.0.0.1:15005`, body `{"method":"ledger_accept"}`). Record which you used in the report.

- [ ] **Step 3: Run it against the stand**

```bash
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml up -d
```
Wait about fifteen seconds, then:
```bash
XRPL_FILE_NODE_URL=ws://localhost:16006 dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.AccountDeleteThresholdTests"
```
Expected: both tests pass. Take the stand down afterwards.

If `tecTOO_SOON` does not appear, or deletion succeeds before the threshold, **stop and report**: the design's section 4 is wrong and the throwaway mode's refund needs redesigning before Task 7 is written.

- [ ] **Step 4: Commit**

```bash
git add tests/XrplFile.Core.Tests/AccountDeleteThresholdTests.cs
git commit -m "test(standalone): the deletion threshold moves with the sequence, not with transactions"
```

---
### Task 1: What the node says about money

The estimate needs three figures nothing currently asks for: what a transaction costs right now, what the reserves are, and what the account holds. They go on `ILedgerSource`, which is already the library's one place for "what a node tells us". The reader does not use them.

**Files:**
- Create: `src/XrplFile.Core/NetworkFees.cs`
- Modify: `src/XrplFile.Core/ILedgerSource.cs`, `src/XrplFile.Core/XrplClientLedgerSource.cs`, `tests/XrplFile.Core.Tests/FakeLedgerSource.cs`
- Test: `tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs`

**Interfaces:**
- Consumes: `XrplClientLedgerSource`'s internal request delegate seam (`internal XrplClientLedgerSource(Func<BaseRequest, CancellationToken, Task<JsonElement>> request)`) and its `AskAsync` wrapper, both as they exist today.
- Produces:
  - `public sealed record NetworkFees(long OpenLedgerFeeDrops, long BaseReserveDrops, long OwnerReserveDrops)`
  - `Task<NetworkFees> FeesAsync(CancellationToken cancellationToken)` on `ILedgerSource`
  - `Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken)` on `ILedgerSource`
  - `Task<long> CurrentLedgerAsync(CancellationToken cancellationToken)` on `ILedgerSource`
  - On `FakeLedgerSource`: settable `NetworkFees Fees`, `Dictionary<string, long> Balances`, `long CurrentLedger`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs`:
```csharp
    [Fact]
    public async Task The_fees_and_reserves_come_from_server_state()
    {
        ScriptedNode node = new();
        node.Answers.Add("""
            {"state":{"validated_ledger":{"base_fee":10,"reserve_base":1000000,"reserve_inc":200000,"seq":106497159}}}
            """);

        NetworkFees fees = await new XrplClientLedgerSource(node.RequestAsync).FeesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10, fees.OpenLedgerFeeDrops);
        Assert.Equal(1_000_000, fees.BaseReserveDrops);
        Assert.Equal(200_000, fees.OwnerReserveDrops);
    }

    [Fact]
    public async Task A_load_factor_raises_the_fee_the_estimate_uses()
    {
        ScriptedNode node = new();
        // The node scales the base fee by load_factor / load_base when it is busy. An estimate
        // built on the base fee alone would be wrong exactly when it matters.
        node.Answers.Add("""
            {"state":{"validated_ledger":{"base_fee":10,"reserve_base":1000000,"reserve_inc":200000},"load_factor":2000,"load_base":256}}
            """);

        NetworkFees fees = await new XrplClientLedgerSource(node.RequestAsync).FeesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(78, fees.OpenLedgerFeeDrops);
    }

    [Fact]
    public async Task An_account_balance_comes_from_account_info()
    {
        ScriptedNode node = new();
        node.Answers.Add("""{"account_data":{"Account":"rWriter","Balance":"25000000","Sequence":7}}""");

        long drops = await new XrplClientLedgerSource(node.RequestAsync)
            .BalanceDropsAsync("rWriter", TestContext.Current.CancellationToken);

        Assert.Equal(25_000_000, drops);
    }

    [Fact]
    public async Task An_account_the_node_does_not_know_holds_nothing()
    {
        ScriptedNode node = new();
        node.Answers.Add("""{"error":"actNotFound"}""");

        long drops = await new XrplClientLedgerSource(node.RequestAsync)
            .BalanceDropsAsync("rNobody", TestContext.Current.CancellationToken);

        Assert.Equal(0, drops);
    }

    [Fact]
    public async Task The_current_ledger_is_the_validated_index()
    {
        ScriptedNode node = new();
        node.Answers.Add("""{"ledger_index":106497159,"validated":true,"ledger":{"ledger_index":"106497159"}}""");

        long ledger = await new XrplClientLedgerSource(node.RequestAsync)
            .CurrentLedgerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(106497159, ledger);
    }

    [Fact]
    public async Task A_node_that_will_not_say_what_it_charges_is_unreachable()
    {
        ScriptedNode node = new();
        node.Answers.Add("""{"error":"noNetwork"}""");

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplClientLedgerSource(node.RequestAsync).FeesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }
```

If `XrplClientLedgerSourceTests` has no `ScriptedNode` helper yet, add one at the bottom of the file:
```csharp
/// <summary>A node that answers from a list, in order, and records what it was asked.</summary>
internal sealed class ScriptedNode
{
    public List<string> Answers { get; } = [];

    public List<string> Commands { get; } = [];

    private int _at;

    public Task<JsonElement> RequestAsync(Xrpl.Models.Methods.BaseRequest request, CancellationToken cancellationToken)
    {
        Commands.Add(request.Command);

        if (_at >= Answers.Count)
        {
            throw new InvalidOperationException($"the script ran out after {_at} answers; {request.Command} was not expected");
        }

        return Task.FromResult(JsonDocument.Parse(Answers[_at++]).RootElement.Clone());
    }
}
```

- [ ] **Step 2: Build to see the tests fail**

Run: `dotnet build XrplFile.sln`
Expected: compile errors naming `NetworkFees`, `FeesAsync`, `BalanceDropsAsync`, `CurrentLedgerAsync`.

- [ ] **Step 3: Write the implementation**

`src/XrplFile.Core/NetworkFees.cs`:
```csharp
namespace XrplFile;

/// <summary>
/// What a transaction costs and what an object locks up, as the node reports them now.
///
/// None of these is a constant. The reserves have been lowered twice and the fee moves with the
/// network's load, so an estimate that carries its own numbers is an estimate that is wrong on
/// the day it matters.
/// </summary>
/// <param name="OpenLedgerFeeDrops">What one transaction costs at the current load.</param>
/// <param name="BaseReserveDrops">What an account must hold to exist.</param>
/// <param name="OwnerReserveDrops">What each owned object, a live ticket included, locks up.</param>
public sealed record NetworkFees(long OpenLedgerFeeDrops, long BaseReserveDrops, long OwnerReserveDrops);
```

Add to `ILedgerSource`:
```csharp
    /// <summary>What the network charges and reserves right now.</summary>
    Task<NetworkFees> FeesAsync(CancellationToken cancellationToken);

    /// <summary>What the account holds, in drops. An account the node does not know holds nothing.</summary>
    Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken);

    /// <summary>The index of the latest validated ledger.</summary>
    Task<long> CurrentLedgerAsync(CancellationToken cancellationToken);
```

Add to `XrplClientLedgerSource`:
```csharp
    public async Task<NetworkFees> FeesAsync(CancellationToken cancellationToken)
    {
        JsonElement answer = await AskAsync(new ServerStateRequest(), "server_state", cancellationToken).ConfigureAwait(false);

        if (!answer.TryGetProperty("state", out JsonElement state)
            || !state.TryGetProperty("validated_ledger", out JsonElement ledger))
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable, "the node answered server_state without a validated ledger");
        }

        long baseFee = Number(ledger, "base_fee") ?? throw new XrplFileException(XrplFileError.NodeUnreachable, "the node did not say what it charges");
        long baseReserve = Number(ledger, "reserve_base") ?? throw new XrplFileException(XrplFileError.NodeUnreachable, "the node did not say its base reserve");
        long ownerReserve = Number(ledger, "reserve_inc") ?? throw new XrplFileException(XrplFileError.NodeUnreachable, "the node did not say its owner reserve");

        // A busy node multiplies its base fee by load_factor over load_base. Ignoring that is how
        // an estimate comes out right on a quiet network and short on a busy one.
        long loadFactor = Number(answer, "load_factor") ?? Number(state, "load_factor") ?? 256;
        long loadBase = Number(answer, "load_base") ?? Number(state, "load_base") ?? 256;
        if (loadBase <= 0) loadBase = 256;

        long fee = (long)Math.Ceiling(baseFee * (double)loadFactor / loadBase);

        return new NetworkFees(Math.Max(fee, baseFee), baseReserve, ownerReserve);
    }

    public async Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken)
    {
        JsonElement answer;

        try
        {
            answer = await AskAsync(new AccountInfoRequest(account), "account_info", cancellationToken).ConfigureAwait(false);
        }
        catch (XrplFileException e) when (e.Message.Contains("actNotFound", StringComparison.Ordinal))
        {
            // An account nobody funded is not a failure to answer: it is an answer, and it is zero.
            return 0;
        }

        return answer.TryGetProperty("account_data", out JsonElement data) && Text(data, "Balance") is { } balance
            && long.TryParse(balance, out long drops)
                ? drops
                : 0;
    }

    public async Task<long> CurrentLedgerAsync(CancellationToken cancellationToken)
    {
        JsonElement answer = await AskAsync(
            new LedgerRequest { LedgerIndex = new LedgerIndex(LedgerIndexType.Validated) }, "ledger", cancellationToken).ConfigureAwait(false);

        // The index sits at the top of the answer, and inside the ledger object as a string.
        if (Number(answer, "ledger_index") is { } top) return top;

        if (answer.TryGetProperty("ledger", out JsonElement ledger) && Text(ledger, "ledger_index") is { } text
            && long.TryParse(text, out long parsed))
        {
            return parsed;
        }

        throw new XrplFileException(XrplFileError.NodeUnreachable, "the node answered ledger without an index");
    }
```

`SendAsync`'s switch gains three cases:
```csharp
            ServerStateRequest state => _client!.GRequest<object, ServerStateRequest>(state, cancellationToken),
            AccountInfoRequest info => _client!.GRequest<object, AccountInfoRequest>(info, cancellationToken),
            LedgerRequest ledger => _client!.GRequest<object, LedgerRequest>(ledger, cancellationToken),
```

Add the usings the new code needs: `Xrpl.Models.Common` for `LedgerIndex`, and an alias for `LedgerIndexType` the way `XrplClientLedgerSink.cs` already does (`Xrpl.Models.Ledger` declares its own `LedgerTransaction`, which clashes):
```csharp
using LedgerIndexType = Xrpl.Models.Ledger.LedgerIndexType;
```

Add to `FakeLedgerSource`:
```csharp
    /// <summary>What this fake node charges and reserves. Ten drops, one XRP, a fifth of an XRP.</summary>
    public NetworkFees Fees { get; set; } = new(10, 1_000_000, 200_000);

    public Dictionary<string, long> Balances { get; } = [];

    public long CurrentLedger { get; set; } = 1;

    public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => Task.FromResult(Fees);

    public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) =>
        Task.FromResult(Balances.TryGetValue(account, out long drops) ? drops : 0);

    public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => Task.FromResult(CurrentLedger);
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.XrplClientLedgerSourceTests"`
Expected: all pass. If `The_current_ledger_is_the_validated_index` fails, check which shape the SDK's `LedgerRequest` answer takes and cover both, as the code above does.

Then the whole non-integration suite: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` — everything still green, because nothing else changed.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core/NetworkFees.cs src/XrplFile.Core/ILedgerSource.cs src/XrplFile.Core/XrplClientLedgerSource.cs tests/XrplFile.Core.Tests/FakeLedgerSource.cs tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs
git commit -m "feat(core): the node's fees, reserves and balances, for an estimate to stand on"
```

---

### Task 2: A submission strategy, and the writer on top of it

The writer stops deciding how transactions reach the ledger. Today's behaviour moves behind an interface unchanged, which is what makes the ticketed one in Task 3 a swap rather than a rewrite.

**Files:**
- Create: `src/XrplFile.Core/ISubmissionStrategy.cs`, `src/XrplFile.Core/SequenceStrategy.cs`, `tests/XrplFile.Core.Tests/SequenceStrategyTests.cs`
- Modify: `src/XrplFile.Core/XrplFileWriter.cs`, `tests/XrplFile.Core.Tests/XrplFileWriterTests.cs`

**Interfaces:**
- Consumes: `ILedgerSink` (`SequenceAsync`, `SubmitAsync`) and `MemoLayout` as they exist today.
- Produces:
  - `public sealed record MemoSubmission(string MemoTypeHex, string MemoDataHex, string? MemoFormatHex)`
  - `public sealed record SubmittedMemo(int Index, string TxHash)`
  - `public interface ISubmissionStrategy : IAsyncDisposable` with `Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)`
  - `public sealed class SequenceStrategyOptions { int FeeDrops = 12; int BatchSize = 200; TimeSpan ValidationTimeout = 3 min; TimeSpan ValidationPoll = 1 s; }`
  - `public sealed class SequenceStrategy(ILedgerSink sink, XrplWallet wallet, SequenceStrategyOptions? options = null) : ISubmissionStrategy`
  - `XrplFileWriter`'s constructor becomes `XrplFileWriter(ISubmissionStrategy strategy, ILedgerSource source, WriterOptions? options = null)`, and `WriterOptions` loses `FeeDrops`, `BatchSize`, `ValidationTimeout` and `ValidationPoll`, keeping only `ReadBack`.
  - `PublishAsync` keeps its parameter list except that `XrplWallet wallet` becomes `string writer` — the strategy holds the wallet now.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/SequenceStrategyTests.cs`:
```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class SequenceStrategyTests
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();

    private static IReadOnlyList<MemoSubmission> Memos(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex([(byte)i]), null))];

    [Fact]
    public async Task Every_memo_is_submitted_in_order_on_consecutive_sequences()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        IReadOnlyList<SubmittedMemo> submitted = await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);

        Assert.Equal([0, 1, 2], submitted.Select(memo => memo.Index).ToArray());
        Assert.Equal(new uint[] { 10, 11, 12 }, sink.Submitted.Select(tx => Convert.ToUInt32(tx["Sequence"])).ToArray());
        Assert.All(sink.Submitted, tx => Assert.Equal("AccountSet", tx["TransactionType"]));
    }

    [Fact]
    public async Task A_batch_waits_for_validation_before_the_next_one_starts()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet, new SequenceStrategyOptions { BatchSize = 2 });

        await strategy.SubmitAllAsync(Memos(5), null, TestContext.Current.CancellationToken);

        // Two submissions, a validated-sequence query, two more, a query, one more, the final query.
        Assert.Equal(
            ["chunk", "chunk", "query", "chunk", "chunk", "query", "chunk", "query"],
            sink.Events.ToArray());
    }

    [Fact]
    public async Task A_refused_submission_stops_the_run_and_names_the_engine_result()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.Refusals[1] = "tecINSUFFICIENT_RESERVE";
        await using SequenceStrategy strategy = new(sink, Wallet);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Contains("tecINSUFFICIENT_RESERVE", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, sink.Submitted.Count);
    }

    [Fact]
    public async Task Progress_counts_submissions_as_they_are_accepted()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        List<int> seen = [];
        await using SequenceStrategy strategy = new(sink, Wallet);

        await strategy.SubmitAllAsync(Memos(4), new Progress<int>(seen.Add), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3, 4], seen.Order().ToArray());
    }

    [Fact]
    public async Task A_memo_format_reaches_the_transaction_only_when_it_is_given()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        await strategy.SubmitAllAsync(
            [new MemoSubmission(MemoLayout.ChunkMarkerHex(0), "00", null),
             new MemoSubmission(MemoLayout.ManifestMarkerHex, "01", MemoLayout.JsonFormatHex)],
            null, TestContext.Current.CancellationToken);

        Assert.False(MemoOf(sink.Submitted[0]).ContainsKey("MemoFormat"));
        Assert.Equal(MemoLayout.JsonFormatHex, MemoOf(sink.Submitted[1])["MemoFormat"]);
    }

    private static Dictionary<string, string> MemoOf(Dictionary<string, object> tx)
    {
        System.Text.Json.JsonElement memo = System.Text.Json.JsonSerializer.SerializeToElement(tx["Memos"])[0].GetProperty("Memo");
        Dictionary<string, string> fields = [];
        foreach (System.Text.Json.JsonProperty property in memo.EnumerateObject())
        {
            fields[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return fields;
    }
}
```

- [ ] **Step 2: Build to see them fail**

Run: `dotnet build XrplFile.sln`
Expected: compile errors naming `SequenceStrategy`, `SequenceStrategyOptions`, `MemoSubmission`, `SubmittedMemo`.

- [ ] **Step 3: Write the interface and the sequential strategy**

`src/XrplFile.Core/ISubmissionStrategy.cs`:
```csharp
namespace XrplFile;

/// <summary>One memo on its way into the ledger: what a transaction will carry.</summary>
public sealed record MemoSubmission(string MemoTypeHex, string MemoDataHex, string? MemoFormatHex);

/// <summary>Where a memo landed: its position in the batch, and the transaction that carried it.</summary>
public sealed record SubmittedMemo(int Index, string TxHash);

/// <summary>
/// How a set of memos reaches the ledger.
///
/// The contract is deliberately strong: when the call returns, every memo is in a validated
/// ledger and its transaction hash is known. How that was achieved — one at a time on the
/// account's sequence, or many at once on tickets — is the implementation's business and
/// nobody else's. A strategy holds state between calls, which is why it is disposable: a
/// ticketed one keeps unspent tickets so a later phase does not mint its own.
/// </summary>
public interface ISubmissionStrategy : IAsyncDisposable
{
    Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken);
}
```

`src/XrplFile.Core/SequenceStrategy.cs`:
```csharp
using Xrpl.Wallet;

namespace XrplFile;

public sealed class SequenceStrategyOptions
{
    public int FeeDrops { get; init; } = 12;

    /// <summary>
    /// After this many submissions the strategy waits for validation, so an open ledger never
    /// holds more than a batch of this account's transactions. A node was measured refusing one
    /// account beyond about a thousand of them.
    /// </summary>
    public int BatchSize { get; init; } = 200;

    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan ValidationPoll { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// One transaction at a time on the account's sequence: about three a second, and no reserve
/// beyond the account's own. What the library did before tickets, moved behind the interface
/// without a change in behaviour.
/// </summary>
public sealed class SequenceStrategy(ILedgerSink sink, XrplWallet wallet, SequenceStrategyOptions? options = null) : ISubmissionStrategy
{
    private readonly ILedgerSink _sink = sink;
    private readonly XrplWallet _wallet = wallet;
    private readonly SequenceStrategyOptions _options = options ?? new SequenceStrategyOptions();

    public async Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
    {
        string account = _wallet.ClassicAddress;
        uint sequence = await _sink.SequenceAsync(account, validated: false, cancellationToken).ConfigureAwait(false);

        List<SubmittedMemo> landed = new(memos.Count);
        int sinceValidation = 0;

        for (int i = 0; i < memos.Count; i++)
        {
            landed.Add(new SubmittedMemo(i, await SubmitAsync(memos[i], sequence++, cancellationToken).ConfigureAwait(false)));
            submitted?.Report(landed.Count);

            if (++sinceValidation >= _options.BatchSize)
            {
                await WaitForValidationAsync(account, sequence, cancellationToken).ConfigureAwait(false);
                sinceValidation = 0;
            }
        }

        await WaitForValidationAsync(account, sequence, cancellationToken).ConfigureAwait(false);

        return landed;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<string> SubmitAsync(MemoSubmission memo, uint sequence, CancellationToken cancellationToken)
    {
        Dictionary<string, object> fields = new() { ["MemoType"] = memo.MemoTypeHex, ["MemoData"] = memo.MemoDataHex };
        if (memo.MemoFormatHex is not null) fields["MemoFormat"] = memo.MemoFormatHex;

        Dictionary<string, object> transaction = new()
        {
            ["TransactionType"] = "AccountSet",
            ["Account"] = _wallet.ClassicAddress,
            ["Sequence"] = sequence,
            ["Fee"] = _options.FeeDrops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SigningPubKey"] = _wallet.PublicKey,
            ["Memos"] = new List<object> { new Dictionary<string, object> { ["Memo"] = fields } },
        };

        SubmitOutcome outcome = await _sink.SubmitAsync(transaction, _wallet, cancellationToken).ConfigureAwait(false);

        if (outcome.EngineResult is not ("tesSUCCESS" or "terQUEUED"))
        {
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"the node answered {outcome.EngineResult} to the transaction at sequence {sequence}");
        }

        return outcome.TxHash;
    }

    private async Task WaitForValidationAsync(string account, uint nextSequence, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.ValidationTimeout;

        while (true)
        {
            uint validated;

            try
            {
                validated = await _sink.SequenceAsync(account, validated: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Xrpl.Client.Exceptions.TimeoutException)
            {
                // One unanswered poll is not a failed publication; the deadline below is.
                validated = 0;
            }

            if (validated >= nextSequence) return;

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new XrplFileException(XrplFileError.ValidationTimeout,
                    $"the validated sequence of {account} is {validated} and {nextSequence} was expected after {_options.ValidationTimeout}");
            }

            await Task.Delay(_options.ValidationPoll, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 4: Run the strategy tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.SequenceStrategyTests"`
Expected: all pass.

- [ ] **Step 5: Move the writer onto the strategy**

In `src/XrplFile.Core/XrplFileWriter.cs`:
- `WriterOptions` keeps only `ReadBack`. Delete `FeeDrops`, `ValidationTimeout`, `ValidationPoll` and `BatchSize` from it; they now live in the strategy's options.
- The constructor becomes `XrplFileWriter(ISubmissionStrategy strategy, ILedgerSource source, WriterOptions? options = null)`.
- `PublishAsync`'s `XrplWallet wallet` parameter becomes `string writer`. The wallet reached the writer only to sign and to name the account; the strategy signs now, and the caller passes the address.
- Delete `SubmitMemoAsync`, `WaitForValidationAsync` and the sequence handling entirely.
- Each phase becomes one call. The chunks phase:
```csharp
        IReadOnlyList<MemoSubmission> chunkMemos =
            [.. chunks.Select((chunk, i) => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunk), null))];

        IReadOnlyList<SubmittedMemo> chunksLanded = await _strategy.SubmitAllAsync(
            chunkMemos,
            new Progress<int>(done => progress?.Report(new PublishProgress("chunks", done, chunkMemos.Count))),
            cancellationToken).ConfigureAwait(false);

        List<string> chunkHashes = [.. chunksLanded.OrderBy(memo => memo.Index).Select(memo => memo.TxHash)];
```
  The lists phase is the same with `MemoLayout.ListMarkerHex(j)`, `MemoLayout.JsonFormatHex` and the stage name `"lists"`. The manifest is a single-element call with `MemoLayout.ManifestMarkerHex`.
- The `NodeUnreachable` and `SubmitRefused` catches in `PublishAsync` stay exactly as they are.

- [ ] **Step 6: Move the writer's tests onto the strategy**

In `tests/XrplFile.Core.Tests/XrplFileWriterTests.cs`, every construction becomes:
```csharp
        await using SequenceStrategy strategy = new(sink, Wallet, new SequenceStrategyOptions { BatchSize = 200 });
        XrplFileWriter writer = new(strategy, ledger);
        ... await writer.PublishAsync(bytes, "text/plain", null, Wallet.ClassicAddress, "testnet", null, token);
```
**No assertion changes.** The sequential path must behave exactly as before; an assertion that needs editing means behaviour moved, which this task does not intend. The two tests that assert on `WriterOptions { BatchSize = … }` pass it to `SequenceStrategyOptions` instead.

- [ ] **Step 7: Run everything**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: green, with the same count as before plus the five new strategy tests.

Then the stand, because `StandaloneTests` constructs a writer too:
```bash
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml up -d
XRPL_FILE_NODE_URL=ws://localhost:16006 dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration"
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml down
```
Expected: green, Task 0's two tests included.

- [ ] **Step 8: Commit**

```bash
git add src/XrplFile.Core/ISubmissionStrategy.cs src/XrplFile.Core/SequenceStrategy.cs src/XrplFile.Core/XrplFileWriter.cs tests/XrplFile.Core.Tests/SequenceStrategyTests.cs tests/XrplFile.Core.Tests/XrplFileWriterTests.cs tests/XrplFile.Core.Tests/StandaloneTests.cs
git commit -m "refactor(core): how a transaction reaches the ledger is a strategy, not the writer's business"
```

---
### Task 3: The ticket strategy

**Files:**
- Create: `src/XrplFile.Core/TicketStrategy.cs`, `tests/XrplFile.Core.Tests/TicketStrategyTests.cs`, `tests/XrplFile.Core.Tests/TicketStrategyStandaloneTests.cs`
- Modify: `tests/XrplFile.Core.Tests/FakeLedgerSink.cs`

**Interfaces:**
- Consumes: `ISubmissionStrategy`, `MemoSubmission`, `SubmittedMemo` (Task 2); `ILedgerSink.SequenceAsync/SubmitAsync`; `ILedgerSource.AccountTransactionsAsync/TransactionAsync/CurrentLedgerAsync` (Task 1); `LedgerTransaction`.
- Produces:
  - `public sealed class TicketStrategyOptions { int FeeDrops = 12; int TicketsPerBatch = 100; int MaxInFlight = 25; TimeSpan BatchTimeout = 3 min; TimeSpan SweepPoll = 2 s; TimeSpan ValidationTimeout = 3 min; TimeSpan ValidationPoll = 1 s; int MaxAttemptsPerMemo = 3; }`
  - `public sealed class TicketStrategy(ILedgerSink sink, ILedgerSource source, XrplWallet wallet, TicketStrategyOptions? options = null) : ISubmissionStrategy`
  - `public const int MaxTicketsPerMint = 250` and `public const int MaxLiveTickets = 250` on `TicketStrategy`.
  - On `FakeLedgerSink`: `HashSet<uint> LiveTickets`, and `TicketCreate` handling that mints `S+1..S+N` and leaves the sequence at `S+1+N`.

The batch loop, from the spec: mint, submit in parallel, reconcile by sweeping `account_tx`, carry the missing into the next batch. The sweep replaces the sequence-based validation wait, which cannot work here because ticketed transactions do not move the sequence.

- [ ] **Step 1: Teach the fake sink about tickets**

In `tests/XrplFile.Core.Tests/FakeLedgerSink.cs`, add to the class:
```csharp
    /// <summary>Tickets this fake account holds, minted by TicketCreate and spent by use.</summary>
    public HashSet<uint> LiveTickets { get; } = [];

    /// <summary>Which submissions (by position) answer terQUEUED and land only on a later sweep.</summary>
    public Dictionary<int, int> QueuedUntilSweep { get; } = [];
```
and inside `SubmitAsync`, before the existing memo handling:
```csharp
        if (transaction.TryGetValue("TransactionType", out object? kind) && (string)kind == "TicketCreate")
        {
            uint at = Convert.ToUInt32(transaction["Sequence"]);
            uint count = Convert.ToUInt32(transaction["TicketCount"]);

            // Exactly what rippled does: tickets at+1..at+count, and the sequence lands at at+1+count.
            for (uint ticket = at + 1; ticket <= at + count; ticket++) LiveTickets.Add(ticket);
            _nextSequence = at + count + 1;

            Events.Add("mint");
            ledger.Add(new LedgerTransaction(
                hash, firstLedger + position / 100 + _ledgersClosed, (string)transaction["Account"],
                Succeeded: true, Validated: true, MemoTypeHex: null, MemoDataHex: null, MemoFormatHex: null));

            return Task.FromResult(new SubmitOutcome("tesSUCCESS", hash));
        }

        if (transaction.TryGetValue("TicketSequence", out object? ticketField))
        {
            uint ticket = Convert.ToUInt32(ticketField);

            // A node refuses a ticket it does not hold, which is what terPRE_TICKET says.
            if (!LiveTickets.Remove(ticket)) return Task.FromResult(new SubmitOutcome("terPRE_TICKET", hash));
        }
```

- [ ] **Step 2: Write the failing tests**

`tests/XrplFile.Core.Tests/TicketStrategyTests.cs`:
```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class TicketStrategyTests
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();

    private static IReadOnlyList<MemoSubmission> Memos(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex([(byte)i]), null))];

    private static TicketStrategy Strategy(FakeLedgerSink sink, FakeLedgerSource ledger, int ticketsPerBatch = 100) =>
        new(sink, ledger, Wallet, new TicketStrategyOptions
        {
            TicketsPerBatch = ticketsPerBatch,
            SweepPoll = TimeSpan.FromMilliseconds(1),
            BatchTimeout = TimeSpan.FromSeconds(5),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

    [Fact]
    public async Task Every_memo_lands_and_reports_the_hash_that_carried_it()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(5), null, TestContext.Current.CancellationToken);

        Assert.Equal([0, 1, 2, 3, 4], landed.Select(memo => memo.Index).Order().ToArray());
        foreach (SubmittedMemo memo in landed)
        {
            LedgerTransaction? tx = await ledger.TransactionAsync(memo.TxHash, TestContext.Current.CancellationToken);
            Assert.NotNull(tx);
            Assert.Equal(MemoLayout.ChunkMarkerHex(memo.Index), tx!.MemoTypeHex);
        }
    }

    [Fact]
    public async Task A_mint_comes_first_and_the_memos_ride_the_tickets_it_made()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);

        Assert.Equal("mint", sink.Events[0]);
        Assert.Equal("TicketCreate", sink.Submitted[0]["TransactionType"]);
        Assert.Equal(3u, Convert.ToUInt32(sink.Submitted[0]["TicketCount"]));

        // The mint sat at sequence 10, so the tickets are 11, 12 and 13 and nothing rides a sequence.
        uint[] ridden = [.. sink.Submitted.Skip(1).Select(tx => Convert.ToUInt32(tx["TicketSequence"])).Order()];
        Assert.Equal(new uint[] { 11, 12, 13 }, ridden);
        Assert.All(sink.Submitted.Skip(1), tx => Assert.Equal(0u, Convert.ToUInt32(tx["Sequence"])));
    }

    [Fact]
    public async Task A_batch_never_mints_more_than_it_needs()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 100);

        await strategy.SubmitAllAsync(Memos(4), null, TestContext.Current.CancellationToken);

        // Four memos, four tickets. A leftover ticket costs an owner reserve until it is spent.
        Assert.Equal(4u, Convert.ToUInt32(sink.Submitted[0]["TicketCount"]));
        Assert.Empty(sink.LiveTickets);
    }

    [Fact]
    public async Task More_memos_than_a_batch_holds_are_minted_for_again()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 2);

        await strategy.SubmitAllAsync(Memos(5), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, sink.Events.Count(kind => kind == "mint"));
    }

    [Fact]
    public async Task A_batch_is_never_larger_than_the_protocol_allows()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = new(sink, ledger, Wallet, new TicketStrategyOptions
        {
            TicketsPerBatch = 5000,
            SweepPoll = TimeSpan.FromMilliseconds(1),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

        await strategy.SubmitAllAsync(Memos(300), null, TestContext.Current.CancellationToken);

        Assert.All(
            sink.Submitted.Where(tx => (string)tx["TransactionType"] == "TicketCreate"),
            tx => Assert.InRange(Convert.ToUInt32(tx["TicketCount"]), 1u, (uint)TicketStrategy.MaxTicketsPerMint));
    }

    [Fact]
    public async Task A_chunk_that_landed_with_a_tec_is_retried_on_a_fresh_ticket()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Position 2 is the second memo: position 0 is the mint, position 1 the first memo.
        sink.FailsAs.Add(2);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 3);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, landed.Count);
        // A second mint had to happen: the failed transaction ate its ticket without writing.
        Assert.Equal(2, sink.Events.Count(kind => kind == "mint"));
    }

    [Fact]
    public async Task A_malformed_submission_hands_its_ticket_back_rather_than_wasting_it()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // A tef never reaches a ledger, so the ticket it named is still live and still ours.
        sink.Refusals[1] = "tefBAD_AUTH";
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 3);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, landed.Count);
        // Three tickets covered three memos and a retry: no second mint was needed.
        Assert.Equal(1, sink.Events.Count(kind => kind == "mint"));
    }

    [Fact]
    public async Task A_queued_transaction_is_waited_for_rather_than_sent_twice()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // The node answers terQUEUED and the transaction appears in the ledger a sweep later.
        sink.QueuedUntilSweep[1] = 1;
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 3);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, landed.Count);
        // Three memos, three transactions. A queued one re-sent would make four.
        Assert.Equal(3, sink.Submitted.Count(tx => tx.ContainsKey("Memos")));
    }

    [Fact]
    public async Task A_memo_that_never_lands_is_reported_rather_than_retried_forever()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Every attempt at this memo lands with a tec, so no attempt ever writes it.
        FakeLedgerSink.FailEvery failing = new(marker: MemoLayout.ChunkMarkerHex(1));
        sink.FailMarkers.Add(failing.Marker);
        await using TicketStrategy strategy = new(sink, ledger, Wallet, new TicketStrategyOptions
        {
            TicketsPerBatch = 3,
            MaxAttemptsPerMemo = 2,
            SweepPoll = TimeSpan.FromMilliseconds(1),
            BatchTimeout = TimeSpan.FromSeconds(2),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Contains(MemoLayout.ChunkMarkerHex(1), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unspent_tickets_survive_into_the_next_call()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 10);

        await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);
        int mintsAfterFirst = sink.Events.Count(kind => kind == "mint");

        // A second phase — the block list, then the manifest — must not mint again while the
        // first phase's tickets are still live.
        await strategy.SubmitAllAsync(Memos(2), null, TestContext.Current.CancellationToken);

        Assert.Equal(mintsAfterFirst, sink.Events.Count(kind => kind == "mint"));
    }

    [Fact]
    public async Task Progress_counts_memos_that_landed_not_memos_that_were_sent()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        List<int> seen = [];
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 4);

        await strategy.SubmitAllAsync(Memos(4), new Progress<int>(seen.Add), TestContext.Current.CancellationToken);

        Assert.Equal(4, seen.Max());
    }
}
```

The last two tests need two more hooks on the fake sink. Add them next to `FailsAs`:
```csharp
    /// <summary>Markers whose every submission validates as failed, however often it is retried.</summary>
    public HashSet<string> FailMarkers { get; } = [];

    /// <summary>Names a marker that always fails; the type exists so a test reads as a sentence.</summary>
    public sealed record FailEvery(string Marker);
```
and in `SubmitAsync`, where a stored transaction's success is decided, treat a memo whose `MemoType` is in `FailMarkers` exactly as `FailsAs` treats a position.

- [ ] **Step 3: Build to see them fail**

Run: `dotnet build XrplFile.sln`
Expected: compile errors naming `TicketStrategy` and `TicketStrategyOptions`.

- [ ] **Step 4: Write the ticket strategy**

`src/XrplFile.Core/TicketStrategy.cs`:
```csharp
using Xrpl.Wallet;

namespace XrplFile;

public sealed class TicketStrategyOptions
{
    public int FeeDrops { get; init; } = 12;

    /// <summary>
    /// Tickets minted at a time, and so the most transactions in flight at once. Every live
    /// ticket locks an owner reserve until it is spent, so this is money as much as speed. It is
    /// capped at what the protocol allows, and it doubles as the open-ledger guard: a node was
    /// measured refusing one account beyond about a thousand queued transactions.
    /// </summary>
    public int TicketsPerBatch { get; init; } = 100;

    /// <summary>How many submissions are in the air at once. The node answers each one.</summary>
    public int MaxInFlight { get; init; } = 25;

    /// <summary>How long a batch may take to appear in the ledger before its stragglers are retried.</summary>
    public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan SweepPoll { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan ValidationPoll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often one memo may be attempted before the publication is called off.</summary>
    public int MaxAttemptsPerMemo { get; init; } = 3;
}

/// <summary>
/// Many transactions at once, each on its own ticket.
///
/// A ticket frees a transaction from the account's sequence, so they no longer have to be sent
/// one after another. The price is a lifecycle: tickets are minted in batches, each live one
/// locks an owner reserve, and a transaction that reaches the ledger with a tec code has spent
/// its ticket without writing anything, so its retry needs a new one.
///
/// The batch loop is mint, submit, reconcile, carry over. Reconciliation is a sweep of
/// account_tx for the batch's markers, and it exists because the usual way of waiting for
/// validation cannot work here: a ticketed transaction does not move the account's sequence, so
/// there is nothing to watch. The sweep answers the better question anyway — not "has time
/// passed" but "which of these actually landed".
/// </summary>
public sealed class TicketStrategy(
    ILedgerSink sink, ILedgerSource source, XrplWallet wallet, TicketStrategyOptions? options = null) : ISubmissionStrategy
{
    /// <summary>rippled's kMaxValidCount: the most tickets one TicketCreate may mint.</summary>
    public const int MaxTicketsPerMint = 250;

    /// <summary>rippled's kMaxTicketThreshold: the most an account may hold at once.</summary>
    public const int MaxLiveTickets = 250;

    private readonly ILedgerSink _sink = sink;
    private readonly ILedgerSource _source = source;
    private readonly XrplWallet _wallet = wallet;
    private readonly TicketStrategyOptions _options = options ?? new TicketStrategyOptions();
    private readonly Queue<uint> _tickets = new();

    /// <summary>The earliest ledger any of this strategy's transactions could be in.</summary>
    private long _sweepFrom;

    public async Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
    {
        Dictionary<int, string> landed = [];
        Dictionary<int, int> attempts = [];
        List<int> pending = [.. Enumerable.Range(0, memos.Count)];

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int take = Math.Min(pending.Count, _options.TicketsPerBatch);
            await EnsureTicketsAsync(take, cancellationToken).ConfigureAwait(false);

            List<int> batch = [.. pending.Take(Math.Min(take, _tickets.Count))];
            Dictionary<string, int> wanted = batch.ToDictionary(index => memos[index].MemoTypeHex, index => index);

            foreach (int index in batch)
            {
                attempts[index] = attempts.GetValueOrDefault(index) + 1;
            }

            await SendBatchAsync(memos, batch, cancellationToken).ConfigureAwait(false);

            IReadOnlyDictionary<string, string> seen = await SweepAsync(wanted.Keys, cancellationToken).ConfigureAwait(false);

            foreach ((string marker, string hash) in seen)
            {
                landed[wanted[marker]] = hash;
            }

            submitted?.Report(landed.Count);

            pending = [.. pending.Where(index => !landed.ContainsKey(index))];

            if (pending.FirstOrDefault(index => attempts.GetValueOrDefault(index) >= _options.MaxAttemptsPerMemo, -1) is int stuck and >= 0)
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"the memo marked {memos[stuck].MemoTypeHex} did not reach the ledger in {_options.MaxAttemptsPerMemo} attempts");
            }
        }

        return [.. landed.OrderBy(pair => pair.Key).Select(pair => new SubmittedMemo(pair.Key, pair.Value))];
    }

    public ValueTask DisposeAsync()
    {
        // Unspent tickets are deliberately left alone. They cost an owner reserve each and there
        // is no transaction that cancels one, so the only ways out are spending them on the next
        // publication or deleting the account, which removes them with it.
        _tickets.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>Mints until at least this many tickets are in hand, never past what the protocol allows.</summary>
    private async Task EnsureTicketsAsync(int need, CancellationToken cancellationToken)
    {
        while (_tickets.Count < need)
        {
            int room = Math.Min(MaxLiveTickets - _tickets.Count, MaxTicketsPerMint);
            int mint = Math.Min(need - _tickets.Count, room);

            if (mint <= 0) return;

            string account = _wallet.ClassicAddress;
            uint sequence = await _sink.SequenceAsync(account, validated: false, cancellationToken).ConfigureAwait(false);

            Dictionary<string, object> transaction = new()
            {
                ["TransactionType"] = "TicketCreate",
                ["Account"] = account,
                ["Sequence"] = sequence,
                ["TicketCount"] = (uint)mint,
                ["Fee"] = _options.FeeDrops.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SigningPubKey"] = _wallet.PublicKey,
            };

            SubmitOutcome outcome = await _sink.SubmitAsync(transaction, _wallet, cancellationToken).ConfigureAwait(false);

            if (outcome.EngineResult is not ("tesSUCCESS" or "terQUEUED"))
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"the node answered {outcome.EngineResult} to a request for {mint} tickets");
            }

            // The mint is the only transaction here that moves the sequence, so it is the only one
            // whose validation can be awaited by watching it. Everything after rides a ticket and
            // leaves the sequence alone.
            await WaitForSequenceAsync(account, sequence + (uint)mint + 1, cancellationToken).ConfigureAwait(false);

            // Exactly rippled's arithmetic: a TicketCreate at S with count N mints S+1 through S+N.
            for (uint ticket = sequence + 1; ticket <= sequence + (uint)mint; ticket++)
            {
                _tickets.Enqueue(ticket);
            }

            if (_sweepFrom == 0)
            {
                LedgerTransaction? mintTx = await _source.TransactionAsync(outcome.TxHash, cancellationToken).ConfigureAwait(false);
                _sweepFrom = mintTx?.LedgerIndex ?? await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SendBatchAsync(IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> batch, CancellationToken cancellationToken)
    {
        using SemaphoreSlim gate = new(_options.MaxInFlight, _options.MaxInFlight);

        IEnumerable<Task> sending = batch.Select(async index =>
        {
            uint ticket;
            lock (_tickets)
            {
                if (!_tickets.TryDequeue(out ticket)) return;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await SendOneAsync(memos[index], ticket, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(sending).ConfigureAwait(false);
    }

    private async Task SendOneAsync(MemoSubmission memo, uint ticket, CancellationToken cancellationToken)
    {
        Dictionary<string, object> fields = new() { ["MemoType"] = memo.MemoTypeHex, ["MemoData"] = memo.MemoDataHex };
        if (memo.MemoFormatHex is not null) fields["MemoFormat"] = memo.MemoFormatHex;

        Dictionary<string, object> transaction = new()
        {
            ["TransactionType"] = "AccountSet",
            ["Account"] = _wallet.ClassicAddress,
            ["Sequence"] = 0u,
            ["TicketSequence"] = ticket,
            ["Fee"] = _options.FeeDrops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SigningPubKey"] = _wallet.PublicKey,
            ["Memos"] = new List<object> { new Dictionary<string, object> { ["Memo"] = fields } },
        };

        SubmitOutcome outcome = await _sink.SubmitAsync(transaction, _wallet, cancellationToken).ConfigureAwait(false);

        // terPRE_TICKET means the ticket is not in a ledger yet, which can only happen if the mint
        // was not awaited. That is a bug here, not a network condition, and it is worth saying so.
        if (string.Equals(outcome.EngineResult, "terPRE_TICKET", StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"ticket {ticket} was spent before it was in a validated ledger; the mint was not awaited");
        }

        // A tem or a tef never reached a ledger, so the ticket is untouched and still ours. Giving
        // it back is the difference between one wasted owner reserve per malformed submission and
        // none. A tec is the opposite case and the ticket really is gone, but a tec is not visible
        // here: it arrives when the ledger closes, and only the sweep sees it.
        if (outcome.EngineResult.StartsWith("tem", StringComparison.Ordinal)
            || outcome.EngineResult.StartsWith("tef", StringComparison.Ordinal))
        {
            lock (_tickets) _tickets.Enqueue(ticket);
            return;
        }

        // Everything else is left to the sweep. A tesSUCCESS may still not make it and a terQUEUED
        // has spent nothing and may yet land, so neither is believed here. The sweep decides, and
        // a memo it does not find comes round again on a fresh ticket.
    }

    /// <summary>
    /// Reads the account forward from the batch's first possible ledger until every marker has
    /// been seen or the batch's deadline passes.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> SweepAsync(
        IReadOnlyCollection<string> markers, CancellationToken cancellationToken)
    {
        Dictionary<string, string> found = [];
        HashSet<string> wanted = [.. markers];
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.BatchTimeout;

        while (true)
        {
            long to = await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

            await foreach (LedgerTransaction tx in _source
                .AccountTransactionsAsync(_wallet.ClassicAddress, _sweepFrom, to, forward: true, cancellationToken)
                .ConfigureAwait(false))
            {
                if (!tx.Succeeded || !tx.Validated || tx.MemoTypeHex is null) continue;
                if (!wanted.Contains(tx.MemoTypeHex)) continue;

                // The first copy in canonical order is the one the format's readers will use, so
                // it is the one whose hash goes in the report.
                found.TryAdd(tx.MemoTypeHex, tx.Hash);
            }

            if (found.Count == wanted.Count) return found;
            if (DateTimeOffset.UtcNow >= deadline) return found;

            await Task.Delay(_options.SweepPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForSequenceAsync(string account, uint expected, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.ValidationTimeout;

        while (true)
        {
            uint validated;

            try
            {
                validated = await _sink.SequenceAsync(account, validated: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Xrpl.Client.Exceptions.TimeoutException)
            {
                validated = 0;
            }

            if (validated >= expected) return;

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new XrplFileException(XrplFileError.ValidationTimeout,
                    $"the mint for {account} did not validate within {_options.ValidationTimeout}");
            }

            await Task.Delay(_options.ValidationPoll, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 5: Run the strategy tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.TicketStrategyTests"`
Expected: all pass. If `Unspent_tickets_survive_into_the_next_call` fails, `_sweepFrom` is being reset between calls; it must not be.

- [ ] **Step 6: Write the standalone tests**

`tests/XrplFile.Core.Tests/TicketStrategyStandaloneTests.cs`:
```csharp
using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// What only a node can answer: whether tickets are minted where the arithmetic says, whether
/// parallel submission on them works, and what happens at the edges the protocol defines.
/// </summary>
[Trait("Category", "Integration")]
public class TicketStrategyStandaloneTests
{
    private const string GenesisSeed = "snoPBrXtMeMyMHUVTgbuqAfg1SUTb";

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    [Fact]
    public async Task Two_hundred_memos_land_on_tickets_and_read_back_by_marker()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 500_000_000, token);

            IReadOnlyList<MemoSubmission> memos =
                [.. Enumerable.Range(0, 200).Select(i => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex([(byte)(i % 251)]), null))];

            await using TicketStrategy strategy = new(sink, source, writer, new TicketStrategyOptions { TicketsPerBatch = 100 });

            IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(memos, null, token);

            Assert.Equal(200, landed.Count);
            Assert.Equal([.. Enumerable.Range(0, 200)], landed.Select(memo => memo.Index).Order().ToArray());

            foreach (SubmittedMemo memo in landed.Take(5))
            {
                LedgerTransaction? tx = await source.TransactionAsync(memo.TxHash, token);
                Assert.NotNull(tx);
                Assert.Equal(MemoLayout.ChunkMarkerHex(memo.Index), tx!.MemoTypeHex);
                Assert.True(tx.Succeeded);
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_ticket_spent_before_its_mint_validated_is_refused_by_the_node()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);

            // A ticket number nothing minted. This is the shape terPRE_TICKET exists for, and it
            // is why the strategy awaits its mint rather than assuming.
            uint sequence = await sink.SequenceAsync(writer.ClassicAddress, validated: false, token);

            Dictionary<string, object> onNothing = new()
            {
                ["TransactionType"] = "AccountSet",
                ["Account"] = writer.ClassicAddress,
                ["Sequence"] = 0u,
                ["TicketSequence"] = sequence + 5000,
                ["Fee"] = "12",
                ["SigningPubKey"] = writer.PublicKey,
                ["Memos"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["Memo"] = new Dictionary<string, object> { ["MemoType"] = MemoLayout.ChunkMarkerHex(0), ["MemoData"] = "00" },
                    },
                },
            };

            SubmitOutcome outcome = await sink.SubmitAsync(onNothing, writer, token);

            Assert.Equal("terPRE_TICKET", outcome.EngineResult);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
```

`StandaloneFixture` is the funding helper the existing `StandaloneTests` already carries as a private method. Extract it into `tests/XrplFile.Core.Tests/StandaloneFixture.cs` as an `internal static class` with `FundedAccountAsync(IXrplClient, XrplClientLedgerSink, long drops, CancellationToken)`, and have `StandaloneTests` call it too rather than keeping its own copy.

- [ ] **Step 7: Run the standalone tests**

```bash
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml up -d
XRPL_FILE_NODE_URL=ws://localhost:16006 dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration"
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml down
```
Expected: everything green, including Task 0's tests and the existing ones. Record the wall-clock time of the two-hundred-memo test in the report: it is the first real measurement of what tickets buy.

- [ ] **Step 8: Run the whole non-integration suite**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: green.

- [ ] **Step 9: Commit**

```bash
git add src/XrplFile.Core/TicketStrategy.cs tests/XrplFile.Core.Tests/TicketStrategyTests.cs tests/XrplFile.Core.Tests/TicketStrategyStandaloneTests.cs tests/XrplFile.Core.Tests/FakeLedgerSink.cs tests/XrplFile.Core.Tests/StandaloneFixture.cs tests/XrplFile.Core.Tests/StandaloneTests.cs
git commit -m "feat(core): submit on tickets, and reconcile against the ledger rather than the clock"
```

---
### Task 4: The estimate

Numbers a person is shown before they spend money, and the two refusals that come from them.

**Files:**
- Create: `src/XrplFile.Core/PublicationEstimate.cs`, `tests/XrplFile.Core.Tests/PublicationEstimateTests.cs`
- Modify: `src/XrplFile.Core/XrplFileException.cs`

**Interfaces:**
- Consumes: `ILedgerSource.FeesAsync/BalanceDropsAsync` (Task 1); `NetworkFees`; `FileTree.ChunkCount/BlockCount/EncodeEntries`; `MemoLayout.ChunkBytes`; `TicketStrategy.MaxTicketsPerMint` (Task 3).
- Produces:
  - `XrplFileError` gains `InsufficientBalance` and `BudgetExceeded`, appended after `NodeUnreachable`.
  - `public sealed record PublicationEstimate(long Chunks, int Blocks, int Lists, int Mints, long Transactions, long FeeDropsPerTransaction, long FeeDropsTotal, long PeakReserveDrops, long BaseReserveDrops, long RequiredDrops, long BalanceDrops)` with `bool BalanceCovers => BalanceDrops >= RequiredDrops`.
  - `public static class PublicationEstimator` with
    `Task<PublicationEstimate> EstimateAsync(ILedgerSource source, string account, long fileBytes, int ticketsPerBatch, CancellationToken cancellationToken)`
    and `void Require(PublicationEstimate estimate, long? budgetDrops)`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/PublicationEstimateTests.cs`:
```csharp
using Xunit;

namespace XrplFile.Tests;

public class PublicationEstimateTests
{
    private static FakeLedgerSource Node(long balanceDrops, long fee = 10, long baseReserve = 1_000_000, long ownerReserve = 200_000)
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(fee, baseReserve, ownerReserve) };
        source.Balances["rWriter"] = balanceDrops;
        return source;
    }

    [Fact]
    public async Task A_one_block_file_needs_no_list_and_one_manifest()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(3, estimate.Chunks);
        Assert.Equal(1, estimate.Blocks);
        Assert.Equal(0, estimate.Lists);
        // Three chunks, no list, one manifest, and one mint to carry the four.
        Assert.Equal(1, estimate.Mints);
        Assert.Equal(5, estimate.Transactions);
    }

    [Fact]
    public async Task A_multi_block_file_pays_for_its_block_list_too()
    {
        // 1024001 bytes is 1025 chunks, so two blocks and one list memo.
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(500_000_000), "rWriter", fileBytes: 1_024_001, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(1025, estimate.Chunks);
        Assert.Equal(2, estimate.Blocks);
        Assert.Equal(1, estimate.Lists);
        // 1027 memos over batches of a hundred is eleven mints.
        Assert.Equal(11, estimate.Mints);
        Assert.Equal(1038, estimate.Transactions);
    }

    [Fact]
    public async Task The_fee_total_is_the_nodes_price_times_the_transactions()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000, fee: 15), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(15, estimate.FeeDropsPerTransaction);
        Assert.Equal(75, estimate.FeeDropsTotal);
    }

    [Fact]
    public async Task The_peak_reserve_is_a_batch_of_tickets_and_the_account_itself()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        // Four memos, so four tickets are ever live at once, not the batch's hundred.
        Assert.Equal(4 * 200_000, estimate.PeakReserveDrops);
        Assert.Equal(1_000_000, estimate.BaseReserveDrops);
        Assert.Equal(estimate.FeeDropsTotal + estimate.PeakReserveDrops + estimate.BaseReserveDrops, estimate.RequiredDrops);
    }

    [Fact]
    public async Task A_batch_smaller_than_the_file_caps_the_peak_reserve()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(500_000_000), "rWriter", fileBytes: 1_024_001, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        // A thousand memos, but only a hundred tickets are ever live together.
        Assert.Equal(100 * 200_000, estimate.PeakReserveDrops);
    }

    [Fact]
    public async Task A_balance_that_covers_the_run_says_so()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.True(estimate.BalanceCovers);
        PublicationEstimator.Require(estimate, budgetDrops: null);
    }

    [Fact]
    public async Task A_balance_that_does_not_cover_the_run_is_refused_with_both_numbers()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(1_500_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.False(estimate.BalanceCovers);

        XrplFileException error = Assert.Throws<XrplFileException>(() => PublicationEstimator.Require(estimate, budgetDrops: null));

        Assert.Equal(XrplFileError.InsufficientBalance, error.Error);
        Assert.Contains(estimate.RequiredDrops.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains("1500000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_above_the_callers_ceiling_is_refused_before_the_balance_is_blamed()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        XrplFileException error = Assert.Throws<XrplFileException>(() => PublicationEstimator.Require(estimate, budgetDrops: 100));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
    }

    [Fact]
    public async Task An_empty_file_has_nothing_to_estimate()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 0, ticketsPerBatch: 100, TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Build to see them fail**

Run: `dotnet build XrplFile.sln`
Expected: compile errors naming `PublicationEstimate`, `PublicationEstimator`, `XrplFileError.InsufficientBalance`.

- [ ] **Step 3: Write the implementation**

Append to `XrplFileError`, after `NodeUnreachable`:
```csharp
    InsufficientBalance,
    BudgetExceeded,
```

`src/XrplFile.Core/PublicationEstimate.cs`:
```csharp
namespace XrplFile;

/// <summary>
/// What a publication will cost, in the numbers a person is shown before they agree to it.
///
/// It is a set of figures rather than a verdict on purpose. "You cannot afford this" is not
/// useful; "this needs 2.4 XRP and you hold 1.9" is.
/// </summary>
/// <param name="Chunks">Memos carrying file bytes.</param>
/// <param name="Blocks">Merkle blocks the chunks group into.</param>
/// <param name="Lists">Memos carrying the block list; zero when there is one block.</param>
/// <param name="Mints">TicketCreate transactions the batches will need.</param>
/// <param name="Transactions">Everything above plus the manifest.</param>
/// <param name="FeeDropsPerTransaction">The node's price at the moment of the estimate.</param>
/// <param name="FeeDropsTotal">That price across every transaction.</param>
/// <param name="PeakReserveDrops">What live tickets lock up at the worst moment.</param>
/// <param name="BaseReserveDrops">What the account must hold to exist at all.</param>
/// <param name="RequiredDrops">The three above, added.</param>
/// <param name="BalanceDrops">What the account holds now.</param>
public sealed record PublicationEstimate(
    long Chunks,
    int Blocks,
    int Lists,
    int Mints,
    long Transactions,
    long FeeDropsPerTransaction,
    long FeeDropsTotal,
    long PeakReserveDrops,
    long BaseReserveDrops,
    long RequiredDrops,
    long BalanceDrops)
{
    public bool BalanceCovers => BalanceDrops >= RequiredDrops;
}

public static class PublicationEstimator
{
    public static async Task<PublicationEstimate> EstimateAsync(
        ILedgerSource source, string account, long fileBytes, int ticketsPerBatch, CancellationToken cancellationToken)
    {
        if (fileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileBytes), fileBytes, "an empty file has no chunks and no root");
        }

        if (ticketsPerBatch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticketsPerBatch), ticketsPerBatch, "a batch holds at least one ticket");
        }

        long chunks = FileTree.ChunkCount(fileBytes, MemoLayout.ChunkBytes);
        int blocks = FileTree.BlockCount(chunks);
        int lists = blocks > 1 ? BlockListMemos(blocks) : 0;

        long memos = chunks + lists + 1;
        int batch = Math.Min(ticketsPerBatch, TicketStrategy.MaxTicketsPerMint);
        int mints = (int)((memos + batch - 1) / batch);
        long transactions = memos + mints;

        NetworkFees fees = await source.FeesAsync(cancellationToken).ConfigureAwait(false);
        long balance = await source.BalanceDropsAsync(account, cancellationToken).ConfigureAwait(false);

        long feeTotal = transactions * fees.OpenLedgerFeeDrops;

        // Only a batch's worth of tickets is ever live at once, and a file smaller than a batch
        // never mints a full one. Charging the whole file's worth would refuse runs that fit.
        long peakReserve = Math.Min(memos, batch) * fees.OwnerReserveDrops;
        long required = feeTotal + peakReserve + fees.BaseReserveDrops;

        return new PublicationEstimate(
            chunks, blocks, lists, mints, transactions,
            fees.OpenLedgerFeeDrops, feeTotal, peakReserve, fees.BaseReserveDrops, required, balance);
    }

    /// <summary>
    /// Refuses a run the caller's ceiling forbids, then one the account cannot carry. The ceiling
    /// comes first because it is the caller's own decision and needs no excuse from the network.
    /// </summary>
    public static void Require(PublicationEstimate estimate, long? budgetDrops)
    {
        if (budgetDrops is { } ceiling && estimate.RequiredDrops > ceiling)
        {
            throw new XrplFileException(XrplFileError.BudgetExceeded,
                $"the run needs {estimate.RequiredDrops} drops and the budget allows {ceiling}");
        }

        if (!estimate.BalanceCovers)
        {
            throw new XrplFileException(XrplFileError.InsufficientBalance,
                $"the run needs {estimate.RequiredDrops} drops and the account holds {estimate.BalanceDrops}");
        }
    }

    /// <summary>
    /// How many memos the block list takes. Counted by packing real entries rather than by
    /// arithmetic, because the packer is greedy and its own code is the only honest answer.
    /// </summary>
    private static int BlockListMemos(int blocks)
    {
        string hash = new('0', 64);
        List<BlockEntry> entries = [.. Enumerable.Range(0, blocks).Select(i => new BlockEntry(i, hash, 999_999_999))];

        return FileTree.EncodeEntries(entries).Count;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.PublicationEstimateTests"`
Expected: all pass. If the mint count is off by one, check the ceiling division: memos over batch, rounded up.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core/PublicationEstimate.cs src/XrplFile.Core/XrplFileException.cs tests/XrplFile.Core.Tests/PublicationEstimateTests.cs
git commit -m "feat(core): what a publication costs, before any of it is spent"
```

---

### Task 5: The session, its state and its report

**Files:**
- Create: `src/XrplFile.Core/PublicationState.cs`, `src/XrplFile.Core/PublicationReport.cs`, `src/XrplFile.Core/PublicationSession.cs`, `tests/XrplFile.Core.Tests/PublicationSessionTests.cs`

**Interfaces:**
- Consumes: `PublicationEstimator` (Task 4); `ISubmissionStrategy` (Task 2); `XrplFileWriter(ISubmissionStrategy, ILedgerSource, WriterOptions?)` and its `PublishAsync(ReadOnlyMemory<byte>, string type, string? name, string writer, string network, IProgress<PublishProgress>?, CancellationToken)` (Task 2); `ILedgerSource.CurrentLedgerAsync` (Task 1); `PublishResult`, `PublishProgress`.
- Produces:
  - `public sealed record PublicationState(int Version, string Network, string Writer, string KeyReference, string Type, string? Name, long Size, int Chunk, string? Root, long StartedInLedger, string Phase, IReadOnlyDictionary<int, string> ChunkHashes)` with `Save`/`Load` to JSON.
  - `public sealed record PublicationReport(XrplFileLink Link, Manifest Manifest, string Writer, long Transactions, long FeeDropsSpent, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, IReadOnlyList<string> ChunkHashes, IReadOnlyList<string> ListHashes, string ManifestHash)`.
  - `public sealed class SessionOptions { string StateDirectory; long? BudgetDrops; int TicketsPerBatch = 100; bool ReadBack = true; }`
  - `public sealed class PublicationSession` with
    `static Task<PublicationEstimate> EstimateAsync(ILedgerSource, string account, long fileBytes, SessionOptions, CancellationToken)`,
    `static Task<PublicationSession> StartAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory<byte> file, string type, string? name, string writer, string keyReference, string network, SessionOptions, CancellationToken)`,
    `Task<PublicationReport> RunAsync(IProgress<PublishProgress>?, CancellationToken)`,
    `string StatePath { get; }`,
    `static IReadOnlyList<PublicationState> Unfinished(string stateDirectory)`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/PublicationSessionTests.cs`:
```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class PublicationSessionTests : IDisposable
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();

    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "xrpl-file-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private SessionOptions Options(long? budget = null) =>
        new() { StateDirectory = _stateDirectory, BudgetDrops = budget, TicketsPerBatch = 100 };

    private static FakeLedgerSource Node(long balanceDrops = 500_000_000)
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(10, 1_000_000, 200_000) };
        source.Balances[Wallet.ClassicAddress] = balanceDrops;
        return source;
    }

    [Fact]
    public async Task A_published_file_comes_back_with_a_link_and_a_report()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("testnet", report.Link.Network);
        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Equal(3, report.ChunkHashes.Count);
        Assert.Empty(report.ListHashes);
        Assert.Equal(4, report.Transactions);
        Assert.True(report.FinishedAt >= report.StartedAt);
    }

    [Fact]
    public async Task A_run_that_costs_more_than_the_budget_never_starts()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(budget: 100), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task An_account_that_cannot_carry_the_run_never_starts_it()
    {
        FakeLedgerSource ledger = Node(balanceDrops: 1_000);
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.InsufficientBalance, error.Error);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task The_state_file_exists_before_the_first_transaction_and_names_the_key_not_the_seed()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(session.StatePath));
        string json = await File.ReadAllTextAsync(session.StatePath, TestContext.Current.CancellationToken);

        Assert.Contains("key-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Wallet.Seed, json, StringComparison.Ordinal);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task The_state_records_the_phase_as_the_run_moves_through_it()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        await session.RunAsync(null, TestContext.Current.CancellationToken);

        PublicationState state = PublicationState.Load(session.StatePath);
        Assert.Equal("done", state.Phase);
        Assert.NotNull(state.Root);
    }

    [Fact]
    public async Task An_unfinished_run_is_findable_by_its_directory()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        IReadOnlyList<PublicationState> unfinished = PublicationSession.Unfinished(_stateDirectory);

        PublicationState only = Assert.Single(unfinished);
        Assert.Equal("key-1", only.KeyReference);
        Assert.NotEqual("done", only.Phase);
    }

    [Fact]
    public async Task A_finished_run_is_no_longer_unfinished()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Empty(PublicationSession.Unfinished(_stateDirectory));
    }

    [Fact]
    public async Task The_estimate_can_be_asked_for_without_starting_anything()
    {
        FakeLedgerSource ledger = Node();

        PublicationEstimate estimate = await PublicationSession.EstimateAsync(
            ledger, Wallet.ClassicAddress, fileBytes: 2500, Options(), TestContext.Current.CancellationToken);

        Assert.Equal(3, estimate.Chunks);
        Assert.False(Directory.Exists(_stateDirectory));
    }
}
```

- [ ] **Step 2: Build to see them fail**

Run: `dotnet build XrplFile.sln`
Expected: compile errors naming `PublicationSession`, `PublicationState`, `PublicationReport`, `SessionOptions`.

- [ ] **Step 3: Write the state and the report**

`src/XrplFile.Core/PublicationState.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XrplFile;

/// <summary>
/// What a publication needs to survive the process dying.
///
/// It holds no seed. The key it names is a reference the caller understands — a row in a key
/// store, a file name, whatever the application uses — and resolving it back to a signer is
/// somebody else's job. That keeps the whole secret surface in one place.
/// </summary>
/// <param name="StartedInLedger">
/// Where reconciliation begins on a resume. Without it a resumed run would sweep the account's
/// entire history rather than the few hundred ledgers it wrote in.
/// </param>
/// <param name="Phase">One of "chunks", "lists", "manifest", "done".</param>
/// <param name="ChunkHashes">
/// What has landed so far, by index. A resume does not trust this — the ledger decides — but it
/// carries the run's report through an interruption.
/// </param>
public sealed record PublicationState(
    int Version,
    string Network,
    string Writer,
    string KeyReference,
    string Type,
    string? Name,
    long Size,
    int Chunk,
    string? Root,
    long StartedInLedger,
    string Phase,
    IReadOnlyDictionary<int, string> ChunkHashes)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Written beside and moved into place: a crash during the write must not leave a state
        // file that parses as something it is not.
        string temporary = path + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options) + "\n");
        File.Move(temporary, path, overwrite: true);
    }

    public static PublicationState Load(string path)
    {
        PublicationState? state = JsonSerializer.Deserialize<PublicationState>(File.ReadAllText(path), Options);

        if (state is null) throw new FormatException($"{path} did not deserialize as a publication state");
        if (state.Version != CurrentVersion) throw new FormatException($"state version {state.Version} is not one this library reads");

        return state;
    }
}
```

`src/XrplFile.Core/PublicationReport.cs`:
```csharp
namespace XrplFile;

/// <summary>
/// What a finished run produced, in the shape the war-and-peace uploader wrote: enough to show a
/// person what happened, and enough to check it against the ledger afterwards.
/// </summary>
public sealed record PublicationReport(
    XrplFileLink Link,
    Manifest Manifest,
    string Writer,
    long Transactions,
    long FeeDropsSpent,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<string> ChunkHashes,
    IReadOnlyList<string> ListHashes,
    string ManifestHash)
{
    public TimeSpan Took => FinishedAt - StartedAt;
}
```

- [ ] **Step 4: Write the session**

`src/XrplFile.Core/PublicationSession.cs`:
```csharp
namespace XrplFile;

public sealed class SessionOptions
{
    /// <summary>Where state files live. One file per run, named after the run.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>The most the run may cost, or null for no ceiling of the caller's own.</summary>
    public long? BudgetDrops { get; init; }

    public int TicketsPerBatch { get; init; } = 100;

    public bool ReadBack { get; init; } = true;
}

/// <summary>
/// A publication as something that can be estimated, started, interrupted and found again.
///
/// The writer knows the format's order and the strategy knows how a transaction reaches the
/// ledger. What is left is everything about money and time: what this will cost before it costs
/// it, whether the account can carry it, what survives the process dying, and what to show
/// afterwards. That is this class, and it is the one an application holds.
/// </summary>
public sealed class PublicationSession
{
    private readonly ISubmissionStrategy _strategy;
    private readonly ILedgerSource _source;
    private readonly ReadOnlyMemory<byte> _file;
    private readonly SessionOptions _options;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private PublicationState _state;

    private PublicationSession(
        ISubmissionStrategy strategy, ILedgerSource source, ReadOnlyMemory<byte> file,
        PublicationState state, string statePath, SessionOptions options)
    {
        _strategy = strategy;
        _source = source;
        _file = file;
        _state = state;
        _options = options;
        StatePath = statePath;
    }

    public string StatePath { get; }

    public PublicationState State => _state;

    /// <summary>What the run would cost, without creating anything.</summary>
    public static Task<PublicationEstimate> EstimateAsync(
        ILedgerSource source, string account, long fileBytes, SessionOptions options, CancellationToken cancellationToken) =>
        PublicationEstimator.EstimateAsync(source, account, fileBytes, options.TicketsPerBatch, cancellationToken);

    /// <summary>
    /// Estimates, refuses what cannot be afforded, and writes the state file — all before a single
    /// transaction is submitted. A run that is going to fail on money fails here, where it has
    /// cost nothing.
    /// </summary>
    public static async Task<PublicationSession> StartAsync(
        ISubmissionStrategy strategy, ILedgerSource source, ReadOnlyMemory<byte> file, string type, string? name,
        string writer, string keyReference, string network, SessionOptions options, CancellationToken cancellationToken)
    {
        PublicationEstimate estimate = await PublicationEstimator
            .EstimateAsync(source, writer, file.Length, options.TicketsPerBatch, cancellationToken).ConfigureAwait(false);

        PublicationEstimator.Require(estimate, options.BudgetDrops);

        long startedIn = await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        PublicationState state = new(
            PublicationState.CurrentVersion, network, writer, keyReference, type, name,
            file.Length, MemoLayout.ChunkBytes, Root: null, startedIn, Phase: "chunks",
            ChunkHashes: new Dictionary<int, string>());

        string path = Path.Combine(options.StateDirectory, $"{startedIn}-{writer}.json");
        state.Save(path);

        return new PublicationSession(strategy, source, file, state, path, options);
    }

    public async Task<PublicationReport> RunAsync(IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        XrplFileWriter writer = new(_strategy, _source, new WriterOptions { ReadBack = _options.ReadBack });

        PublishResult result = await writer.PublishAsync(
            _file, _state.Type, _state.Name, _state.Writer, _state.Network, progress, cancellationToken).ConfigureAwait(false);

        _state = _state with
        {
            Root = result.Manifest.Root,
            Phase = "done",
            ChunkHashes = result.ChunkHashes
                .Select((hash, index) => (hash, index))
                .ToDictionary(pair => pair.index, pair => pair.hash),
        };
        _state.Save(StatePath);

        NetworkFees fees = await _source.FeesAsync(cancellationToken).ConfigureAwait(false);
        long transactions = result.ChunkHashes.Count + result.ListHashes.Count + 1;

        return new PublicationReport(
            result.Link, result.Manifest, result.Writer,
            transactions, transactions * fees.OpenLedgerFeeDrops,
            _startedAt, DateTimeOffset.UtcNow,
            result.ChunkHashes, result.ListHashes, result.ManifestHash);
    }

    /// <summary>
    /// Runs that were started and never finished. Money may be sitting on the accounts they name,
    /// and their state files are the only record of it.
    /// </summary>
    public static IReadOnlyList<PublicationState> Unfinished(string stateDirectory)
    {
        if (!Directory.Exists(stateDirectory)) return [];

        List<PublicationState> unfinished = [];

        foreach (string path in Directory.EnumerateFiles(stateDirectory, "*.json"))
        {
            PublicationState state;

            try
            {
                state = PublicationState.Load(path);
            }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException)
            {
                // A file this library cannot read is not a run it can resume. Listing it as one
                // would put a broken row in front of a person with nothing they could do about it.
                continue;
            }

            if (!string.Equals(state.Phase, "done", StringComparison.Ordinal)) unfinished.Add(state);
        }

        return unfinished;
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.PublicationSessionTests"`
Expected: all pass. `JsonSerializerOptions.NewLine` needs .NET 9 or later; if it does not compile, drop it and normalise the string with `.Replace("\r\n", "\n")` before writing, so a state file is the same on every platform.

- [ ] **Step 6: Commit**

```bash
git add src/XrplFile.Core/PublicationState.cs src/XrplFile.Core/PublicationReport.cs src/XrplFile.Core/PublicationSession.cs tests/XrplFile.Core.Tests/PublicationSessionTests.cs
git commit -m "feat(core): a publication is a session with an estimate, a state file and a report"
```

---
### Task 6: Resumption

A crash can fall between a submission and its record, so a resumed run asks the ledger what is there rather than believing the file. This is the task where an interruption is a real one.

**Files:**
- Modify: `src/XrplFile.Core/PublicationSession.cs`, `src/XrplFile.Core/XrplFileWriter.cs`, `src/XrplFile.Core/XrplFileException.cs`
- Test: `tests/XrplFile.Core.Tests/PublicationSessionTests.cs`, create `tests/XrplFile.Core.Tests/PublicationSessionStandaloneTests.cs`

**Interfaces:**
- Consumes: `PublicationSession` and `PublicationState` (Task 5); `ILedgerSource.AccountTransactionsAsync/CurrentLedgerAsync`; `MemoLayout.ChunkIndexOf`; `FileTree.Split`.
- Produces:
  - `XrplFileError` gains `ResumeMismatch`, appended after `BudgetExceeded`.
  - `static Task<PublicationSession> ResumeAsync(ISubmissionStrategy strategy, ILedgerSource source, ReadOnlyMemory<byte> file, string statePath, SessionOptions options, CancellationToken cancellationToken)` on `PublicationSession`.
  - `XrplFileWriter.PublishAsync` gains a parameter `IReadOnlyDictionary<int, string>? alreadyLanded` before the progress argument, so a resumed run does not re-send what the ledger already holds.

- [ ] **Step 1: Write the failing tests**

Append to `tests/XrplFile.Core.Tests/PublicationSessionTests.cs`:
```csharp
    [Fact]
    public async Task A_resumed_run_does_not_re_send_the_chunks_the_ledger_already_holds()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("resume", 2500);

        // A first run that dies after its chunks: the strategy refuses everything past the third
        // submission, which is where the manifest would have gone.
        sink.Refusals[3] = "tefBAD_AUTH";
        await using (SequenceStrategy dying = new(sink, Wallet))
        {
            PublicationSession first = await PublicationSession.StartAsync(
                dying, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
                Options(), TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<XrplFileException>(() => first.RunAsync(null, TestContext.Current.CancellationToken));
        }

        int submittedByTheFirstRun = sink.Submitted.Count;
        sink.Refusals.Clear();

        // The state file is all the second run is given, besides the file itself.
        string statePath = Directory.EnumerateFiles(_stateDirectory, "*.json").Single();

        await using SequenceStrategy resumed = new(sink, Wallet);
        PublicationSession second = await PublicationSession.ResumeAsync(
            resumed, ledger, file, statePath, Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await second.RunAsync(null, TestContext.Current.CancellationToken);

        // Three chunks landed in the first run and are not sent again; only the manifest is.
        Assert.Equal(submittedByTheFirstRun + 1, sink.Submitted.Count);
        Assert.Equal(3, report.ChunkHashes.Count);
    }

    [Fact]
    public async Task A_resumed_run_reads_the_ledger_rather_than_believing_the_state_file()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("resume", 2500);

        await using SequenceStrategy strategy = new(sink, Wallet);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        // The chunks reach the ledger, and then the process dies before anything is written down:
        // the state file still says nothing has landed.
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
        await strategy.SubmitAllAsync(
            [.. chunks.Select((chunk, i) => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunk), null))],
            null, TestContext.Current.CancellationToken);

        int alreadyThere = sink.Submitted.Count;
        PublicationState onDisk = PublicationState.Load(first.StatePath);
        Assert.Empty(onDisk.ChunkHashes);

        await using SequenceStrategy resumed = new(sink, Wallet);
        PublicationSession second = await PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken);

        await second.RunAsync(null, TestContext.Current.CancellationToken);

        // Only the manifest was needed, because the sweep found the chunks the file did not know about.
        Assert.Equal(alreadyThere + 1, sink.Submitted.Count);
    }

    [Fact]
    public async Task A_resume_with_a_different_file_is_refused()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);

        await using SequenceStrategy strategy = new(sink, Wallet);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("one", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        await using SequenceStrategy resumed = new(sink, Wallet);
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            resumed, ledger, VectorStream.Bytes("another", 2500), first.StatePath, Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
    }

    [Fact]
    public async Task A_resume_of_a_finished_run_is_refused_rather_than_publishing_twice()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("s", 2500);

        await using SequenceStrategy strategy = new(sink, Wallet);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);
        await first.RunAsync(null, TestContext.Current.CancellationToken);

        await using SequenceStrategy resumed = new(sink, Wallet);
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
    }
```

- [ ] **Step 2: Build to see them fail**

Run: `dotnet build XrplFile.sln`
Expected: compile errors naming `ResumeAsync` and `XrplFileError.ResumeMismatch`.

- [ ] **Step 3: Write the implementation**

Append to `XrplFileError`, after `BudgetExceeded`:
```csharp
    ResumeMismatch,
```

Add to `PublicationSession`:
```csharp
    /// <summary>
    /// Picks a run back up from its state file.
    ///
    /// The file is where to look, not what to believe. A crash can fall between a submission and
    /// its record, so the ledger is read from the run's starting point and whatever is actually
    /// there is what counts. The file's own map of hashes carries the report through the
    /// interruption; it does not decide what still needs sending.
    /// </summary>
    public static async Task<PublicationSession> ResumeAsync(
        ISubmissionStrategy strategy, ILedgerSource source, ReadOnlyMemory<byte> file,
        string statePath, SessionOptions options, CancellationToken cancellationToken)
    {
        PublicationState state = PublicationState.Load(statePath);

        if (string.Equals(state.Phase, "done", StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} describes a run that finished; publishing it again would put a second copy in the ledger");
        }

        if (state.Size != file.Length)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} was started for {state.Size} bytes and this file is {file.Length}");
        }

        // The size matching is not enough: two different files of one size are ordinary. The root
        // is the identity, so it is what the check is made of.
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, state.Chunk);
        string root = RootOf(chunks, state.StartedInLedger);

        if (state.Root is { } recorded && !string.Equals(recorded, root, StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} was started for a file rooted at {recorded} and this one roots at {root}");
        }

        PublicationSession session = new(strategy, source, file, state, statePath, options);
        session._landed = await session.ReconcileAsync(chunks.Count, cancellationToken).ConfigureAwait(false);

        return session;
    }

    /// <summary>
    /// Which chunk indices are already in the ledger, read from the ledger itself.
    ///
    /// The sweep starts at the ledger the run started in, which is why that number is in the
    /// state file: without it this would read the account's whole history.
    /// </summary>
    private async Task<Dictionary<int, string>> ReconcileAsync(int chunkCount, CancellationToken cancellationToken)
    {
        Dictionary<int, string> landed = [];
        long to = await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        await foreach (LedgerTransaction tx in _source
            .AccountTransactionsAsync(_state.Writer, _state.StartedInLedger, to, forward: true, cancellationToken)
            .ConfigureAwait(false))
        {
            if (!tx.Succeeded || !tx.Validated) continue;
            if (MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is not { } index) continue;
            if (index < 0 || index >= chunkCount) continue;

            // First in canonical order, exactly as a reader will resolve it.
            landed.TryAdd((int)index, tx.Hash);
        }

        return landed;
    }

    /// <summary>The file's root, computed the way the writer will compute it.</summary>
    private static string RootOf(IReadOnlyList<byte[]> chunks, long placeholderLedger)
    {
        int blocks = FileTree.BlockCount(chunks.Count);
        List<BlockEntry> entries = new(blocks);

        for (int block = 0; block < blocks; block++)
        {
            List<byte[]> members = [.. chunks.Skip(block * FileTree.BlockChunks).Take(FileTree.BlockChunks)];
            entries.Add(new BlockEntry(block, Convert.ToHexString(FileTree.BlockHash(members)), placeholderLedger));
        }

        // The root is over the block hashes alone; the ledger in an entry does not enter it.
        return FileTree.RootHex(entries);
    }
```

Add the field beside the others:
```csharp
    private Dictionary<int, string> _landed = [];
```

and pass it through in `RunAsync`:
```csharp
        PublishResult result = await writer.PublishAsync(
            _file, _state.Type, _state.Name, _state.Writer, _state.Network,
            _landed.Count > 0 ? _landed : null, progress, cancellationToken).ConfigureAwait(false);
```

In `XrplFileWriter.PublishAsync`, add the parameter and use it in the chunks phase only — a partial run never reaches the lists, because the writer does not start them until every chunk has landed:
```csharp
        IReadOnlyList<MemoSubmission> chunkMemos =
            [.. chunks.Select((chunk, i) => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunk), null))];

        // A resumed run is handed what the ledger already holds. Re-sending those would be paid
        // for twice and would put a second copy of each chunk in the account's history.
        List<MemoSubmission> missing = [];
        List<int> missingIndices = [];

        for (int i = 0; i < chunkMemos.Count; i++)
        {
            if (alreadyLanded is not null && alreadyLanded.ContainsKey(i)) continue;

            missing.Add(chunkMemos[i]);
            missingIndices.Add(i);
        }

        Dictionary<int, string> chunkByIndex = alreadyLanded is null ? [] : new Dictionary<int, string>(alreadyLanded);

        if (missing.Count > 0)
        {
            IReadOnlyList<SubmittedMemo> sent = await _strategy.SubmitAllAsync(
                missing,
                new Progress<int>(done => progress?.Report(new PublishProgress("chunks", chunkByIndex.Count + done, chunkMemos.Count))),
                cancellationToken).ConfigureAwait(false);

            foreach (SubmittedMemo memo in sent)
            {
                chunkByIndex[missingIndices[memo.Index]] = memo.TxHash;
            }
        }

        List<string> chunkHashes = [.. Enumerable.Range(0, chunkMemos.Count).Select(i => chunkByIndex[i])];
```

- [ ] **Step 4: Run the session tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.PublicationSessionTests"`
Expected: all pass, the four new ones included.

- [ ] **Step 5: Write the standalone resumption test**

`tests/XrplFile.Core.Tests/PublicationSessionStandaloneTests.cs`:
```csharp
using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// A whole publication on tickets against a node, interrupted for real and picked back up.
/// Nothing less proves that the state file and the reconciliation sweep work together.
/// </summary>
[Trait("Category", "Integration")]
public class PublicationSessionStandaloneTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "xrpl-file-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    [Fact]
    public async Task A_run_interrupted_after_its_chunks_is_finished_by_a_second_session()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 500_000_000, token);

            // 1024001 bytes: 1025 chunks over two blocks, so the list phase is exercised too.
            VectorFile vector = Vectors.File(1_024_001);
            byte[] file = VectorStream.Bytes(vector.Seed, vector.Size);

            SessionOptions options = new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };

            string statePath;

            // The first session publishes the chunks by hand and then stops, which is what a
            // process dying between the chunks and the manifest leaves behind.
            await using (TicketStrategy first = new(sink, source, writer, new TicketStrategyOptions { TicketsPerBatch = 100 }))
            {
                PublicationSession session = await PublicationSession.StartAsync(
                    first, source, file, "application/octet-stream", "resumed.bin",
                    writer.ClassicAddress, "key-standalone", "1", options, token);

                statePath = session.StatePath;

                IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
                await first.SubmitAllAsync(
                    [.. chunks.Select((chunk, i) => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunk), null))],
                    null, token);
            }

            // A second session, given only the state file and the bytes.
            await using TicketStrategy second = new(sink, source, writer, new TicketStrategyOptions { TicketsPerBatch = 100 });

            PublicationSession resumed = await PublicationSession.ResumeAsync(second, source, file, statePath, options, token);
            PublicationReport report = await resumed.RunAsync(null, token);

            Assert.Equal(vector.Root, report.Manifest.Root);

            // The proof is the read: a stranger's reader gets the file back byte for byte.
            ReadResult read = await new XrplFileReader(source).ReadAsync(report.Link, token);

            Assert.Equal(file, read.Bytes);
            Assert.Equal(writer.ClassicAddress, read.Writer);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
```

- [ ] **Step 6: Run the standalone tests**

```bash
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml up -d
XRPL_FILE_NODE_URL=ws://localhost:16006 dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration"
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml down
```
Expected: everything green. Note the wall-clock of the resumed publication in the report.

- [ ] **Step 7: Commit**

```bash
git add src/XrplFile.Core/PublicationSession.cs src/XrplFile.Core/XrplFileWriter.cs src/XrplFile.Core/XrplFileException.cs tests/XrplFile.Core.Tests/PublicationSessionTests.cs tests/XrplFile.Core.Tests/PublicationSessionStandaloneTests.cs
git commit -m "feat(core): a run picks up where it stopped, asking the ledger rather than the file"
```

---

### Task 7: The throwaway account

**Files:**
- Create: `src/XrplFile.Core/ThrowawayAccount.cs`, `tests/XrplFile.Core.Tests/ThrowawayAccountTests.cs`, `tests/XrplFile.Core.Tests/ThrowawayAccountStandaloneTests.cs`

**Interfaces:**
- Consumes: `ILedgerSource.BalanceDropsAsync/FeesAsync/CurrentLedgerAsync/AccountTransactionsAsync` (Task 1); `ILedgerSink.SequenceAsync/SubmitAsync`; `PublicationEstimate` (Task 4); `XrplWallet`.
- Produces:
  - `public sealed record FundingRequest(string Address, long RequiredDrops, long BalanceDrops)` with `bool Covered => BalanceDrops >= RequiredDrops`.
  - `public sealed record RefundResult(string Destination, long SentDrops, long LeftBehindDrops, string TxHash)`.
  - `public sealed class ThrowawayAccount(ILedgerSink sink, ILedgerSource source, XrplWallet wallet)` with
    `string Address`,
    `Task<FundingRequest> FundingAsync(PublicationEstimate estimate, CancellationToken)`,
    `Task<FundingRequest> AwaitFundingAsync(PublicationEstimate estimate, TimeSpan pollEvery, CancellationToken)`,
    `Task<string?> FunderAsync(long fromLedger, CancellationToken)`,
    `Task<RefundResult> RefundAsync(string destination, CancellationToken)`,
    `Task<long> DeletableFromLedgerAsync(CancellationToken)`.

The account is never created by this class: an account exists on the XRP Ledger only once somebody has paid its reserve into it. `XrplWallet.Generate()` makes the key, and funding makes the account.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/ThrowawayAccountTests.cs`:
```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class ThrowawayAccountTests
{
    private static readonly XrplWallet Throwaway = XrplWallet.Generate();

    private static PublicationEstimate Estimate(long required) =>
        new(3, 1, 0, 1, 5, 10, 50, 800_000, 1_000_000, required, 0);

    private static FakeLedgerSource Node(long balance = 0, long current = 5000)
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(10, 1_000_000, 200_000), CurrentLedger = current };
        source.Balances[Throwaway.ClassicAddress] = balance;
        return source;
    }

    [Fact]
    public async Task The_funding_request_names_the_address_and_what_it_needs()
    {
        FakeLedgerSource ledger = Node();
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        FundingRequest request = await account.FundingAsync(Estimate(2_500_000), TestContext.Current.CancellationToken);

        Assert.Equal(Throwaway.ClassicAddress, request.Address);
        Assert.Equal(2_500_000, request.RequiredDrops);
        Assert.Equal(0, request.BalanceDrops);
        Assert.False(request.Covered);
    }

    [Fact]
    public async Task Money_that_arrived_covers_the_request()
    {
        FakeLedgerSource ledger = Node(balance: 3_000_000);
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        FundingRequest request = await account.FundingAsync(Estimate(2_500_000), TestContext.Current.CancellationToken);

        Assert.True(request.Covered);
    }

    [Fact]
    public async Task Waiting_returns_as_soon_as_the_money_is_there()
    {
        FakeLedgerSource ledger = Node();
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        Task<FundingRequest> waiting = account.AwaitFundingAsync(
            Estimate(2_500_000), TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        ledger.Balances[Throwaway.ClassicAddress] = 2_500_000;

        FundingRequest request = await waiting;

        Assert.True(request.Covered);
    }

    [Fact]
    public async Task Waiting_stops_when_the_caller_cancels()
    {
        FakeLedgerSource ledger = Node();
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);
        using CancellationTokenSource giveUp = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => account.AwaitFundingAsync(Estimate(2_500_000), TimeSpan.FromMilliseconds(5), giveUp.Token));
    }

    [Fact]
    public async Task The_funder_is_read_from_the_payment_that_arrived()
    {
        FakeLedgerSource ledger = Node(balance: 3_000_000);
        ledger.Add(new LedgerTransaction(
            "AAAA", 5001, "rWhoeverPaid", Succeeded: true, Validated: true,
            MemoTypeHex: null, MemoDataHex: null, MemoFormatHex: null));
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        string? funder = await account.FunderAsync(5000, TestContext.Current.CancellationToken);

        Assert.Equal("rWhoeverPaid", funder);
    }

    [Fact]
    public async Task Nobody_paid_means_nobody_is_proposed()
    {
        FakeLedgerSource ledger = Node();
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        Assert.Null(await account.FunderAsync(5000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_refund_sends_everything_above_the_reserve_and_the_fee()
    {
        FakeLedgerSource ledger = Node(balance: 3_000_000);
        FakeLedgerSink sink = new(ledger);
        ThrowawayAccount account = new(sink, ledger, Throwaway);

        RefundResult refund = await account.RefundAsync("rBack", TestContext.Current.CancellationToken);

        // Three XRP held, one XRP of base reserve stays, ten drops pay for the payment.
        Assert.Equal(3_000_000 - 1_000_000 - 10, refund.SentDrops);
        Assert.Equal(1_000_000, refund.LeftBehindDrops);
        Assert.Equal("rBack", refund.Destination);
        Assert.Equal("rBack", sink.Submitted[0]["Destination"]);
    }

    [Fact]
    public async Task A_balance_that_is_only_the_reserve_has_nothing_to_send_back()
    {
        FakeLedgerSource ledger = Node(balance: 1_000_000);
        FakeLedgerSink sink = new(ledger);
        ThrowawayAccount account = new(sink, ledger, Throwaway);

        RefundResult refund = await account.RefundAsync("rBack", TestContext.Current.CancellationToken);

        Assert.Equal(0, refund.SentDrops);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_refund_without_a_destination_is_refused()
    {
        FakeLedgerSource ledger = Node(balance: 3_000_000);
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        await Assert.ThrowsAsync<ArgumentException>(
            () => account.RefundAsync("   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deletion_becomes_possible_two_hundred_and_fifty_five_ledgers_past_the_sequence()
    {
        FakeLedgerSource ledger = Node(balance: 1_000_000, current: 5000);
        FakeLedgerSink sink = new(ledger);
        ThrowawayAccount account = new(sink, ledger, Throwaway);

        // The fake sink hands out sequence 10 for this account.
        long from = await account.DeletableFromLedgerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10 + 255, from);
    }
}
```

- [ ] **Step 2: Build to see them fail**

Run: `dotnet build XrplFile.sln`
Expected: compile errors naming `ThrowawayAccount`, `FundingRequest`, `RefundResult`.

- [ ] **Step 3: Write the implementation**

`src/XrplFile.Core/ThrowawayAccount.cs`:
```csharp
using Xrpl.Wallet;

namespace XrplFile;

/// <summary>What the account needs before it can publish, and what it holds now.</summary>
public sealed record FundingRequest(string Address, long RequiredDrops, long BalanceDrops)
{
    public bool Covered => BalanceDrops >= RequiredDrops;

    public long MissingDrops => Math.Max(0, RequiredDrops - BalanceDrops);
}

/// <summary>What went back, and what could not.</summary>
public sealed record RefundResult(string Destination, long SentDrops, long LeftBehindDrops, string TxHash);

/// <summary>
/// An account made for one publication and abandoned afterwards, so that a person's own account
/// does not carry a thousand transactions of somebody else's file forever.
///
/// This class never creates an account: on the XRP Ledger an account exists once its reserve has
/// been paid into it, so the key is made here and the funding makes the account. The funding
/// itself is not ours to send — the address goes on the screen and the money arrives from
/// whatever wallet the person already trusts, which is why publishing this way needs no second
/// key in any store.
/// </summary>
public sealed class ThrowawayAccount(ILedgerSink sink, ILedgerSource source, XrplWallet wallet)
{
    /// <summary>rippled's AccountDelete precondition: Sequence + 255 must not exceed the ledger index.</summary>
    public const int DeletionSequenceDelta = 255;

    private readonly ILedgerSink _sink = sink;
    private readonly ILedgerSource _source = source;
    private readonly XrplWallet _wallet = wallet;

    public string Address => _wallet.ClassicAddress;

    public async Task<FundingRequest> FundingAsync(PublicationEstimate estimate, CancellationToken cancellationToken)
    {
        long balance = await _source.BalanceDropsAsync(Address, cancellationToken).ConfigureAwait(false);

        return new FundingRequest(Address, estimate.RequiredDrops, balance);
    }

    /// <summary>
    /// Polls until the account can carry the run. The money may arrive in a minute or in an hour,
    /// from a phone, so nothing here has a deadline of its own: the caller's token is the only
    /// thing that ends the wait.
    /// </summary>
    public async Task<FundingRequest> AwaitFundingAsync(
        PublicationEstimate estimate, TimeSpan pollEvery, CancellationToken cancellationToken)
    {
        while (true)
        {
            FundingRequest request = await FundingAsync(estimate, cancellationToken).ConfigureAwait(false);

            if (request.Covered) return request;

            await Task.Delay(pollEvery, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Who paid, read from the first transaction to reach this account. It is a proposal for the
    /// refund's destination and never more than that: the person confirms it or names another.
    /// </summary>
    public async Task<string?> FunderAsync(long fromLedger, CancellationToken cancellationToken)
    {
        long to = await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        await foreach (LedgerTransaction tx in _source
            .AccountTransactionsAsync(Address, fromLedger, to, forward: true, cancellationToken)
            .ConfigureAwait(false))
        {
            if (!tx.Succeeded || !tx.Validated) continue;
            if (string.IsNullOrEmpty(tx.Account)) continue;
            if (string.Equals(tx.Account, Address, StringComparison.Ordinal)) continue;

            return tx.Account;
        }

        return null;
    }

    /// <summary>
    /// Sends everything above the base reserve and the fee to the destination the caller names.
    ///
    /// The base reserve stays behind. Returning it would need AccountDelete, and that refuses
    /// while the account's sequence plus 255 exceeds the ledger index — which minting tickets
    /// pushes hours or days into the future. What is left is a payment, and it is honest about
    /// what it cannot recover.
    /// </summary>
    public async Task<RefundResult> RefundAsync(string destination, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("a refund needs a destination; nothing is assumed", nameof(destination));
        }

        NetworkFees fees = await _source.FeesAsync(cancellationToken).ConfigureAwait(false);
        long balance = await _source.BalanceDropsAsync(Address, cancellationToken).ConfigureAwait(false);
        long sendable = balance - fees.BaseReserveDrops - fees.OpenLedgerFeeDrops;

        if (sendable <= 0)
        {
            return new RefundResult(destination, 0, Math.Min(balance, fees.BaseReserveDrops), string.Empty);
        }

        uint sequence = await _sink.SequenceAsync(Address, validated: false, cancellationToken).ConfigureAwait(false);

        Dictionary<string, object> payment = new()
        {
            ["TransactionType"] = "Payment",
            ["Account"] = Address,
            ["Destination"] = destination,
            ["Amount"] = sendable.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Sequence"] = sequence,
            ["Fee"] = fees.OpenLedgerFeeDrops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SigningPubKey"] = _wallet.PublicKey,
        };

        SubmitOutcome outcome = await _sink.SubmitAsync(payment, _wallet, cancellationToken).ConfigureAwait(false);

        if (outcome.EngineResult is not ("tesSUCCESS" or "terQUEUED"))
        {
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"the node answered {outcome.EngineResult} to the refund of {sendable} drops to {destination}");
        }

        return new RefundResult(destination, sendable, fees.BaseReserveDrops, outcome.TxHash);
    }

    /// <summary>
    /// The first ledger at which deleting this account would be allowed.
    ///
    /// Every transaction raises the account's sequence and so raises this number with it, and
    /// TicketCreate raises it by the ticket count plus one. Nothing an account does brings the
    /// moment closer; only ledgers closing do.
    /// </summary>
    public async Task<long> DeletableFromLedgerAsync(CancellationToken cancellationToken)
    {
        uint sequence = await _sink.SequenceAsync(Address, validated: true, cancellationToken).ConfigureAwait(false);

        return sequence + DeletionSequenceDelta;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.ThrowawayAccountTests"`
Expected: all pass.

- [ ] **Step 5: Write the standalone test**

`tests/XrplFile.Core.Tests/ThrowawayAccountStandaloneTests.cs`:
```csharp
using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>The throwaway mode end to end: funded from outside, published, and paid back.</summary>
[Trait("Category", "Integration")]
public class ThrowawayAccountStandaloneTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "xrpl-file-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    [Fact]
    public async Task A_file_is_published_from_a_funded_throwaway_and_the_remainder_goes_home()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);

            XrplWallet funder = await StandaloneFixture.FundedAccountAsync(client, sink, 900_000_000, token);
            XrplWallet throwaway = XrplWallet.Generate();
            ThrowawayAccount account = new(sink, source, throwaway);

            byte[] file = VectorStream.Bytes("throwaway", 12_000);
            long startedIn = await source.CurrentLedgerAsync(token);

            PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
                source, throwaway.ClassicAddress, file.Length, ticketsPerBatch: 100, token);

            // The address goes on a screen; the money comes from wherever the person keeps it.
            FundingRequest request = await account.FundingAsync(estimate, token);
            Assert.False(request.Covered);

            await StandaloneFixture.PayAsync(sink, funder, request.Address, request.RequiredDrops + 5_000_000, token);

            FundingRequest funded = await account.AwaitFundingAsync(estimate, TimeSpan.FromSeconds(2), token);
            Assert.True(funded.Covered);

            // The funder is read back from the ledger, and it is who actually paid.
            string? observed = await account.FunderAsync(startedIn, token);
            Assert.Equal(funder.ClassicAddress, observed);

            SessionOptions options = new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };

            await using TicketStrategy strategy = new(sink, source, throwaway, new TicketStrategyOptions { TicketsPerBatch = 100 });

            PublicationSession session = await PublicationSession.StartAsync(
                strategy, source, file, "application/octet-stream", "throwaway.bin",
                throwaway.ClassicAddress, "key-throwaway", "1", options, token);

            PublicationReport report = await session.RunAsync(null, token);

            ReadResult read = await new XrplFileReader(source).ReadAsync(report.Link, token);
            Assert.Equal(file, read.Bytes);

            // What is left goes back to the address the funder was read from.
            long before = await source.BalanceDropsAsync(funder.ClassicAddress, token);
            RefundResult refund = await account.RefundAsync(observed!, token);

            Assert.True(refund.SentDrops > 0, "the run should not have spent everything");

            await StandaloneFixture.WaitForBalanceAboveAsync(source, funder.ClassicAddress, before, token);

            // The base reserve is the price of the mode, and it is still on the dead account.
            long left = await source.BalanceDropsAsync(throwaway.ClassicAddress, token);
            Assert.InRange(left, 900_000, 1_100_000);

            // And deletion is a long way off, because minting moved the sequence far ahead.
            long deletableFrom = await account.DeletableFromLedgerAsync(token);
            long now = await source.CurrentLedgerAsync(token);
            Assert.True(deletableFrom > now, $"deletion should still be out of reach at ledger {now}");
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
```

Add `PayAsync` and `WaitForBalanceAboveAsync` to `StandaloneFixture` alongside `FundedAccountAsync`; both are the payment and polling helpers the existing tests already contain in private form.

- [ ] **Step 6: Run everything**

```bash
dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml up -d
XRPL_FILE_NODE_URL=ws://localhost:16006 dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration"
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml down
cd web && npm test
```
Expected: all three green. The JavaScript suite must be untouched by this work; a failure there means the format moved, which nothing in this plan intends.

- [ ] **Step 7: Commit**

```bash
git add src/XrplFile.Core/ThrowawayAccount.cs tests/XrplFile.Core.Tests/ThrowawayAccountTests.cs tests/XrplFile.Core.Tests/ThrowawayAccountStandaloneTests.cs tests/XrplFile.Core.Tests/StandaloneFixture.cs
git commit -m "feat(core): publish from an account made for it, and pay back what it did not spend"
```

---

### Task 8: Say what changed

**Files:**
- Modify: `README.md`, `docs/superpowers/specs/2026-09-07-tickets-and-publication-session-design.md`, `.github/workflows/test.yml`

**Interfaces:**
- Consumes: everything above.

- [ ] **Step 1: Replace the README's publishing example**

The current example calls `writer.PublishAsync(...)` with a wallet, which no longer compiles. Replace that section with the session, and add the throwaway mode beside it:

````markdown
## Publishing a file (.NET)

```csharp
XrplWallet wallet = XrplWallet.FromSeed(Environment.GetEnvironmentVariable("XRPL_SEED")!);
XrplClientLedgerSource source = new(client);
XrplClientLedgerSink sink = new(client);
SessionOptions options = new() { StateDirectory = "runs", BudgetDrops = 5_000_000 };

// What it will cost, before any of it is spent.
PublicationEstimate estimate = await PublicationSession.EstimateAsync(
    source, wallet.ClassicAddress, new FileInfo("cover.png").Length, options, CancellationToken.None);

Console.WriteLine($"{estimate.Transactions} transactions, {estimate.RequiredDrops} drops needed, {estimate.BalanceDrops} held");

await using TicketStrategy strategy = new(sink, source, wallet);
PublicationSession session = await PublicationSession.StartAsync(
    strategy, source, File.ReadAllBytes("cover.png"), "image/png", "cover.png",
    wallet.ClassicAddress, keyReference: "my-key", "mainnet", options, CancellationToken.None);

PublicationReport report = await session.RunAsync(
    new Progress<PublishProgress>(p => Console.WriteLine($"{p.Stage} {p.Done}/{p.Total}")), CancellationToken.None);

Console.WriteLine(report.Link);
```

Chunks ride tickets, so they go in parallel rather than one after another. The session writes a
state file before the first transaction; if the process dies, `PublicationSession.ResumeAsync`
picks the run up from it and asks the ledger what actually landed rather than trusting the file.

`SequenceStrategy` is the slower alternative and needs no reserve beyond the account's own. It is
what to use when the account cannot hold a batch of tickets.
````

- [ ] **Step 2: Record what the stand measured**

In the design document, replace the section "One thing to measure before it is believed" with what Task 0 actually found: the observed threshold, whether `tecTOO_SOON` appeared where predicted, and the wall-clock the ticketed publication took in Tasks 3 and 6. Keep the section, retitled "What the stand measured", so the next reader sees numbers rather than a promise.

- [ ] **Step 3: Check the workflow still describes the suite**

`.github/workflows/test.yml` runs the two suites and the vector regeneration. Nothing in this plan changes those commands, so the only question is whether the integration job's fifteen-second wait is still enough now that the integration suite is longer. Read the job, and if the new tests push it past the runner's patience, say so in the report rather than raising the timeout blindly.

- [ ] **Step 4: Verify the README's commands as written**

Run every command in the README's "Building and testing" section from a clean clone, including the integration block. Expected: each exits zero.

- [ ] **Step 5: Commit**

```bash
git add README.md docs/superpowers/specs/2026-09-07-tickets-and-publication-session-design.md .github/workflows/test.yml
git commit -m "docs: publishing is a session now, and the stand has measured the deletion threshold"
```

---

## Done when

- The non-integration suite is green, and so is `web`'s, unchanged.
- The integration suite is green against the stand, Task 0's threshold tests included.
- A ticketed publication of a megabyte-scale file is measurably faster than the sequential one, with the number in the report.
- A publication interrupted after its chunks is finished by a second session, and the file reads back byte for byte.
- A throwaway account is funded from outside, publishes, and pays its remainder to an address that was confirmed rather than assumed.
