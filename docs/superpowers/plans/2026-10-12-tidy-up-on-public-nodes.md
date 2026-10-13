# Tidy-up on Loaded Public Nodes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the post-publication ticket tidy-up survive loaded public XRPL nodes — busy answers, rate-limit warnings, dropped queued spends, closed sockets — and make the publication report say what the run and its tidy-up cost.

**Architecture:** The two SDK adapters (`XrplClientLedgerSink`, `XrplClientLedgerSource`) learn to classify what a real node does: error answers become outcomes, transport failures become `NodeUnreachable`, load warnings are counted. `LeftoverTickets.ReleaseAsync` is rebuilt around per-ticket bookkeeping (`Spends`) and a pacing object (`TidyUpPace`): each round reads the validated index, sends spends carrying `LastLedgerSequence`, settles against one listing, and backs off on strain. The session prices the tidy-up and counts the mints its own strategy made.

**Tech Stack:** .NET (C#), xUnit v3 (built-in `Assert`), Xrpl C# SDK 11.2.0, rippled 3.3.0 standalone stand.

**Spec:** `docs/superpowers/specs/2026-10-12-leftover-tickets-design.md` — decisions I13–I18 and sections 4.6–4.9, 5, 7, 8. Read sections 4.6, 4.8 and 4.9 in full before starting any task.

## Global Constraints

- `dotnet test` does not work in this repo. Run a test project as an executable: `dotnet run --project <proj> -- <filters>`; filters are `-class <FullName>`, `-method <FullName>`, `-trait "Category=Integration"`, `-trait- "Category=Integration"`.
- Test projects: `tests/XrplFile.Core.Tests`, `tests/XrplFile.App.Core.Tests`, `tests/XrplFile.Keys.Tests`. Test namespace is `XrplFile.Tests`.
- Warnings are errors. Explicit types, never `var`. `ConfigureAwait(false)` on every await in `src`.
- xUnit1069 is a build error: a test with `Timeout` must use `TestContext.Current.CancellationToken`.
- A fake must never be more forgiving than a real node.
- Integration stand: compose project `xrpl-file-ci`, up at `ws://localhost:6006`. Docker only with `-p xrpl-file-ci`.
- Commits: Conventional Commits, no AI attribution of any kind. Do not push.
- Exact values from the spec: `LastLedgerSequence = validated index + 10`; at most 5 submissions per ticket; sink makes at most 3 transmissions per submission; passing refusals: `tel*`, `ter*` except `terQUEUED`, `tefMAX_LEDGER`, `error:tooBusy`, `error:notSynced`, `error:noCurrent`, `error:noNetwork`, `error:noClosed`; back-off `inFlight = max(1, inFlight / 2)` from 25, `pause = min(16 × poll, pause × 2)` from `poll`; no speed-up.
- `PublicationReport`'s and `TicketRelease`'s positional constructors do not change; additions are init-only properties.

## Review Focus

1. **The validated index stops moving** (a stalled network, a stand without its ledger acceptor): no spend may ever be judged expired, so a queued spend is sent exactly once and waits for the deadline. Test in Task 5.
2. **The index read fails every round**: nothing is sent, nothing throws, the tidy-up ends at its deadline with every ticket not released and none in doubt. Test in Task 5.
3. **Warnings on every answer**: the pace reaches its floor of one and its ceiling of sixteen polls and stays there, and a small tidy-up still finishes. Test in Task 7.
4. **Every submission unanswered, then the ticket consumed by something else**: the ticket is already gone and in doubt, never charged. Test in Task 6.
5. **A resend after expiry carries a fresh `LastLedgerSequence` strictly greater than the expired one** — resending the same deadline would be refused `tefMAX_LEDGER` for ever. Test in Task 5.

---

## File Structure

| File | Responsibility | Tasks |
|---|---|---|
| `src/XrplFile.Core/NodeAnswer.cs` (new) | Internal: one raw answer plus the envelope's `warning` | 1 |
| `src/XrplFile.Core/ILedgerSink.cs` | `SubmitOutcome.FollowsUnansweredAttempt`; `ILedgerSink.LoadWarnings` default member | 1, 2 |
| `src/XrplFile.Core/ILedgerSource.cs` | `ILedgerSource.LoadWarnings` default member | 2 |
| `src/XrplFile.Core/XrplClientLedgerSink.cs` | Error answers → outcomes, transport failures → unanswered, warning count | 1, 2 |
| `src/XrplFile.Core/XrplClientLedgerSource.cs` | Unsolicited cancellation → `NodeUnreachable`, warning count | 2 |
| `tests/XrplFile.Core.Tests/XrplClientLedgerSinkTests.cs` (exists) | Sink seam tests; its `ScriptedNode` moves onto `NodeAnswer` | 1, 2 |
| `src/XrplFile.Core/XrplFileWriter.cs`, `tests/XrplFile.Core.Tests/XrplFileWriterTests.cs` | An unsolicited cancellation is `NodeUnreachable` at the writer's boundary | 2 |
| `tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs` | Source seam tests | 2 |
| `tests/XrplFile.TestSupport/FakeLedgerSink.cs`, `FakeLedgerSource.cs` | LLS enforcement, scripted warnings, in-flight/timing log, ledger clock | 3 |
| `tests/XrplFile.Core.Tests/FakeLedgerSinkTests.cs` | Tests of the fakes' new behaviour | 3 |
| `src/XrplFile.Core/TidyUpPace.cs` (new) | Back-off arithmetic | 4 |
| `src/XrplFile.Core/LeftoverTickets.cs` | Rounds, LLS, expiry, `Spends` bookkeeping, back-off wiring | 5, 6, 7 |
| `src/XrplFile.Core/TicketRelease.cs` | `Charged`, `InDoubt` | 6 |
| `tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs`, `TidyUpPaceTests.cs` (new) | Tidy-up tests | 4–7 |
| `src/XrplFile.Core/ISubmissionStrategy.cs`, `TicketStrategy.cs`, `SequenceStrategy.cs` | `TicketMints` | 8 |
| `src/XrplFile.Core/PublicationSession.cs`, `PublicationReport.cs` | Mints delta, tidy-up fees, null when counts lost | 9 |
| `README.md`, spec | Report fields and back-off described | 9 |
| `tests/XrplFile.Core.Tests/LeftoverTicketsStandaloneTests.cs`, `XrplClientLedgerSinkStandaloneTests.cs` (new) | Real-node proof | 10 |

---

### Task 1: The sink keeps its contract against a real node (I13, I14)

**Files:**
- Create: `src/XrplFile.Core/NodeAnswer.cs`
- Modify: `src/XrplFile.Core/ILedgerSink.cs` (record `SubmitOutcome`)
- Modify: `src/XrplFile.Core/XrplClientLedgerSink.cs`
- Modify: `tests/XrplFile.Core.Tests/XrplClientLedgerSinkTests.cs` (exists: 10 tests, its own `ScriptedNode`, `Wallet`, `Signer` and `Submit(node)` helper — keep all of them)

**Interfaces:**
- Produces: `internal readonly record struct NodeAnswer(JsonElement Result, string? Warning)`; `SubmitOutcome.FollowsUnansweredAttempt { get; init; }` (bool, true only on a refusal); internal ctor `XrplClientLedgerSink(Func<BaseRequest, CancellationToken, Task<NodeAnswer>> request)` beside the existing `Task<JsonElement>` one; the test file's `ScriptedNode` answers `Task<NodeAnswer>` and has `Answer(string json, string? warning = null)`, `Throw(Exception)`, `Do(Func<NodeAnswer>)`, `Requests` and `Commands`.

- [ ] **Step 1: Move the test file's `ScriptedNode` onto `NodeAnswer`**

In `tests/XrplFile.Core.Tests/XrplClientLedgerSinkTests.cs` replace the `ScriptedNode` class with the one below. Every existing test keeps compiling unchanged: `Answer(json)` and `Throw(e)` keep their meaning, `Requests` keeps its tuples, and `node.HandleAsync` as a method group now picks the `NodeAnswer` constructor.

```csharp
    /// <summary>A node whose answers are scripted, in call order: raw JSON with the envelope's warning, or an exception.</summary>
    private sealed class ScriptedNode
    {
        private readonly Queue<Func<NodeAnswer>> _answers = new();

        public List<(string Command, string? TxBlob)> Requests { get; } = [];

        public List<string> Commands => [.. Requests.Select(request => request.Command)];

        public void Answer(string json, string? warning = null) =>
            _answers.Enqueue(() => new NodeAnswer(JsonDocument.Parse(json).RootElement, warning));

        public void Throw(Exception exception) => _answers.Enqueue(() => throw exception);

        /// <summary>Runs <paramref name="answer"/> when this call is reached — to cancel a token mid-call, say.</summary>
        public void Do(Func<NodeAnswer> answer) => _answers.Enqueue(answer);

        public Task<NodeAnswer> HandleAsync(BaseRequest request, CancellationToken cancellationToken)
        {
            string? txBlob = request is SubmitRequest submit ? submit.TxBlob : null;
            Requests.Add((request.Command, txBlob));

            if (_answers.Count == 0)
            {
                throw new InvalidOperationException("the script ran out of answers");
            }

            return Task.FromResult(_answers.Dequeue()());
        }
    }
```

- [ ] **Step 2: Write the failing tests**

Add to the same class (it already has `Wallet` and `Signer`; add the `using` lines `Xrpl.Client.Exceptions` and `Xrpl.Models.Subscriptions` if absent):

```csharp
    private static Dictionary<string, object> AccountSet() => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = Wallet.ClassicAddress,
        ["Sequence"] = 1u,
        ["Fee"] = "12",
        ["SigningPubKey"] = Wallet.PublicKey,
    };

    private static RippledException NodeError(string code) => new(code, new ErrorResponse { Error = code });

    [Fact]
    public async Task A_thrown_error_answer_is_an_outcome_like_an_embedded_one()
    {
        ScriptedNode thrown = new();
        thrown.Throw(NodeError("tooBusy"));
        ScriptedNode embedded = new();
        embedded.Answer("""{"error":"tooBusy"}""");

        SubmitOutcome fromThrown = await new XrplClientLedgerSink(thrown.HandleAsync)
            .SubmitAsync(AccountSet(), Signer, TestContext.Current.CancellationToken);
        SubmitOutcome fromEmbedded = await new XrplClientLedgerSink(embedded.HandleAsync)
            .SubmitAsync(AccountSet(), Signer, TestContext.Current.CancellationToken);

        Assert.Equal("error:tooBusy", fromThrown.EngineResult);
        Assert.Equal("error:tooBusy", fromEmbedded.EngineResult);
        Assert.False(fromThrown.FollowsUnansweredAttempt);
    }

    [Fact]
    public async Task A_closed_socket_is_retried_and_then_unreachable()
    {
        ScriptedNode node = new();

        // submit, tx lookup, three times over.
        for (int i = 0; i < 6; i++) node.Throw(new DisconnectedException("websocket was closed, code: 1008, reason: threshold exceeded"));

        XrplFileException failure = await Assert.ThrowsAsync<XrplFileException>(() =>
            new XrplClientLedgerSink(node.HandleAsync).SubmitAsync(AccountSet(), Signer, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, failure.Error);
        Assert.Equal(["submit", "tx", "submit", "tx", "submit", "tx"], node.Commands);
    }

    [Fact]
    public async Task A_cancellation_nobody_asked_for_is_a_node_that_did_not_answer()
    {
        ScriptedNode node = new();
        node.Throw(new OperationCanceledException("Connection was intentionally closed."));
        node.Throw(new NotConnectedException());
        node.Answer("""{"engine_result":"tesSUCCESS"}""");

        SubmitOutcome outcome = await new XrplClientLedgerSink(node.HandleAsync)
            .SubmitAsync(AccountSet(), Signer, TestContext.Current.CancellationToken);

        Assert.Equal("tesSUCCESS", outcome.EngineResult);

        // Only a refusal carries the flag: this answer took the transaction.
        Assert.False(outcome.FollowsUnansweredAttempt);
    }

    [Fact]
    public async Task The_callers_own_cancellation_stays_a_cancellation()
    {
        // Cancelled inside the node call, after signing: WalletSigner checks the token first, so a
        // token cancelled before the call would never reach the sink's own handling.
        using CancellationTokenSource caller = new();
        ScriptedNode node = new();
        node.Do(() =>
        {
            caller.Cancel();
            throw new OperationCanceledException(caller.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new XrplClientLedgerSink(node.HandleAsync).SubmitAsync(AccountSet(), Signer, caller.Token));

        // No lookup and no resubmission: the caller stopped, the node did not fail.
        Assert.Equal(["submit"], node.Commands);
    }

    [Fact]
    public async Task A_signer_that_refuses_is_not_a_node_failure()
    {
        ScriptedNode node = new();

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new XrplClientLedgerSink(node.HandleAsync).SubmitAsync(AccountSet(), new RefusingSigner(), TestContext.Current.CancellationToken));

        Assert.Equal("the vault is locked", refused.Message);
        Assert.Empty(node.Commands);
    }

    [Fact]
    public async Task A_refusal_after_an_unanswered_transmission_says_so()
    {
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("submit timed out"));
        node.Answer("""{"error":"txnNotFound"}""");
        node.Answer("""{"engine_result":"telCAN_NOT_QUEUE"}""");

        SubmitOutcome outcome = await new XrplClientLedgerSink(node.HandleAsync)
            .SubmitAsync(AccountSet(), Signer, TestContext.Current.CancellationToken);

        Assert.Equal("telCAN_NOT_QUEUE", outcome.EngineResult);
        Assert.True(outcome.FollowsUnansweredAttempt);
    }

    [Fact]
    public async Task A_thrown_error_answer_to_the_lookup_is_not_known_yet()
    {
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("submit timed out"));
        node.Throw(NodeError("tooBusy"));
        node.Answer("""{"engine_result":"tesSUCCESS"}""");

        SubmitOutcome outcome = await new XrplClientLedgerSink(node.HandleAsync)
            .SubmitAsync(AccountSet(), Signer, TestContext.Current.CancellationToken);

        Assert.Equal("tesSUCCESS", outcome.EngineResult);
        Assert.Equal(["submit", "tx", "submit"], node.Commands);
    }

    [Fact]
    public async Task An_embedded_error_answer_to_the_lookup_is_not_known_yet_and_not_a_queued_transaction()
    {
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("submit timed out"));
        node.Answer("""{"error":"tooBusy"}""");
        node.Answer("""{"engine_result":"tesSUCCESS"}""");

        SubmitOutcome outcome = await new XrplClientLedgerSink(node.HandleAsync)
            .SubmitAsync(AccountSet(), Signer, TestContext.Current.CancellationToken);

        Assert.Equal("tesSUCCESS", outcome.EngineResult);
        Assert.Equal(["submit", "tx", "submit"], node.Commands);
    }

    /// <summary>A signer that refuses the way a locked vault does.</summary>
    private sealed class RefusingSigner : ITransactionSigner
    {
        public string Account => Wallet.ClassicAddress;

        public string PublicKey => Wallet.PublicKey;

        public Task<SignedTransaction> SignAsync(IDictionary<string, object> transaction, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the vault is locked");
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.XrplClientLedgerSinkTests`
Expected: build error — `NodeAnswer` and `FollowsUnansweredAttempt` do not exist.

- [ ] **Step 4: Implement**

Create `src/XrplFile.Core/NodeAnswer.cs`:

```csharp
using System.Text.Json;

namespace XrplFile;

/// <summary>
/// One answer from a node as the connection classes see it: the <c>result</c> member, and the
/// envelope's <c>warning</c> beside it, which <see cref="Xrpl.Client.XrplResponse{T}.Raw"/> does
/// not reach (spec 4.8).
/// </summary>
/// <param name="Result">The <c>result</c> member exactly as the node sent it.</param>
/// <param name="Warning">The envelope's <c>warning</c> — <c>"load"</c> when the client nears its rate limit — or null.</param>
internal readonly record struct NodeAnswer(JsonElement Result, string? Warning);
```

In `src/XrplFile.Core/ILedgerSink.cs` replace the record line with:

```csharp
/// <summary>What the node said to one submission, and the hash it is known by.</summary>
public sealed record SubmitOutcome(string EngineResult, string TxHash)
{
    /// <summary>
    /// True when this answer is a refusal of a later transmission of the same blob, after an earlier
    /// transmission went unanswered (spec 4.8); never on an answer that took the transaction. The
    /// refusal is about the later copy only; the earlier one may still be in the node and apply.
    /// </summary>
    public bool FollowsUnansweredAttempt { get; init; }
}
```

In `src/XrplFile.Core/XrplClientLedgerSink.cs`:

1. Change the field to `private readonly Func<BaseRequest, CancellationToken, Task<NodeAnswer>> _request;`.
2. Keep the existing internal ctor, adapting it, and add the new one:

```csharp
    internal XrplClientLedgerSink(Func<BaseRequest, CancellationToken, Task<JsonElement>> request)
        : this(async (sent, token) => new NodeAnswer(await request(sent, token).ConfigureAwait(false), null))
    {
    }

    /// <summary>The same seam, for a scripted node that also says what the envelope's <c>warning</c> was.</summary>
    internal XrplClientLedgerSink(Func<BaseRequest, CancellationToken, Task<NodeAnswer>> request)
    {
        _request = request;
    }
```

3. Replace `SubmitAsync` and `ResultOfAsync` with:

```csharp
    public async Task<SubmitOutcome> SubmitAsync(
        Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
    {
        SignedTransaction signed = await signer.SignAsync(transaction, cancellationToken).ConfigureAwait(false);
        string hash = signed.Hash;
        bool unanswered = false;

        for (int attempt = 1; attempt <= MaxSubmitAttempts; attempt++)
        {
            string outcome;

            try
            {
                NodeAnswer answer = await _request(new SubmitRequest { TxBlob = signed.TxBlob }, cancellationToken).ConfigureAwait(false);

                outcome =
                    answer.Result.TryGetProperty("engine_result", out JsonElement code) ? code.GetString() ?? string.Empty
                    : answer.Result.TryGetProperty("error", out JsonElement error) ? "error:" + error.GetString()
                    : answer.Result.GetRawText();
            }
            catch (RippledException e)
            {
                // A real node's error answer: the SDK raises status "error" before any JSON reaches
                // here. The same outcome the embedded shape above gives, so both agree (spec I13).
                outcome = "error:" + e.Classify().RawError;
            }
            catch (Exception e) when (DidNotAnswer(e, cancellationToken))
            {
                // The node did not answer; it may still have the transaction (spec I14).
                unanswered = true;
                string? landed = await ResultOfAsync(hash, cancellationToken).ConfigureAwait(false);
                if (landed is not null) return new SubmitOutcome(landed, hash);

                continue;
            }

            if (outcome is "tefPAST_SEQ" or "tefALREADY" or "tefNO_TICKET")
            {
                string? landed = await ResultOfAsync(hash, cancellationToken).ConfigureAwait(false);
                if (landed is not null) return new SubmitOutcome(landed, hash);
            }

            return new SubmitOutcome(outcome, hash) { FollowsUnansweredAttempt = unanswered && !TookIt(outcome) };
        }

        throw new XrplFileException(XrplFileError.NodeUnreachable,
            $"the node answered neither submit nor tx for transaction {hash} in {MaxSubmitAttempts} attempts");
    }

    /// <summary>The answers that mean the node took the transaction: it may yet apply, and is not a refusal.</summary>
    private static bool TookIt(string outcome) =>
        outcome.StartsWith("tes", StringComparison.Ordinal)
        || outcome.StartsWith("tec", StringComparison.Ordinal)
        || string.Equals(outcome, Queued, StringComparison.Ordinal);

    /// <summary>
    /// A request the node did not answer (spec I14): a connection closed, lost or never made, a
    /// timeout, a response the SDK could not read, or a cancellation the caller's token did not
    /// cause — the SDK cancels requests in flight when the network drops. A node's own error answer
    /// is a <see cref="RippledException"/> and is not this.
    /// </summary>
    private static bool DidNotAnswer(Exception e, CancellationToken cancellationToken) =>
        e is XrplException and not RippledException
        || e is System.TimeoutException
        || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>
    /// What the node says happened to this transaction, or null when it does not say — which a lookup
    /// that times out or loses its connection also means, and so does any error answer to <c>tx</c>
    /// in either shape: <c>txnNotFound</c> above all, but a busy node's <c>tooBusy</c> says nothing
    /// about the transaction either (spec 4.8). A validated transaction reports its final result, so
    /// a <c>tec</c>/<c>tef</c> code is reported as such and never mistaken for acceptance; one still
    /// in the open ledger reports its provisional result, or merely that the node holds it.
    /// </summary>
    private async Task<string?> ResultOfAsync(string hash, CancellationToken cancellationToken)
    {
        NodeAnswer answer;

        try
        {
            answer = await _request(new TxRequest(hash), cancellationToken).ConfigureAwait(false);
        }
        catch (RippledException)
        {
            return null;
        }
        catch (Exception e) when (DidNotAnswer(e, cancellationToken))
        {
            return null;
        }

        if (answer.Result.TryGetProperty("error", out _)) return null;

        return TransactionResult(answer.Result) ?? Queued;
    }
```

Keep the existing XML doc on `SubmitAsync` and add one paragraph: "A node's error answer comes back as `error:<code>` whichever shape it arrives in, and a connection that dropped is handled as a timeout is (spec 4.8). A refusal that follows an unanswered transmission of the same blob says so on `FollowsUnansweredAttempt`." The `XrplErrorCodes` using stays if anything else uses it; remove it otherwise (warnings are errors).

4. `SendAsync` returns the envelope's warning:

```csharp
    private async Task<NodeAnswer> SendAsync(BaseRequest request, CancellationToken cancellationToken)
    {
        Task<XrplResponse<object>> pending = request switch
        {
            SubmitRequest submit => _client!.GRequest<object, SubmitRequest>(submit, cancellationToken),
            TxRequest tx => _client!.GRequest<object, TxRequest>(tx, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Command, "this sink sends submit and tx, nothing else"),
        };

        XrplResponse<object> response = await pending.ConfigureAwait(false);

        return new NodeAnswer(response.Raw.ToJsonElement(), response.Warning);
    }
```

The public ctor keeps `_request = SendAsync;`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.XrplClientLedgerSinkTests`
Expected: 18 passed (10 existing, 8 new).

- [ ] **Step 6: Run the whole non-integration Core suite**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/XrplFile.Core/NodeAnswer.cs src/XrplFile.Core/ILedgerSink.cs src/XrplFile.Core/XrplClientLedgerSink.cs tests/XrplFile.Core.Tests/XrplClientLedgerSinkTests.cs
git commit -m "fix(core): the sink reports a real node's error answers and dropped sockets as it reports the scripted ones"
```

---

### Task 2: Load warnings are counted; the source names an unsolicited cancellation (I14, I15)

**Files:**
- Modify: `src/XrplFile.Core/ILedgerSink.cs`, `src/XrplFile.Core/ILedgerSource.cs`
- Modify: `src/XrplFile.Core/XrplClientLedgerSink.cs`, `src/XrplFile.Core/XrplClientLedgerSource.cs`, `src/XrplFile.Core/XrplFileWriter.cs`
- Test: `tests/XrplFile.Core.Tests/XrplClientLedgerSinkTests.cs`, `tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs`, `tests/XrplFile.Core.Tests/XrplFileWriterTests.cs`

**Interfaces:**
- Consumes: `NodeAnswer` (Task 1).
- Produces: `long ILedgerSink.LoadWarnings => 0;` and `long ILedgerSource.LoadWarnings => 0;` (default interface members); public `LoadWarnings` on both connection classes; internal ctor `XrplClientLedgerSource(Func<BaseRequest, CancellationToken, Task<NodeAnswer>> request, int pageSize = 400)`.

- [ ] **Step 1: Write the failing tests**

Add to `XrplClientLedgerSinkTests`:

```csharp
    [Fact]
    public async Task The_sink_counts_the_nodes_load_warnings()
    {
        ScriptedNode node = new();
        node.Answer("""{"engine_result":"tesSUCCESS"}""", warning: "load");
        node.Answer("""{"engine_result":"tesSUCCESS"}""");
        XrplClientLedgerSink sink = new(node.HandleAsync);

        await sink.SubmitAsync(AccountSet(), Signer, TestContext.Current.CancellationToken);
        await sink.SubmitAsync(AccountSet(), Signer, TestContext.Current.CancellationToken);

        Assert.Equal(1, sink.LoadWarnings);
        Assert.Equal(1, ((ILedgerSink)sink).LoadWarnings);
    }
```

In `XrplClientLedgerSourceTests`, replace `A_cancellation_stays_a_cancellation` with the two tests below, and add a `NodeAnswer` script to `ScriptedNode` (a second queue is not needed — add a method `AnswerWithWarning(string json)` that enqueues into a parallel `Queue<string?>` of warnings, and a `HandleWithWarningsAsync` returning `Task<NodeAnswer>` that dequeues both):

```csharp
    [Fact]
    public async Task The_callers_own_cancellation_stays_a_cancellation()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        ScriptedNode node = new();
        node.Throw(new OperationCanceledException(cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new XrplClientLedgerSource(node.HandleAsync).EarliestLedgerAsync(cancelled.Token));
    }

    [Theory]
    [InlineData("listing")]
    [InlineData("ledger")]
    [InlineData("lookup")]
    public async Task A_cancellation_nobody_asked_for_is_a_node_that_did_not_answer(string read)
    {
        ScriptedNode node = new();
        node.Throw(new OperationCanceledException("Connection was intentionally closed."));
        XrplClientLedgerSource source = new(node.HandleAsync);
        CancellationToken token = TestContext.Current.CancellationToken;
        Func<Task> call = read switch
        {
            "listing" => () => source.LiveTicketsAsync("rWriter", token),
            "ledger" => () => source.CurrentLedgerAsync(token),
            _ => () => source.TransactionAsync(new string('A', 64), token),
        };

        XrplFileException failure = await Assert.ThrowsAsync<XrplFileException>(call);

        Assert.Equal(XrplFileError.NodeUnreachable, failure.Error);
    }

    [Fact]
    public async Task The_source_counts_the_nodes_load_warnings()
    {
        ScriptedNode node = new();
        node.AnswerWithWarning("""{"ledger_index":7}""", "load");
        node.AnswerWithWarning("""{"ledger_index":8}""", null);
        XrplClientLedgerSource source = new(node.HandleWithWarningsAsync);

        await source.CurrentLedgerAsync(TestContext.Current.CancellationToken);
        await source.CurrentLedgerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, source.LoadWarnings);
    }
```

`ScriptedNode` additions:

```csharp
        private readonly Queue<string?> _warnings = new();

        public void AnswerWithWarning(string json, string? warning)
        {
            Answer(json);
            _warnings.Enqueue(warning);
        }

        public async Task<NodeAnswer> HandleWithWarningsAsync(Xrpl.Models.Methods.BaseRequest request, CancellationToken cancellationToken) =>
            new(await HandleAsync(request, cancellationToken), _warnings.Count > 0 ? _warnings.Dequeue() : null);
```

In `tests/XrplFile.Core.Tests/XrplFileWriterTests.cs` (it has `Wallet` and `Signer`) add:

```csharp
    [Fact]
    public async Task A_network_drop_is_an_unreachable_node_not_a_cancellation()
    {
        FakeLedgerSource ledger = new();
        await using SequenceStrategy strategy = new(new DroppingSink(new FakeLedgerSink(ledger)), Signer);
        XrplFileWriter writer = new(strategy, ledger);

        XrplFileException failure = await Assert.ThrowsAsync<XrplFileException>(() => writer.PublishAsync(
            VectorStream.Bytes("drop", 2500), "text/plain", "d.txt", Wallet.ClassicAddress, "testnet",
            null, null, TestContext.Current.CancellationToken));

        // The SDK cancels requests in flight when the network drops; nobody cancelled this run (spec I14).
        Assert.Equal(XrplFileError.NodeUnreachable, failure.Error);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
    }

    /// <summary>A sink whose sequence read meets a dropped network: the SDK's cancellation, not the caller's.</summary>
    private sealed class DroppingSink(ILedgerSink inner) : ILedgerSink
    {
        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            Task.FromException<uint>(new OperationCanceledException("Connection was intentionally closed."));

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
            inner.SubmitAsync(transaction, signer, cancellationToken);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.XrplClientLedgerSourceTests` and `-class XrplFile.Tests.XrplClientLedgerSinkTests`
Expected: build error — `LoadWarnings` and the `NodeAnswer` source ctor do not exist. (`A_network_drop_is_an_unreachable_node_not_a_cancellation` compiles today and fails with an `OperationCanceledException`.)

- [ ] **Step 3: Implement**

`ILedgerSink` gains:

```csharp
    /// <summary>
    /// How many of the node's answers so far carried <c>"warning": "load"</c> — the client is nearing
    /// the node's rate limit (spec 4.8). Only grows. Zero for an implementation that does not talk to
    /// a rate-limited node; a decorator that wraps one should forward it.
    /// </summary>
    long LoadWarnings => 0;
```

`ILedgerSource` gains the same member with the same doc.

`XrplClientLedgerSink`: add

```csharp
    private long _loadWarnings;

    public long LoadWarnings => Interlocked.Read(ref _loadWarnings);

    /// <summary>Sends one request and counts the envelope's load warning before the answer is read.</summary>
    private async Task<NodeAnswer> AskAsync(BaseRequest request, CancellationToken cancellationToken)
    {
        NodeAnswer answer = await _request(request, cancellationToken).ConfigureAwait(false);

        if (string.Equals(answer.Warning, "load", StringComparison.Ordinal)) Interlocked.Increment(ref _loadWarnings);

        return answer;
    }
```

and replace both `_request(` calls in `SubmitAsync` and `ResultOfAsync` with `AskAsync(`.

`XrplClientLedgerSource`:

1. Field `private readonly Func<BaseRequest, CancellationToken, Task<NodeAnswer>> _request;` and `private long _loadWarnings;` with `public long LoadWarnings => Interlocked.Read(ref _loadWarnings);`.
2. The existing internal ctor adapts as the sink's does: `: this(async (sent, token) => new NodeAnswer(await request(sent, token).ConfigureAwait(false), null), pageSize)`; add the `Task<NodeAnswer>` ctor that sets `_pageSize` and `_request`.
3. `SendAsync` returns `new NodeAnswer(response.Raw.ToJsonElement(), response.Warning)`.
4. In `AskAsync` and `AskOrAbsentAsync`, replace `answer = await _request(request, cancellationToken)…` with:

```csharp
            NodeAnswer raw = await _request(request, cancellationToken).ConfigureAwait(false);
            if (string.Equals(raw.Warning, "load", StringComparison.Ordinal)) Interlocked.Increment(ref _loadWarnings);
            answer = raw.Result;
```

5. `IsNodeFailure` takes the caller's token:

```csharp
    /// <summary>
    /// Everything the SDK raises when a request does not come back with an answer this library
    /// can use: a connection that was never made or was lost, a request that timed out, the node's
    /// own error responses (<see cref="RippledException"/>), and a cancellation the caller's token did
    /// not cause — the SDK cancels requests in flight when the network drops (spec I14). The caller's
    /// own cancellation passes through, as does an <see cref="XrplFileException"/> from further in.
    /// </summary>
    private static bool IsNodeFailure(Exception e, CancellationToken cancellationToken) =>
        e is not XrplFileException
        && (e is XrplException or System.TimeoutException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested));
```

and pass `cancellationToken` at both call sites. Update the class summary's list of failures to include "a cancellation the caller did not ask for".

6. In `src/XrplFile.Core/XrplFileWriter.cs`, `PublishAsync`, add as the first catch after the `try`:

```csharp
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            // A cancellation nobody asked for: the SDK cancels requests in flight when the network
            // drops, and a strategy's wait for validation reads sequences through the SDK's typed
            // path, which nothing below renames. Reporting it as cancelled would tell the person
            // they stopped a run they did not stop (spec I14, 4.8).
            throw new XrplFileException(XrplFileError.NodeUnreachable, $"the connection to the node dropped: {e.Message}", inner: e);
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.XrplClientLedgerSourceTests`, then `-class XrplFile.Tests.XrplClientLedgerSinkTests`, then `-class XrplFile.Tests.XrplFileWriterTests`
Expected: all pass.

- [ ] **Step 5: Run the non-integration Core and App.Core suites**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` and `dotnet run --project tests/XrplFile.App.Core.Tests`
Expected: all pass. A failing App.Core test that expected a network drop to read as a cancellation is the behaviour change spec 4.8 names: update it to expect `NodeUnreachable`, citing spec I14 in a comment. The standalone `StopAfterSink` throws `OperationCanceledException(stop.Token)` on a token the caller really cancelled, so it stays a cancellation.

- [ ] **Step 6: Commit**

```bash
git add src/XrplFile.Core tests/XrplFile.Core.Tests
git commit -m "feat(core): count the node's load warnings and name an unsolicited cancellation as an unreachable node"
```

---

### Task 3: The fakes are as strict as a node about LastLedgerSequence, and can warn

**Files:**
- Modify: `tests/XrplFile.TestSupport/FakeLedgerSink.cs`, `tests/XrplFile.TestSupport/FakeLedgerSource.cs`
- Test: `tests/XrplFile.Core.Tests/FakeLedgerSinkTests.cs`

**Interfaces:**
- Consumes: `ILedgerSink.LoadWarnings`, `ILedgerSource.LoadWarnings` (Task 2).
- Produces (FakeLedgerSink): `HashSet<int> WarnsAt`; `long LoadWarnings`; `IReadOnlyList<int> InFlightAtSubmit` (in flight, this one included, when each position was taken); `IReadOnlyList<TimeSpan> SubmittedAt` (elapsed since the fake was built, per position); `tefMAX_LEDGER` for a transaction landing past its `LastLedgerSequence`; `telCAN_NOT_QUEUE` under `QueueEverything` when `LastLedgerSequence < landing + 2`.
- Produces (FakeLedgerSource): `int ListingWarnings` (the next N `LiveTicketsAsync` answers each carry a warning); `long LoadWarnings`; `long LedgerAdvancePerRead` (added to the current ledger before each `CurrentLedgerAsync` answer); `int CurrentLedgerFailures` (the next N `CurrentLedgerAsync` calls fail with `NodeUnreachable`, then it recovers — the one-shot counterpart of `LiveTicketsFailures`).

- [ ] **Step 1: Write the failing tests**

Add to `tests/XrplFile.Core.Tests/FakeLedgerSinkTests.cs` (it already has a wallet/signer; reuse its helpers, or add `private static readonly XrplWallet Wallet = XrplWallet.Generate(); private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);` if absent):

```csharp
    private static Dictionary<string, object> SpendOf(uint ticket, uint lastLedger) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = Wallet.ClassicAddress,
        ["Sequence"] = 0u,
        ["TicketSequence"] = ticket,
        ["LastLedgerSequence"] = lastLedger,
        ["Fee"] = "12",
        ["SigningPubKey"] = Wallet.PublicKey,
    };

    [Fact]
    public async Task A_spend_landing_past_its_last_ledger_is_refused_and_leaves_its_ticket()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        uint ticket = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true)[0];

        SubmitOutcome outcome = await sink.SubmitAsync(SpendOf(ticket, 4000), Signer, TestContext.Current.CancellationToken);

        Assert.Equal("tefMAX_LEDGER", outcome.EngineResult);
        Assert.Contains(ticket, sink.LiveTickets);
    }

    [Fact]
    public async Task A_consumed_ticket_answers_no_ticket_before_max_ledger_as_rippled_checks_them()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        uint ticket = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true)[0];
        await sink.SubmitAsync(SpendOf(ticket, 9000), Signer, TestContext.Current.CancellationToken);

        SubmitOutcome outcome = await sink.SubmitAsync(SpendOf(ticket, 4000), Signer, TestContext.Current.CancellationToken);

        Assert.Equal("tefNO_TICKET", outcome.EngineResult);
    }

    [Fact]
    public async Task A_queue_refuses_a_spend_whose_last_ledger_is_too_close()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger) { QueueEverything = true };
        uint ticket = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true)[0];
        uint landing = (uint)ledger.CurrentLedger;

        SubmitOutcome outcome = await sink.SubmitAsync(SpendOf(ticket, landing + 1), Signer, TestContext.Current.CancellationToken);

        Assert.Equal("telCAN_NOT_QUEUE", outcome.EngineResult);
    }

    [Fact]
    public async Task Scripted_warnings_are_counted_on_the_sink_and_the_source()
    {
        FakeLedgerSource ledger = new() { ListingWarnings = 1 };
        FakeLedgerSink sink = new(ledger);
        sink.WarnsAt.Add(0);
        uint ticket = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true)[0];

        await sink.SubmitAsync(SpendOf(ticket, 9000), Signer, TestContext.Current.CancellationToken);
        await ledger.LiveTicketsAsync(Wallet.ClassicAddress, TestContext.Current.CancellationToken);
        await ledger.LiveTicketsAsync(Wallet.ClassicAddress, TestContext.Current.CancellationToken);

        Assert.Equal(1, ((ILedgerSink)sink).LoadWarnings);
        Assert.Equal(1, ((ILedgerSource)ledger).LoadWarnings);
    }

    [Fact]
    public async Task The_ledger_clock_moves_the_validated_index_on_each_read()
    {
        FakeLedgerSource ledger = new() { CurrentLedger = 100, LedgerAdvancePerRead = 3 };

        long first = await ledger.CurrentLedgerAsync(TestContext.Current.CancellationToken);
        long second = await ledger.CurrentLedgerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(103, first);
        Assert.Equal(106, second);
    }

    [Fact]
    public async Task A_ledger_read_can_fail_once_and_recover()
    {
        FakeLedgerSource ledger = new() { CurrentLedger = 100, CurrentLedgerFailures = 1 };

        XrplFileException failure = await Assert.ThrowsAsync<XrplFileException>(() => ledger.CurrentLedgerAsync(TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, failure.Error);
        Assert.Equal(100, await ledger.CurrentLedgerAsync(TestContext.Current.CancellationToken));
    }
```

`LeaveTickets` puts its mint at ledger `5000 + …`, so `ledger.CurrentLedger` is about 5000 and the spend lands at about 5000: a `LastLedgerSequence` of 4000 is past, 9000 is not.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.FakeLedgerSinkTests`
Expected: build error on `WarnsAt`, `ListingWarnings`, `LedgerAdvancePerRead`.

- [ ] **Step 3: Implement in `FakeLedgerSink`**

Fields and properties:

```csharp
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<int> _inFlightAtSubmit = [];
    private readonly List<TimeSpan> _submittedAt = [];
    private long _loadWarnings;

    /// <summary>Which submissions (by position) are answered with <c>"warning": "load"</c>, as a node nearing a client's rate limit answers.</summary>
    public HashSet<int> WarnsAt { get; } = [];

    public long LoadWarnings => Interlocked.Read(ref _loadWarnings);

    /// <summary>How many submissions were in flight, this one included, when each position was taken.</summary>
    public IReadOnlyList<int> InFlightAtSubmit
    {
        get { lock (_gate) return [.. _inFlightAtSubmit]; }
    }

    /// <summary>When each position was taken, measured from this fake's construction.</summary>
    public IReadOnlyList<TimeSpan> SubmittedAt
    {
        get { lock (_gate) return [.. _submittedAt]; }
    }
```

In `SubmitAsync`, inside the lock after `_inFlight++` and the peak update:

```csharp
            _inFlightAtSubmit.Add(_inFlight);
            _submittedAt.Add(_clock.Elapsed);
```

In `Answer`, first thing inside the lock, after computing `hash`:

```csharp
            if (WarnsAt.Contains(position)) Interlocked.Increment(ref _loadWarnings);

            // The ledger this submission would land in, as the stored record below computes it.
            uint landing = (uint)(firstLedger + position / 100 + _ledgersClosed);
            uint? lastLedger = transaction.TryGetValue("LastLedgerSequence", out object? last) ? Convert.ToUInt32(last) : null;
```

After the `Refusals` check and before `QueueEverything`'s return:

```csharp
            // rippled's TxQ will not queue a transaction that cannot stay at least two ledgers.
            if (QueueEverything && lastLedger is { } queuedUntil && queuedUntil < landing + 2)
            {
                Events.Add("submit");
                return new SubmitOutcome("telCAN_NOT_QUEUE", hash);
            }
```

In the `TicketSequence` branch, replace `if (!LiveTickets.Remove(ticket)) { … }` with a contains-check, then the expiry check, then the removal — rippled checks the ticket (`tefNO_TICKET`, `terPRE_TICKET`) before `LastLedgerSequence`:

```csharp
                if (!LiveTickets.Contains(ticket))
                {
                    return new SubmitOutcome(
                        _consumed.Contains(ticket) || ticket < _nextSequence ? "tefNO_TICKET" : "terPRE_TICKET", hash);
                }

                // Checked after the ticket, as rippled does: a live ticket and a deadline already past.
                if (lastLedger is { } until && landing > until) return new SubmitOutcome("tefMAX_LEDGER", hash);

                LiveTickets.Remove(ticket);
```

In the sequence branch (`else`), before `_nextSequence = …`:

```csharp
                if (lastLedger is { } until && landing > until) return new SubmitOutcome("tefMAX_LEDGER", hash);
```

- [ ] **Step 4: Implement in `FakeLedgerSource`**

```csharp
    private long _loadWarnings;

    public long LoadWarnings => Interlocked.Read(ref _loadWarnings);

    /// <summary>How many of the next <see cref="LiveTicketsAsync"/> answers carry <c>"warning": "load"</c>.</summary>
    public int ListingWarnings
    {
        get { lock (_gate) return _listingWarnings; }
        set { lock (_gate) _listingWarnings = value; }
    }

    private int _listingWarnings;

    /// <summary>
    /// How far the validated ledger moves before each <see cref="CurrentLedgerAsync"/> answer — a
    /// network closing ledgers while the caller works. Zero, the default, keeps the index where the
    /// transactions put it.
    /// </summary>
    public long LedgerAdvancePerRead { get; set; }
```

In `LiveTicketsAsync`, inside the lock after `_liveTicketsCalls++` and before the failure check:

```csharp
            if (_listingWarnings > 0)
            {
                _listingWarnings--;
                Interlocked.Increment(ref _loadWarnings);
            }
```

Add:

```csharp
    /// <summary>
    /// How many of the next <see cref="CurrentLedgerAsync"/> calls fail with
    /// <see cref="XrplFileError.NodeUnreachable"/> before it recovers — a node that drops one ledger
    /// answer. Each failing call takes one off this count.
    /// </summary>
    public int CurrentLedgerFailures
    {
        get { lock (_gate) return _currentLedgerFailures; }
        set { lock (_gate) _currentLedgerFailures = value; }
    }

    private int _currentLedgerFailures;
```

In `CurrentLedgerAsync`, inside the lock after `_currentLedgerCalls++`:

```csharp
            if (_currentLedgerFailures > 0)
            {
                _currentLedgerFailures--;

                return Task.FromException<long>(new XrplFileException(XrplFileError.NodeUnreachable, "fake: ledger did not answer"));
            }

            _currentLedger += LedgerAdvancePerRead;
```

(before the existing `CurrentLedgerFailure` check).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.FakeLedgerSinkTests`
Expected: all pass.

- [ ] **Step 6: Run the non-integration Core and App.Core suites**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` and `dotnet run --project tests/XrplFile.App.Core.Tests`
Expected: all pass (nothing yet sends `LastLedgerSequence`).

- [ ] **Step 7: Commit**

```bash
git add tests/XrplFile.TestSupport tests/XrplFile.Core.Tests/FakeLedgerSinkTests.cs
git commit -m "test(support): fakes enforce LastLedgerSequence as rippled does and can warn of load"
```

---

### Task 4: The back-off arithmetic (I15)

**Files:**
- Create: `src/XrplFile.Core/TidyUpPace.cs`
- Create: `tests/XrplFile.Core.Tests/TidyUpPaceTests.cs`

**Interfaces:**
- Produces: `internal sealed class TidyUpPace(TimeSpan poll)` with `const int StartInFlight = 25`, `const int MaxPauseInPolls = 16`, `int InFlight { get; }`, `TimeSpan Pause { get; }`, `void Strained()`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Xunit;

namespace XrplFile.Tests;

public class TidyUpPaceTests
{
    [Fact]
    public void A_fresh_pace_sends_twenty_five_at_once_and_pauses_one_poll()
    {
        TidyUpPace pace = new(TimeSpan.FromSeconds(1));

        Assert.Equal(25, pace.InFlight);
        Assert.Equal(TimeSpan.FromSeconds(1), pace.Pause);
    }

    [Fact]
    public void Each_strained_round_halves_the_spends_in_flight_down_to_one()
    {
        TidyUpPace pace = new(TimeSpan.FromSeconds(1));
        List<int> inFlight = [];

        for (int round = 0; round < 6; round++)
        {
            pace.Strained();
            inFlight.Add(pace.InFlight);
        }

        Assert.Equal([12, 6, 3, 1, 1, 1], inFlight);
    }

    [Fact]
    public void Each_strained_round_doubles_the_pause_up_to_sixteen_polls()
    {
        TidyUpPace pace = new(TimeSpan.FromSeconds(1));
        List<TimeSpan> pauses = [];

        for (int round = 0; round < 6; round++)
        {
            pace.Strained();
            pauses.Add(pace.Pause);
        }

        Assert.Equal(
            [2, 4, 8, 16, 16, 16],
            pauses.Select(pause => (int)pause.TotalSeconds));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.TidyUpPaceTests`
Expected: build error — `TidyUpPace` does not exist.

- [ ] **Step 3: Implement `src/XrplFile.Core/TidyUpPace.cs`**

```csharp
namespace XrplFile;

/// <summary>
/// How fast the tidy-up goes (spec 4.6, I15): how many spends are in flight at once and how long it
/// pauses between rounds. A round that showed strain halves the first and doubles the second;
/// nothing speeds it up again, because a load warning means load already on the client's IP, which
/// rippled lets decay over a 32-second window.
/// </summary>
internal sealed class TidyUpPace(TimeSpan poll)
{
    /// <summary>Spends in flight at once before any strain: the ticket strategy's own default.</summary>
    public const int StartInFlight = 25;

    /// <summary>The longest pause, in polls: one second to sixteen with the default poll.</summary>
    public const int MaxPauseInPolls = 16;

    private readonly TimeSpan _maxPause = poll * MaxPauseInPolls;

    public int InFlight { get; private set; } = StartInFlight;

    public TimeSpan Pause { get; private set; } = poll;

    /// <summary>Slows down after a round that showed strain: <c>max(1, inFlight / 2)</c> and <c>min(16 × poll, pause × 2)</c>.</summary>
    public void Strained()
    {
        InFlight = Math.Max(1, InFlight / 2);

        TimeSpan doubled = Pause * 2;
        Pause = doubled > _maxPause ? _maxPause : doubled;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.TidyUpPaceTests`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core/TidyUpPace.cs tests/XrplFile.Core.Tests/TidyUpPaceTests.cs
git commit -m "feat(core): the tidy-up's pace halves its spends in flight and doubles its pause under strain"
```

---

### Task 5: Rounds with LastLedgerSequence, the wider passing set, and expiry (I13, I16)

**Files:**
- Modify: `src/XrplFile.Core/LeftoverTickets.cs`
- Test: `tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs`

**Interfaces:**
- Consumes: `TidyUpPace` (Task 4); fakes' `LedgerAdvancePerRead`, `tefMAX_LEDGER` (Task 3); `SubmitOutcome.FollowsUnansweredAttempt` (Task 1).
- Produces: `internal const uint LastLedgerOffset = 10` on `LeftoverTickets`; a private `Spends` class holding the per-ticket bookkeeping that Tasks 6 and 7 extend; `ReleaseAsync`'s signature is unchanged.

This task restructures `ReleaseAsync` around `Spends`. Counts of charged and in-doubt spends come in Task 6, back-off wiring in Task 7; here `TidyUpPace` is created and its `InFlight` and `Pause` are used, but `Strained()` is not yet called.

- [ ] **Step 1: Write the failing tests**

Add to `LeftoverTicketsTests`:

```csharp
    [Theory]
    [InlineData("error:tooBusy")]
    [InlineData("error:noCurrent")]
    [InlineData("error:noNetwork")]
    [InlineData("tefMAX_LEDGER")]
    public async Task A_spend_refused_for_a_passing_reason_is_sent_again_and_released(string refusal)
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Refusals[0] = refusal;
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, release.Released);
        Assert.DoesNotContain(ours[0], sink.LiveTickets);
        Assert.Equal(2, sink.Submitted.Count);
    }

    [Fact]
    public async Task Another_error_answer_is_not_sent_again()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Refusals[0] = "error:invalidTransaction";
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, release.NotReleased);
        Assert.Single(sink.Submitted);
    }

    [Fact]
    public async Task Every_spend_carries_a_last_ledger_ten_past_the_validated_index()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 3, marked: true);
        long validated = ledger.CurrentLedger;
        await using TicketStrategy strategy = Strategy(sink, ledger);

        await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.All(sink.Submitted, tx => Assert.Equal((uint)validated + 10, Convert.ToUInt32(tx["LastLedgerSequence"])));
    }

    [Fact(Timeout = 10_000)]
    public async Task A_queued_spend_that_never_lands_is_sent_again_once_its_last_ledger_is_reached()
    {
        FakeLedgerSource ledger = new() { LedgerAdvancePerRead = 1 };
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);

        // Taken, and then dropped from the queue: it never lands.
        sink.Refusals[0] = "terQUEUED";
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, release.Released);
        Assert.DoesNotContain(ours[0], sink.LiveTickets);

        uint first = Convert.ToUInt32(sink.Submitted[0]["LastLedgerSequence"]);
        uint second = Convert.ToUInt32(sink.Submitted[1]["LastLedgerSequence"]);

        // Not judged expired while the index was one short; judged expired in the round whose index
        // reached it, and resent in the round after, with a fresh deadline (Review Focus 5).
        Assert.Equal(first + 11, second);
        Assert.Equal(2, sink.Submitted.Count);
    }

    [Fact]
    public async Task A_census_the_node_did_not_answer_once_is_tried_again()
    {
        FakeLedgerSource ledger = new() { LiveTicketsFailFromCall = 1, LiveTicketsFailures = 1 };
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, release.Released);
        Assert.Empty(sink.LiveTickets);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_stalled_ledger_never_expires_a_queued_spend()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Refusals[0] = "terQUEUED";
        await using TicketStrategy strategy = Strategy(sink, ledger);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(300));

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(deadline.Token);

        Assert.Equal(1, release.NotReleased);
        Assert.Single(sink.Submitted);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_round_whose_index_cannot_be_read_sends_nothing_and_does_not_throw()
    {
        FakeLedgerSource ledger = new()
        {
            CurrentLedgerFailure = new XrplFileException(XrplFileError.NodeUnreachable, "fake: ledger did not answer"),
            CurrentLedgerFailsFromCall = 1,
        };
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using TicketStrategy strategy = Strategy(sink, ledger);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(300));

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(deadline.Token);

        Assert.Equal(2, release.NotReleased);
        Assert.Empty(sink.Submitted);
    }
```

Note on `A_queued_spend_that_never_lands…`: `LeaveTickets` leaves the index at V = 5000, and the fake advances it by one before each read, so round r reads V+r. Round 1 sends with LLS V+11 (`first`). A round sends, then settles: round 11 reads V+11, has nothing to send (the spend is waiting), and judges it expired in its settle step; round 12 reads V+12 and resends with LLS V+22 = `first + 11`. Nothing else in the tidy-up calls `CurrentLedgerAsync`; if the implementation does, the equality tells you.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.LeftoverTicketsTests`
Expected: the new tests fail (no `LastLedgerSequence` key, passing set too narrow, queued spend never resent); the existing ones pass.

- [ ] **Step 3: Implement**

Replace everything in `LeftoverTickets.cs` from the `MaxInFlight` constant through the end of the old `Passing` method (inclusive) with the code below — it brings its own `Passing` and `Busy`. `FindAsync`, `IsOurMint`, `Reached` and `Spend` stay; `Spend` gains a `lastLedger` parameter.

```csharp
    /// <summary>
    /// How many times one ticket's spend is submitted before a passing refusal is taken as final. A
    /// burst of spends is what a loaded node answers with <c>telCAN_NOT_QUEUE</c>; five rounds give
    /// its queue room to drain without resending a hopeless spend until the deadline. A submission is
    /// one sink call; the sink's own retransmissions of the same blob are not further submissions.
    /// </summary>
    private const int MaxSpendAttempts = 5;

    /// <summary>
    /// How far past the validated ledger a spend may still apply (spec I16): the node's queue refuses
    /// anything closer than two past the open ledger, and ten — some thirty-five seconds — gives a
    /// spend three chances inside the two-minute deadline.
    /// </summary>
    internal const uint LastLedgerOffset = 10;

    /// <summary>
    /// Finds this library's tickets afresh from the validated ledger, spends each on an AccountSet
    /// that sets nothing, and waits for the outcomes (spec 4.6). Works in rounds — read the validated
    /// index, send, list once, settle, pause — and sends again a spend the node refused for a passing
    /// reason or one whose <c>LastLedgerSequence</c> expired. Cancellation is the deadline: it ends
    /// the wait and leaves every unsettled ticket counted as not released.
    /// </summary>
    public static async Task<TicketRelease> ReleaseAsync(
        ILedgerSink sink, ILedgerSource source, ITransactionSigner signer, long feeDrops, TimeSpan poll, CancellationToken cancellationToken)
    {
        string account = signer.Account;
        Spends? spends = null;
        TidyUpPace pace = new(poll);

        try
        {
            while (true)
            {
                try
                {
                    if (spends is null)
                    {
                        // Inside the round, so one dropped answer before anything was sent is retried
                        // rather than leaving every ticket locked (spec 4.6).
                        TicketCensus census = await FindAsync(source, account, null, cancellationToken).ConfigureAwait(false);

                        if (census.Ours.Count == 0) return TicketRelease.None;

                        spends = new Spends(census.Ours);
                    }

                    // Before the listing, so the listing comes from this ledger or a later one, which
                    // is what makes a spend's expiry provable (spec 4.6).
                    long validated = await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
                    uint lastLedger = (uint)(validated + LastLedgerOffset);

                    await SendRoundAsync(sink, signer, account, feeDrops, lastLedger, pace.InFlight, spends, cancellationToken)
                        .ConfigureAwait(false);
                    await SettleRoundAsync(source, account, validated, spends, cancellationToken).ConfigureAwait(false);

                    if (spends.Finished) break;
                }
                catch (XrplFileException failure) when (failure.Error == XrplFileError.NodeUnreachable)
                {
                    // A node that could not answer this round has decided nothing: what is settled
                    // stays settled, and the next round reads it again instead of losing every count.
                    // Anything else — a signer refusing, a locked vault — still ends the tidy-up.
                }

                await Task.Delay(pace.Pause, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The deadline, or the caller's own cancellation: the counts stand as they are, and
            // everything not yet settled is not released (spec 4.6).
        }

        return spends?.Result() ?? TicketRelease.None;
    }

    /// <summary>
    /// Sends one spend for every ticket still to be sent, at most <paramref name="inFlight"/> at once,
    /// each carrying <paramref name="lastLedger"/>, and records every answer against its ticket as it
    /// arrives, so a round cut short keeps what it already heard (spec 4.6).
    /// </summary>
    private static async Task SendRoundAsync(
        ILedgerSink sink, ITransactionSigner signer, string account, long feeDrops, uint lastLedger, int inFlight,
        Spends spends, CancellationToken cancellationToken)
    {
        uint[] round = spends.ToSend();

        if (round.Length == 0) return;

        await Parallel.ForEachAsync(
            round,
            new ParallelOptions { MaxDegreeOfParallelism = inFlight, CancellationToken = cancellationToken },
            async (ticket, token) =>
            {
                spends.Opened(ticket, lastLedger);
                SubmitOutcome? outcome;

                try
                {
                    outcome = await sink
                        .SubmitAsync(Spend(account, signer.PublicKey, ticket, feeDrops, lastLedger), signer, token).ConfigureAwait(false);
                }
                catch (XrplFileException failure) when (failure.Error == XrplFileError.NodeUnreachable)
                {
                    // Null: the node could not be reached, which says nothing about the ticket.
                    outcome = null;
                }

                spends.Answered(ticket, lastLedger, outcome);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the listing once and settles what it can: a waiting spend whose ticket left the ledger
    /// is asked what it did, a spend whose ticket is still listed past its deadline has expired, and
    /// any other ticket the listing no longer shows is gone.
    /// </summary>
    private static async Task SettleRoundAsync(
        ILedgerSource source, string account, long validated, Spends spends, CancellationToken cancellationToken)
    {
        // One listing a round rather than one lookup per ticket: a public node sees one
        // account_objects a round, not two hundred and fifty tx requests.
        IReadOnlyList<LiveTicket> still = await source.LiveTicketsAsync(account, cancellationToken).ConfigureAwait(false);
        HashSet<uint> listed = [.. still.Select(ticket => ticket.Sequence)];

        foreach ((uint ticket, string hash) in spends.WaitingNotIn(listed))
        {
            LedgerTransaction? tx = await source.TransactionAsync(hash, cancellationToken).ConfigureAwait(false);
            spends.Settled(ticket, tx);
        }

        spends.Listed(listed, validated);
    }

    /// <summary>
    /// A refusal before any ledger saw the spend, for a reason that can pass: a <c>tel</c>; a
    /// <c>ter</c> other than <c>terQUEUED</c>; <c>tefMAX_LEDGER</c>, which rippled checks only after
    /// finding the ticket live; or an error answer naming a passing server condition (spec I13). The
    /// ticket is intact and no fee was claimed.
    /// </summary>
    private static bool Passing(string engineResult) =>
        engineResult.StartsWith("tel", StringComparison.Ordinal)
        || (engineResult.StartsWith("ter", StringComparison.Ordinal)
            && !string.Equals(engineResult, "terQUEUED", StringComparison.Ordinal))
        || string.Equals(engineResult, "tefMAX_LEDGER", StringComparison.Ordinal)
        || Busy(engineResult);

    /// <summary>The universal errors that name a passing server condition (spec I13).</summary>
    private static bool Busy(string engineResult) =>
        engineResult is "error:tooBusy" or "error:noCurrent" or "error:noNetwork";
```

Keep `Reached` as it is. Change `Spend`:

```csharp
    /// <summary>
    /// An AccountSet that sets nothing, riding one ticket and applying no later than
    /// <paramref name="lastLedger"/>: the cheapest transaction that frees its owner reserve.
    /// </summary>
    private static Dictionary<string, object> Spend(string account, string publicKey, uint ticket, long feeDrops, uint lastLedger) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = account,
        ["Sequence"] = 0u,
        ["TicketSequence"] = ticket,
        ["LastLedgerSequence"] = lastLedger,
        ["Fee"] = feeDrops.ToString(CultureInfo.InvariantCulture),
        ["SigningPubKey"] = publicKey,
    };
```

Add the bookkeeping class at the end of `LeftoverTickets`:

```csharp
    /// <summary>
    /// The tidy-up's bookkeeping for one call, per ticket (spec 4.6). Guarded: a round's answers
    /// arrive in parallel.
    /// </summary>
    private sealed class Spends(IReadOnlyList<uint> ours)
    {
        private readonly Lock _gate = new();
        private readonly HashSet<uint> _toSend = [.. ours];
        private readonly Dictionary<uint, int> _submissions = [];
        private readonly Dictionary<uint, (string Hash, uint LastLedger)> _waiting = [];
        private readonly HashSet<uint> _gone = [];
        private readonly HashSet<uint> _released = [];

        /// <summary>Nothing left to send and nothing waiting: every ticket is settled or given up on.</summary>
        public bool Finished
        {
            get { lock (_gate) return _toSend.Count == 0 && _waiting.Count == 0; }
        }

        public uint[] ToSend()
        {
            lock (_gate) return [.. _toSend.Order()];
        }

        /// <summary>A submission is about to be sent: it counts against the ticket's five.</summary>
        public void Opened(uint ticket, uint lastLedger)
        {
            lock (_gate) _submissions[ticket] = _submissions.GetValueOrDefault(ticket) + 1;
        }

        /// <summary>
        /// Sorts one answer: a spend that reached a ledger — or was refused only after an earlier
        /// copy of the same blob went unanswered — waits for the listing under its hash; a passing
        /// refusal, or a node that could not be reached, stays to be sent again until the ticket has
        /// had its five submissions; anything else is not sent again.
        /// </summary>
        public void Answered(uint ticket, uint lastLedger, SubmitOutcome? outcome)
        {
            lock (_gate)
            {
                bool spent = _submissions[ticket] >= MaxSpendAttempts;

                if (outcome is not null && (Reached(outcome.EngineResult) || outcome.FollowsUnansweredAttempt))
                {
                    // Either may still apply: a new spend would only compete with it, and the listing
                    // or its expiry says which it was (spec 4.6).
                    _toSend.Remove(ticket);
                    _waiting[ticket] = (outcome.TxHash, lastLedger);

                    return;
                }

                if (outcome is null || Passing(outcome.EngineResult))
                {
                    if (spent) _toSend.Remove(ticket);

                    return;
                }

                // tefNO_TICKET is not a verdict: the node may have seen the ticket consumed in an open
                // ledger by a transaction that never validates, which leaves it live. Only a listing
                // settles such a ticket, and it is never sent again (I5). A tem, or any other answer,
                // would be refused the same way every time.
                _toSend.Remove(ticket);
            }
        }

        /// <summary>The waiting spends whose ticket this listing no longer shows: each is asked what it did.</summary>
        public IReadOnlyList<(uint Ticket, string Hash)> WaitingNotIn(IReadOnlySet<uint> listed)
        {
            lock (_gate)
            {
                return [.. _waiting.Where(pair => !listed.Contains(pair.Key)).Select(pair => (pair.Key, pair.Value.Hash))];
            }
        }

        /// <summary>
        /// A waiting spend whose ticket left the ledger: nothing short of its own validated tesSUCCESS
        /// is a release; a ticket that left any other way — a tec on the spend, a straggler first —
        /// is gone, and its reserve is free either way.
        /// </summary>
        public void Settled(uint ticket, LedgerTransaction? tx)
        {
            lock (_gate)
            {
                _waiting.Remove(ticket);

                if (tx is { Validated: true, Succeeded: true }) _released.Add(ticket);
                else _gone.Add(ticket);
            }
        }

        /// <summary>
        /// Applies one listing read at or after <paramref name="validated"/>. A waiting spend whose
        /// ticket is still listed and whose <c>LastLedgerSequence</c> that index has reached has
        /// expired: it never applied and cannot, so its ticket goes back to be sent again while
        /// submissions remain (spec I16). A ticket the listing no longer shows and whose own spend is
        /// not waiting is gone.
        /// </summary>
        public void Listed(IReadOnlySet<uint> listed, long validated)
        {
            lock (_gate)
            {
                foreach (uint ticket in ours)
                {
                    if (listed.Contains(ticket))
                    {
                        if (_waiting.TryGetValue(ticket, out (string Hash, uint LastLedger) waiting) && validated >= waiting.LastLedger)
                        {
                            _waiting.Remove(ticket);
                            if (_submissions.GetValueOrDefault(ticket) < MaxSpendAttempts) _toSend.Add(ticket);
                        }

                        continue;
                    }

                    if (_released.Contains(ticket) || _waiting.ContainsKey(ticket)) continue;

                    _gone.Add(ticket);
                    _toSend.Remove(ticket);
                }
            }
        }

        public TicketRelease Result()
        {
            lock (_gate)
            {
                int alreadyGone = _gone.Count(ticket => !_released.Contains(ticket));

                return new TicketRelease(_released.Count, alreadyGone, ours.Count - _released.Count - alreadyGone);
            }
        }
    }
```

Remove the `using System.Collections.Concurrent;` if nothing else in the file uses it.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.LeftoverTicketsTests`
Expected: all pass, old and new.

- [ ] **Step 5: Run the non-integration Core suite**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/XrplFile.Core/LeftoverTickets.cs tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs
git commit -m "feat(core): tidy-up spends carry LastLedgerSequence and are sent again once it expires or the node is busy"
```

---

### Task 6: Charged and in-doubt spends (I17)

**Files:**
- Modify: `src/XrplFile.Core/TicketRelease.cs`, `src/XrplFile.Core/LeftoverTickets.cs` (class `Spends`)
- Test: `tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs`

**Interfaces:**
- Consumes: `Spends` (Task 5).
- Produces: `TicketRelease.Charged { get; init; }` and `TicketRelease.InDoubt { get; init; }` (int).

`TicketRelease` is a record, so its equality now includes `Charged` and `InDoubt`. Existing assertions of the form `Assert.Equal(new TicketRelease(a, b, c), release)` must name the expected cost too; Step 1 lists every one.

- [ ] **Step 1: Update the existing assertions and write the failing tests**

In `LeftoverTicketsTests`, change these expectations (left: test name; right: new expected value):

| Test | Expected |
|---|---|
| `The_tidy_up_spends_this_librarys_tickets_on_transactions_that_do_nothing` | `new TicketRelease(3, 0, 0) { Charged = 3 }` |
| `A_ticket_the_node_calls_gone_but_the_ledger_still_lists_is_not_released` | `new TicketRelease(1, 0, 1) { Charged = 1 }` |
| `A_ticket_the_node_calls_gone_and_the_ledger_no_longer_lists_is_already_gone` | `new TicketRelease(0, 1, 0)` |
| `A_spend_that_validates_as_failed_is_not_a_release_but_the_ticket_is_gone` | `new TicketRelease(0, 1, 0) { Charged = 1 }` |
| `A_spend_the_node_refuses_once_is_sent_again_and_released` | `new TicketRelease(1, 0, 0) { Charged = 1 }` |
| `A_spend_the_node_refuses_leaves_the_ticket_not_released` | `new TicketRelease(0, 0, 1)` |
| `A_malformed_spend_is_not_sent_again` | `new TicketRelease(0, 0, 1)` |
| `A_round_the_node_could_not_answer_is_skipped_not_fatal` | `new TicketRelease(2, 0, 0) { Charged = 2 }` |
| `A_spend_the_node_could_not_take_counts_as_an_attempt` | `new TicketRelease(0, 0, 1) { InDoubt = 1 }` |
| `The_tidy_up_stops_at_its_deadline_and_counts_what_did_not_settle` | `new TicketRelease(0, 0, 2) { InDoubt = 2 }` |
| `A_sequence_strategy_tidies_up_too_when_it_can_read_the_ledger` | `new TicketRelease(2, 0, 0) { Charged = 2 }` |

Add:

```csharp
    [Fact(Timeout = 10_000)]
    public async Task An_in_doubt_spend_that_expires_is_no_longer_in_doubt()
    {
        FakeLedgerSource ledger = new() { LedgerAdvancePerRead = 1 };
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);

        // Taken, never lands; every later submission refused for good.
        sink.Refusals[0] = "terQUEUED";
        sink.Refusals[1] = "temMALFORMED";
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(0, 0, 1), release);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_refusal_after_an_unanswered_transmission_keeps_the_spend_in_doubt()
    {
        FakeLedgerSource ledger = new();
        UnansweredFirstSink sink = new(new FakeLedgerSink(ledger));
        sink.Inner.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Inner.Refusals[0] = "temMALFORMED";
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // The ledger index never moves here, so the waiting spend neither lands nor expires.
        deadline.CancelAfter(TimeSpan.FromMilliseconds(300));

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(deadline.Token);

        Assert.Equal(new TicketRelease(0, 0, 1) { InDoubt = 1 }, release);
        Assert.Single(sink.Inner.Submitted);
    }

    [Fact]
    public async Task A_refusal_after_an_unanswered_transmission_whose_earlier_copy_landed_is_released()
    {
        FakeLedgerSource ledger = new();
        EarlierCopyLandedSink sink = new(new FakeLedgerSink(ledger));
        sink.Inner.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // The spend waited under its hash; when the ticket left the listing, that hash was looked up
        // and found validated with tesSUCCESS (spec 4.6).
        Assert.Equal(new TicketRelease(1, 0, 0) { Charged = 1 }, release);
    }

    [Fact]
    public async Task An_unanswered_spend_whose_ticket_somebody_else_consumed_is_gone_and_in_doubt()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Throws[0] = new XrplFileException(XrplFileError.NodeUnreachable, "fake: submit did not answer");
        ConsumingSink consuming = new(sink, ledger, ours[0]);
        await using TicketStrategy strategy = new(consuming, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(0, 1, 0) { InDoubt = 1 }, release);
    }

    [Fact(Timeout = 10_000)]
    public async Task Answers_a_cancelled_round_already_heard_stay_counted()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink fake = new(ledger);
        fake.LeaveTickets(Wallet.ClassicAddress, 30, marked: true);
        for (int position = 0; position < 25; position++) fake.Refusals[position] = "telCAN_NOT_QUEUE";
        HoldingSink sink = new(fake, answered: 25, held: 5);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<TicketRelease> running = strategy.ReleaseLeftoverTicketsAsync(deadline.Token);
        await sink.Heard.Task.WaitAsync(TestContext.Current.CancellationToken);
        await sink.Holding.Task.WaitAsync(TestContext.Current.CancellationToken);
        await deadline.CancelAsync();

        TicketRelease release = await running;

        // Twenty-five refusals were heard and closed their submissions; five were cut off in flight
        // and stay open. A tidy-up that recorded answers only at the end of the round would have
        // lost the twenty-five and reported thirty in doubt.
        Assert.Equal(5, release.InDoubt);
        Assert.Equal(0, release.Charged);
        Assert.Equal(30, release.NotReleased);
    }

    /// <summary>
    /// Forwards the first <paramref name="answered"/> submissions and signals once all have answered;
    /// holds the next <paramref name="held"/> until the call is cancelled, and signals once all are held.
    /// </summary>
    private sealed class HoldingSink(FakeLedgerSink inner, int answered, int held) : ILedgerSink
    {
        private int _calls;
        private int _answers;
        private int _holding;

        public TaskCompletionSource Heard { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) > answered)
            {
                if (Interlocked.Increment(ref _holding) == held) Holding.TrySetResult();

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            SubmitOutcome outcome = await inner.SubmitAsync(transaction, signer, cancellationToken);

            if (Interlocked.Increment(ref _answers) == answered) Heard.TrySetResult();

            return outcome;
        }
    }

    /// <summary>
    /// Sends through to the fake, where the spend lands, and then reports what a node that had lost
    /// the first transmission's answer would: a refusal of the second copy, flagged.
    /// </summary>
    private sealed class EarlierCopyLandedSink(FakeLedgerSink inner) : ILedgerSink
    {
        public FakeLedgerSink Inner => inner;

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            SubmitOutcome landed = await inner.SubmitAsync(transaction, signer, cancellationToken);
            return new SubmitOutcome("telCAN_NOT_QUEUE", landed.TxHash) { FollowsUnansweredAttempt = true };
        }
    }

    /// <summary>The first transmission of every submission goes unanswered; the sink then reports the node's answer to the second, flagged.</summary>
    private sealed class UnansweredFirstSink(FakeLedgerSink inner) : ILedgerSink
    {
        public FakeLedgerSink Inner => inner;

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            SubmitOutcome outcome = await inner.SubmitAsync(transaction, signer, cancellationToken);
            return outcome with { FollowsUnansweredAttempt = !Reached(outcome.EngineResult) };
        }

        private static bool Reached(string result) =>
            result.StartsWith("tes", StringComparison.Ordinal) || result.StartsWith("tec", StringComparison.Ordinal) || result == "terQUEUED";
    }

    /// <summary>Lets another transaction consume one ticket the moment the tidy-up's own submission for it fails to answer.</summary>
    private sealed class ConsumingSink(FakeLedgerSink inner, FakeLedgerSource ledger, uint ticket) : ILedgerSink
    {
        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            try
            {
                return await inner.SubmitAsync(transaction, signer, cancellationToken);
            }
            catch (XrplFileException)
            {
                inner.LiveTickets.Remove(ticket);
                ledger.SetLiveTickets((string)transaction["Account"], [.. inner.LiveTickets.Order().Select(live => new LiveTicket(live, FakeLedgerSink.UnknownCreator))]);
                throw;
            }
        }
    }
```

In `An_unanswered_spend_whose_ticket_somebody_else_consumed…`, `Throws[0]` fires on every attempt (a throw takes no position), but after the first round the listing no longer shows the ticket, so it is gone and never sent again. In `Answers_a_cancelled_round…` nothing depends on timing: the test cancels only once twenty-five answers are in and five submissions are held; a body whose `SubmitAsync` already returned records its answer before `Parallel.ForEachAsync` observes the cancellation.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.LeftoverTicketsTests`
Expected: build error — `Charged`, `InDoubt` do not exist.

- [ ] **Step 3: Implement `TicketRelease`**

Add inside the record, and extend its `<remarks>`:

```csharp
    /// <summary>
    /// Tickets one of whose spends validated, with tesSUCCESS or a tec: a fee was certainly claimed
    /// (spec 4.6, I17). Every released ticket is charged; an already-gone one is when its own spend's
    /// tec consumed it. At most one fee per ticket, however often its spend was sent.
    /// </summary>
    public int Charged { get; init; }

    /// <summary>
    /// Tickets not charged with a submission still open: taken by the node and not settled, never
    /// answered, cancelled in flight, or refused only after an unanswered transmission of the same
    /// blob. Such a spend may still apply, and claim its fee, until its LastLedgerSequence passes.
    /// </summary>
    public int InDoubt { get; init; }
```

- [ ] **Step 4: Implement in `Spends`**

Add fields:

```csharp
        /// <summary>Per ticket: the LastLedgerSequence of every submission not yet shown unable to apply.</summary>
        private readonly Dictionary<uint, List<uint>> _open = [];
        private readonly HashSet<uint> _charged = [];

        private List<uint> Open(uint ticket)
        {
            if (!_open.TryGetValue(ticket, out List<uint>? open))
            {
                open = [];
                _open[ticket] = open;
            }

            return open;
        }
```

`Opened` also opens the submission: `Open(ticket).Add(lastLedger);` inside the lock.

`Answered`: a refusal that does not wait closes its submission. Insert right after the waiting branch's `return;` block, before the `outcome is null || Passing(…)` check:

```csharp
                // Refused before any ledger saw it: this submission can never apply. Unanswered or
                // cancelled stays open, and a refusal after an unanswered transmission waited above.
                if (outcome is not null) Open(ticket).Remove(lastLedger);
```

`Settled`: a validated spend is charged and closes everything for the ticket; one that did not validate closes only itself, since the ticket was consumed by something else in a validated ledger:

```csharp
            lock (_gate)
            {
                (string Hash, uint LastLedger) waiting = _waiting[ticket];
                _waiting.Remove(ticket);

                if (tx is { Validated: true })
                {
                    _charged.Add(ticket);
                    Open(ticket).Clear();
                }
                else
                {
                    Open(ticket).Remove(waiting.LastLedger);
                }

                if (tx is { Validated: true, Succeeded: true }) _released.Add(ticket);
                else _gone.Add(ticket);
            }
```

`Listed`: for a listed ticket, before the waiting check, close every open submission whose deadline the index has reached:

```csharp
                    if (listed.Contains(ticket))
                    {
                        Open(ticket).RemoveAll(lastLedger => validated >= lastLedger);
                        …existing waiting/expiry code…
                        continue;
                    }
```

`Result`:

```csharp
            lock (_gate)
            {
                int alreadyGone = _gone.Count(ticket => !_released.Contains(ticket));
                int inDoubt = ours.Count(ticket => !_charged.Contains(ticket) && _open.TryGetValue(ticket, out List<uint>? open) && open.Count > 0);

                return new TicketRelease(_released.Count, alreadyGone, ours.Count - _released.Count - alreadyGone)
                {
                    Charged = _charged.Count,
                    InDoubt = inDoubt,
                };
            }
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.LeftoverTicketsTests`
Expected: all pass.

- [ ] **Step 6: Run the non-integration Core suite**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: all pass. A `PublicationSessionTests` assertion comparing a whole `TicketRelease` would need the same treatment as Step 1.

- [ ] **Step 7: Commit**

```bash
git add src/XrplFile.Core/TicketRelease.cs src/XrplFile.Core/LeftoverTickets.cs tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs
git commit -m "feat(core): the tidy-up counts the spends that claimed a fee and the ones that still may"
```

---

### Task 7: Back off on strain (I15)

**Files:**
- Modify: `src/XrplFile.Core/LeftoverTickets.cs`
- Test: `tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs`

**Interfaces:**
- Consumes: `ILedgerSink.LoadWarnings`, `ILedgerSource.LoadWarnings` (Task 2); `FakeLedgerSink.WarnsAt`, `InFlightAtSubmit`, `SubmittedAt`, `FakeLedgerSource.ListingWarnings` (Task 3); `TidyUpPace` (Task 4).

- [ ] **Step 1: Write the failing tests**

```csharp
    private static TicketStrategy PacedStrategy(FakeLedgerSink sink, FakeLedgerSource ledger, TimeSpan poll) =>
        new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = poll });

    /// <summary>The most submissions in flight at once among positions [from, to).</summary>
    private static int PeakBetween(FakeLedgerSink sink, int from, int to) => sink.InFlightAtSubmit.Skip(from).Take(to - from).Max();

    [Fact(Timeout = 20_000)]
    public async Task A_load_warning_on_a_submit_halves_the_next_rounds_spends_in_flight_and_doubles_its_pause()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger) { SubmitDelay = TimeSpan.FromMilliseconds(5) };
        sink.LeaveTickets(Wallet.ClassicAddress, 60, marked: true);
        for (int position = 0; position < 60; position++) sink.Refusals[position] = "telCAN_NOT_QUEUE";
        sink.WarnsAt.Add(0);
        TimeSpan poll = TimeSpan.FromMilliseconds(100);
        await using TicketStrategy strategy = PacedStrategy(sink, ledger, poll);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(60, release.Released);
        Assert.True(PeakBetween(sink, 60, 120) <= 12, $"round two had {PeakBetween(sink, 60, 120)} in flight");

        // The pause before round two is two polls. A timer may fire up to a tick early on Windows, so
        // the check is one and a half polls: still more than an undoubled pause could give.
        TimeSpan gap = sink.SubmittedAt[60] - sink.SubmittedAt[59];
        Assert.True(gap >= 1.5 * poll, $"round two started {gap} after round one");
    }

    [Fact(Timeout = 20_000)]
    public async Task A_load_warning_on_the_listing_backs_off_too()
    {
        // The census is the first listing and comes before any round; the second is round one's.
        FakeLedgerSource ledger = new() { ListingWarnings = 2 };
        FakeLedgerSink sink = new(ledger) { SubmitDelay = TimeSpan.FromMilliseconds(5) };
        sink.LeaveTickets(Wallet.ClassicAddress, 60, marked: true);
        for (int position = 0; position < 60; position++) sink.Refusals[position] = "telCAN_NOT_QUEUE";
        await using TicketStrategy strategy = PacedStrategy(sink, ledger, TimeSpan.FromMilliseconds(1));

        await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.True(PeakBetween(sink, 60, 120) <= 12);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("error:tooBusy")]
    [InlineData("listing")]
    [InlineData("index")]
    [InlineData("submission")]
    public async Task A_busy_answer_or_an_unreachable_node_backs_off_too(string strain)
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink fake = new(ledger) { SubmitDelay = TimeSpan.FromMilliseconds(5) };
        fake.LeaveTickets(Wallet.ClassicAddress, 60, marked: true);
        for (int position = 0; position < 60; position++) fake.Refusals[position] = "telCAN_NOT_QUEUE";
        ILedgerSink sink = fake;

        // The fake's position at which the round after the strained one begins.
        int afterStrain = 60;

        switch (strain)
        {
            case "listing":
                // The census is listing 1; round one's is listing 2.
                ledger.LiveTicketsFailFromCall = 2;
                ledger.LiveTicketsFailures = 1;
                break;
            case "index":
                // Round one cannot read the index and sends nothing; round two sends all sixty.
                ledger.CurrentLedgerFailures = 1;
                afterStrain = 0;
                break;
            case "submission":
                // The first submission never reaches the fake, so round one takes positions 0..58.
                sink = new FirstCallUnreachableSink(fake);
                afterStrain = 59;
                break;
            default:
                fake.Refusals[0] = strain;
                break;
        }

        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });

        await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.True(PeakBetween(fake, afterStrain, afterStrain + 60) <= 12);
    }

    /// <summary>Fails its first submission as an unreachable node, before the fake sees it; forwards every other.</summary>
    private sealed class FirstCallUnreachableSink(ILedgerSink inner) : ILedgerSink
    {
        private int _calls;

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1
                ? Task.FromException<SubmitOutcome>(new XrplFileException(XrplFileError.NodeUnreachable, "fake: the first submit did not answer"))
                : inner.SubmitAsync(transaction, signer, cancellationToken);
    }

    [Fact(Timeout = 20_000)]
    public async Task A_round_without_strain_changes_nothing()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger) { SubmitDelay = TimeSpan.FromMilliseconds(5) };
        sink.LeaveTickets(Wallet.ClassicAddress, 60, marked: true);
        for (int position = 0; position < 60; position++) sink.Refusals[position] = "telCAN_NOT_QUEUE";
        await using TicketStrategy strategy = PacedStrategy(sink, ledger, TimeSpan.FromMilliseconds(1));

        await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.True(PeakBetween(sink, 60, 120) > 12, "a round without strain slowed down");
    }

    [Fact(Timeout = 30_000)]
    public async Task Warnings_on_every_answer_slow_the_tidy_up_to_its_floor_and_it_still_finishes()
    {
        FakeLedgerSource ledger = new() { ListingWarnings = int.MaxValue };
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 4, marked: true);
        // Four rounds of four refusals: the fifth round, every ticket's last submission, runs at the
        // floor of one in flight after four strained rounds (25, 12, 6, 3, 1).
        for (int position = 0; position < 16; position++) sink.Refusals[position] = "telCAN_NOT_QUEUE";
        for (int position = 0; position < 20; position++) sink.WarnsAt.Add(position);
        await using TicketStrategy strategy = PacedStrategy(sink, ledger, TimeSpan.FromMilliseconds(2));

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, release.Released);
        Assert.Equal(1, sink.InFlightAtSubmit[^1]);
    }
```

In `A_busy_answer_or_an_unreachable_node…`: in the listing case the second listing (after round one's sends) fails, so round one settles nothing and round two resends the same sixty at positions 60..119; in the index case round one sends nothing, so round two is positions 0..59; in the submission case round one's other fifty-nine take positions 0..58 and round two starts at 59.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.LeftoverTicketsTests`
Expected: the four back-off tests fail (round two runs 25 in flight); `A_round_without_strain_changes_nothing` passes.

- [ ] **Step 3: Implement**

`SendRoundAsync` returns whether its round showed strain:

```csharp
    /// <returns>True when any answer was a busy error answer or the node could not be reached (spec 4.6).</returns>
    private static async Task<bool> SendRoundAsync(…same parameters…)
    {
        uint[] round = spends.ToSend();

        if (round.Length == 0) return false;

        int strained = 0;

        await Parallel.ForEachAsync(…same options…, async (ticket, token) =>
            {
                …same as before, then before spends.Answered:
                if (outcome is null || Busy(outcome.EngineResult)) Interlocked.Exchange(ref strained, 1);

                spends.Answered(ticket, lastLedger, outcome);
            }).ConfigureAwait(false);

        return strained != 0;
    }
```

`ReleaseAsync`'s loop:

```csharp
            while (true)
            {
                long warningsBefore = Warnings(sink, source);
                bool strained = false;

                try
                {
                    long validated = await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
                    uint lastLedger = (uint)(validated + LastLedgerOffset);

                    strained = await SendRoundAsync(sink, signer, account, feeDrops, lastLedger, pace.InFlight, spends, cancellationToken)
                        .ConfigureAwait(false);
                    await SettleRoundAsync(source, account, validated, spends, cancellationToken).ConfigureAwait(false);

                    if (spends.Finished) break;
                }
                catch (XrplFileException failure) when (failure.Error == XrplFileError.NodeUnreachable)
                {
                    // …existing comment…
                    strained = true;
                }

                // A warning anywhere in the round — a submit's answer, the listing, a lookup — counts:
                // rippled attaches it to whichever answer comes next, at most once a second (spec I15).
                if (strained || Warnings(sink, source) > warningsBefore) pace.Strained();

                await Task.Delay(pace.Pause, cancellationToken).ConfigureAwait(false);
            }
```

and

```csharp
    /// <summary>Every load warning the node has given this client so far, through either half of the connection.</summary>
    private static long Warnings(ILedgerSink sink, ILedgerSource source) => sink.LoadWarnings + source.LoadWarnings;
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.LeftoverTicketsTests`
Expected: all pass. Run the class twice more to check the timing tests are stable.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core/LeftoverTickets.cs tests/XrplFile.Core.Tests/LeftoverTicketsTests.cs
git commit -m "feat(core): the tidy-up slows down when the node warns of load, answers busy or cannot be reached"
```

---

### Task 8: A strategy says how many mints it made (I18)

**Files:**
- Modify: `src/XrplFile.Core/ISubmissionStrategy.cs`, `src/XrplFile.Core/TicketStrategy.cs`, `src/XrplFile.Core/SequenceStrategy.cs`
- Modify (forward the member): `tests/XrplFile.Core.Tests/PublicationSessionTests.cs` (`ScriptedReleaseStrategy`, `LedgerClosingStrategy`), `tests/XrplFile.Core.Tests/XrplFileWriterTests.cs` (`ProbingStrategy`)
- Test: `tests/XrplFile.Core.Tests/TicketStrategyTests.cs`, `SequenceStrategyTests.cs`

**Interfaces:**
- Produces: `int ISubmissionStrategy.TicketMints { get; }` (no default).

- [ ] **Step 1: Write the failing tests**

Read the top of `TicketStrategyTests.cs` for its existing helpers (a strategy factory, memo builder) and use them. The tests:

```csharp
    [Fact]
    public async Task A_strategy_counts_the_mints_it_made()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            TicketsPerBatch = 100, SweepPoll = TimeSpan.FromMilliseconds(1), ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

        await strategy.SubmitAllAsync(Memos(150), null, TestContext.Current.CancellationToken);

        Assert.Equal(2, strategy.TicketMints);
    }

    [Fact]
    public async Task Picked_up_tickets_mean_fewer_mints()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 100, marked: true);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            TicketsPerBatch = 100, SweepPoll = TimeSpan.FromMilliseconds(1), ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

        await strategy.SubmitAllAsync(Memos(150), null, TestContext.Current.CancellationToken);

        Assert.Equal(1, strategy.TicketMints);
    }
```

`Memos(n)` stands for the file's existing helper that builds `n` distinct `MemoSubmission`s; if the file names it differently, use that name. In `SequenceStrategyTests` add:

```csharp
    [Fact]
    public async Task A_sequence_strategy_mints_nothing()
    {
        FakeLedgerSource ledger = new();
        await using SequenceStrategy strategy = new(new FakeLedgerSink(ledger), Signer);

        Assert.Equal(0, strategy.TicketMints);
    }
```

(use the file's own `Signer` field or create `new WalletSigner(XrplWallet.Generate())`).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.TicketStrategyTests`
Expected: build error — `TicketMints` does not exist.

- [ ] **Step 3: Implement**

`ISubmissionStrategy`, after `Account`:

```csharp
    /// <summary>
    /// How many <c>TicketCreate</c> transactions this instance has submitted, over its lifetime, that
    /// validated and minted — each claimed a fee (spec 4.9, I18). A mint that validates with a
    /// <c>tec</c> stops the publication, so it never reaches a report. A strategy that mints nothing answers zero.
    /// There is no default: a strategy written later has to say what it minted. A session reads it
    /// before and after its run and reports the difference, so an instance handed to several
    /// sessions is counted once per session.
    /// </summary>
    int TicketMints { get; }
```

`TicketStrategy`: field `private int _mints;`, property `public int TicketMints => Volatile.Read(ref _mints);`, and in `EnsureTicketsAsync` right after `AddTickets(sequence + 1, sequence + (uint)mint);`:

```csharp
            Interlocked.Increment(ref _mints);
```

`SequenceStrategy`: `public int TicketMints => 0;`.

Forward in the three test decorators: `public int TicketMints => inner.TicketMints;`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core tests/XrplFile.Core.Tests
git commit -m "feat(core): a submission strategy says how many ticket mints it made"
```

---

### Task 9: The report states what the run and its tidy-up cost (I17, I18)

**Files:**
- Modify: `src/XrplFile.Core/PublicationSession.cs`, `src/XrplFile.Core/PublicationReport.cs`
- Modify: `README.md` (tidy-up paragraph), spec section 4.9 (one sentence)
- Test: `tests/XrplFile.Core.Tests/PublicationSessionTests.cs`

**Interfaces:**
- Consumes: `ISubmissionStrategy.TicketMints` (Task 8); `TicketRelease.Charged`, `InDoubt` (Task 6).
- Produces: `PublicationReport.TicketReleaseFeeDrops { get; init; }` and `TicketReleaseFeeDropsInDoubt { get; init; }` (both `long?`).

- [ ] **Step 1: Write the failing tests**

In `PublicationSessionTests` (reuse `Node()`, `Options()`, `Wallet`, `Signer`, `NodeFeeDrops`, `ScriptedReleaseStrategy`, `LedgerClosingStrategy` as the existing tests do; read the test at line ~190 that asserts `estimate.Mints` for the ticketed setup and copy its strategy options):

```csharp
    [Fact]
    public async Task A_fresh_run_counts_the_mints_it_made_not_the_estimates()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 100, marked: true);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            FeeDrops = NodeFeeDrops, SweepPoll = TimeSpan.FromMilliseconds(1), ValidationPoll = TimeSpan.FromMilliseconds(1),
        });
        byte[] file = VectorStream.Bytes("mints", 2500);

        PublicationEstimate estimate = await PublicationSession.EstimateAsync(ledger, Wallet.ClassicAddress, file.Length, Options(), TestContext.Current.CancellationToken);
        PublicationSession session = await PublicationSession.StartAsync(
            new LedgerClosingStrategy(strategy, sink), ledger, file, "text/plain", "m.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        long memos = report.ChunkHashes.Count + report.ListHashes.Count + 1;
        Assert.Equal(memos + strategy.TicketMints, report.Transactions);
        Assert.True(report.Transactions < memos + estimate.Mints, "the picked-up tickets cost nothing to mint");
        Assert.Equal(report.Transactions * NodeFeeDrops, report.FeeDropsEstimated);
    }

    [Fact]
    public async Task A_reused_strategy_counts_only_this_sessions_mints()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            FeeDrops = NodeFeeDrops, SweepPoll = TimeSpan.FromMilliseconds(1), ValidationPoll = TimeSpan.FromMilliseconds(1),
        });
        LedgerClosingStrategy closing = new(strategy, sink);

        PublicationSession first = await PublicationSession.StartAsync(
            closing, ledger, VectorStream.Bytes("one", 2500), "text/plain", "1.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);
        await first.RunAsync(null, TestContext.Current.CancellationToken);
        int afterFirst = strategy.TicketMints;

        PublicationSession second = await PublicationSession.StartAsync(
            closing, ledger, VectorStream.Bytes("two", 2500), "text/plain", "2.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);
        PublicationReport report = await second.RunAsync(null, TestContext.Current.CancellationToken);

        long memos = report.ChunkHashes.Count + report.ListHashes.Count + 1;
        Assert.Equal(memos + (strategy.TicketMints - afterFirst), report.Transactions);
    }

    [Fact]
    public async Task The_report_prices_the_tidy_ups_charged_and_in_doubt_spends()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy inner = new(sink, Signer, StrategyOptions);
        ScriptedReleaseStrategy strategy = new(inner, _ => Task.FromResult(new TicketRelease(3, 1, 2) { Charged = 4, InDoubt = 2 }));

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("fees", 2500), "text/plain", "f.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);
        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(4 * NodeFeeDrops, report.TicketReleaseFeeDrops);
        Assert.Equal(2 * NodeFeeDrops, report.TicketReleaseFeeDropsInDoubt);
    }

    [Fact]
    public async Task A_tidy_up_that_throws_leaves_its_cost_unknown()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy inner = new(sink, Signer, StrategyOptions);
        ScriptedReleaseStrategy strategy = new(inner, _ => throw new InvalidOperationException("the tidy-up broke"));

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("fees", 2500), "text/plain", "f.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);
        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(report.TicketReleaseFeeDrops);
        Assert.Null(report.TicketReleaseFeeDropsInDoubt);
    }
```

Also find the existing test that uses `RunWithReleaseTimeoutAsync` and asserts `IsType<TimeoutException>(report.TicketReleaseFailure)` (grace expired): add `Assert.Null(report.TicketReleaseFeeDrops);` to it. In `A_tidy_up_that_does_not_finish_in_time_does_not_hold_the_report_back` (its scripted release ends in a cancellation at the deadline) add `Assert.Null(report.TicketReleaseFeeDrops);` too. A resume test that compares `report.Transactions` with the estimate stays as it is: a resume keeps the estimate's mints.

Two existing fresh-run tests on a `SequenceStrategy` assert the estimate's two mints, which a sequence strategy never pays; I18 changes that deliberately:

- `A_published_file_comes_back_with_a_link_and_a_report` (around line 60): change to
  ```csharp
        // Four memos. A sequence strategy mints nothing, and a fresh run counts what its own
        // strategy minted, not the estimate's two (spec I18).
        Assert.Equal(4, report.Transactions);
        Assert.Equal(4 * NodeFeeDrops, report.FeeDropsEstimated);
  ```
- `The_report_counts_every_transaction_the_estimate_priced` (around line 180): build the strategy as `new LedgerClosingStrategy(new TicketStrategy(sink, ledger, Signer, new TicketStrategyOptions { FeeDrops = NodeFeeDrops, SweepPoll = TimeSpan.FromMilliseconds(1), ValidationPoll = TimeSpan.FromMilliseconds(1) }), sink)` (dispose the inner `TicketStrategy` with `await using`), keep `Assert.Equal(2, estimate.Mints)` and `Assert.Equal(estimate.Transactions, report.Transactions)`, and change the comment to: "A fresh ticketed run with no leftovers mints exactly what the estimate priced — one batching pass for the chunks and one for the manifest — and the report counts those mints (spec I18)."

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.PublicationSessionTests`
Expected: build error — `TicketReleaseFeeDrops` does not exist.

- [ ] **Step 3: Implement `PublicationReport`**

Add after `TicketReleaseFailure`:

```csharp
    /// <summary>
    /// What the tidy-up's spends certainly cost: <see cref="TicketRelease.Charged"/> at the fee the run
    /// was approved at, which every spend carried (spec 4.9, I17). Null when the tidy-up's counts were
    /// lost — it failed, did not stop within its grace, or ended in a cancellation instead of returning
    /// them; the most it could have cost is the worst case the estimate already covered (spec I10).
    /// </summary>
    public long? TicketReleaseFeeDrops { get; init; }

    /// <summary>
    /// What the tidy-up's spends may still cost: <see cref="TicketRelease.InDoubt"/> at the same fee.
    /// Such a spend may apply until its LastLedgerSequence passes. Null when the counts were lost.
    /// </summary>
    public long? TicketReleaseFeeDropsInDoubt { get; init; }
```

Rewrite the `Transactions` param doc's mint sentences: "…and the ticket mints a ticketed strategy pays for on top of them. A session started fresh counts the mints its own strategy made during the session (`ISubmissionStrategy.TicketMints`), so tickets picked up from an earlier run cost nothing here; a resumed session counts the estimate's `Mints`, because the earlier sessions' mints are on no record it reads. The tidy-up is not in it: see `TicketReleaseFeeDrops`." Rewrite `FeeDropsEstimated`'s doc to say the publication cost excluding the tidy-up, and that "what the publication and its tidy-up cost is `FeeDropsEstimated + TicketReleaseFeeDrops`, and it may have cost up to `TicketReleaseFeeDropsInDoubt` more."

- [ ] **Step 4: Implement `PublicationSession`**

1. Field `private readonly int? _mintsAtStart;` with doc "What the strategy had minted when a fresh run was started, or null on a resume (spec I18)." Add a ctor parameter `int? mintsAtStart` (last) and assign it. `StartAsync` passes `strategy.TicketMints`; `ResumeAsync` passes `null`.
2. `ReleaseLeftoverTicketsAsync` returns `(TicketRelease? Release, Exception? Failure)`: success `(release, null)`; grace timeout `(null, new TimeoutException(…))`; deadline cancellation `(null, null)`; other exception `(null, failure)`. Update its doc: "Null counts mean they were lost."
3. In `RunAsync`:

```csharp
            (TicketRelease? release, Exception? releaseFailure) = await ReleaseLeftoverTicketsAsync(cancellationToken).ConfigureAwait(false);
            TicketRelease counts = release ?? TicketRelease.None;

            // A fresh run's session is its whole publication, so its strategy's own count is the
            // publication's; a resume's earlier sessions minted too, unrecorded (spec I18).
            int mints = _mintsAtStart is { } before ? _strategy.TicketMints - before : _mints;
            long transactions = result.ChunkHashes.Count + result.ListHashes.Count + 1 + mints;
```

and in the initializer:

```csharp
                TicketsReleased = counts.Released,
                TicketsAlreadyGone = counts.AlreadyGone,
                TicketsNotReleased = counts.NotReleased,
                TicketReleaseFailure = releaseFailure,
                TicketReleaseFeeDrops = release is null ? null : release.Charged * _feeDropsPerTransaction,
                TicketReleaseFeeDropsInDoubt = release is null ? null : release.InDoubt * _feeDropsPerTransaction,
```

Update the `_mints` field comment: "the estimate's mints, used on a resume".

- [ ] **Step 5: Spec and README**

In the spec, section 4.9, replace "Both are null when the tidy-up failed or did not stop within its grace (`TicketReleaseFailure` is set): its counts were lost with it" with "Both are null when the tidy-up's counts were lost: it failed or did not stop within its grace (`TicketReleaseFailure` is set), or it ended by throwing a cancellation instead of returning them". In `README.md`'s tidy-up paragraph, after the sentence naming `TicketReleaseFailure`, add: "It also says what the tidy-up cost (`TicketReleaseFeeDrops`) and what it may still cost (`TicketReleaseFeeDropsInDoubt`) — a spend still queued when time ran out may land until its `LastLedgerSequence`, ten ledgers on. On a loaded public node the tidy-up resends a spend the node was too busy to take, and slows down when the node warns that the client is nearing its rate limit."

- [ ] **Step 6: Run to verify pass**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` and `dotnet run --project tests/XrplFile.App.Core.Tests`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/XrplFile.Core tests/XrplFile.Core.Tests README.md docs/superpowers/specs/2026-10-12-leftover-tickets-design.md
git commit -m "feat(core): the report prices the tidy-up and counts a fresh run's own mints"
```

---

### Task 10: Proof against the standalone node

**Files:**
- Modify: `tests/XrplFile.Core.Tests/LeftoverTicketsStandaloneTests.cs`
- Create: `tests/XrplFile.Core.Tests/XrplClientLedgerSinkStandaloneTests.cs`

**Interfaces:**
- Consumes: everything above; `StandaloneFixture`, `[Collection("Standalone")]`, `[Trait("Category", "Integration")]`.

- [ ] **Step 1: Check the stand is up**

Run: `docker compose -p xrpl-file-ci ps`
Expected: `rippled-service` and `ledger-acceptor` Up. If not, `docker compose -p xrpl-file-ci up -d` from the directory whose compose file defines that project (find it with `grep -rl "xrpl-file-ci" --include=*.yml .`).

- [ ] **Step 2: Write the tests**

In `LeftoverTicketsStandaloneTests.A_small_publication_after_a_stopped_large_one_releases_what_it_left`, after the existing report assertions:

```csharp
            // Every spend carried a LastLedgerSequence and the node took it (spec I16); each released
            // ticket cost exactly one fee, and nothing is left in doubt (spec I17).
            Assert.Equal(report.TicketsReleased * (long)fee, report.TicketReleaseFeeDrops);
            Assert.Equal(0, report.TicketReleaseFeeDropsInDoubt);
```

In `A_resumed_run_spends_what_the_stopped_one_left…`, after the existing assertions:

```csharp
            Assert.Equal(0, report.TicketReleaseFeeDropsInDoubt);
            Assert.True(report.TicketReleaseFeeDrops >= report.TicketsReleased * (long)fee);
```

Create `tests/XrplFile.Core.Tests/XrplClientLedgerSinkStandaloneTests.cs`:

```csharp
using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// The sink against a real node's error shape (spec I13): rippled answers a blob it cannot parse with
/// status "error", which the SDK raises as RippledException — the shape no fake can vouch for.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class XrplClientLedgerSinkStandaloneTests
{
    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    /// <summary>Signs properly and then cuts four bytes off the blob, so it still decodes as hex but not as a transaction.</summary>
    private sealed class TruncatingSigner(ITransactionSigner inner) : ITransactionSigner
    {
        public string Account => inner.Account;

        public string PublicKey => inner.PublicKey;

        public async Task<SignedTransaction> SignAsync(IDictionary<string, object> transaction, CancellationToken cancellationToken)
        {
            SignedTransaction signed = await inner.SignAsync(transaction, cancellationToken);
            return signed with { TxBlob = signed.TxBlob[..^8] };
        }
    }

    [Fact]
    public async Task A_node_error_answer_comes_back_as_an_outcome_not_an_exception()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(timeout.Token);

        try
        {
            XrplWallet wallet = XrplWallet.Generate();
            XrplClientLedgerSink sink = new(client);

            SubmitOutcome outcome = await sink.SubmitAsync(
                new Dictionary<string, object>
                {
                    ["TransactionType"] = "AccountSet",
                    ["Account"] = wallet.ClassicAddress,
                    ["Sequence"] = 1u,
                    ["Fee"] = "12",
                    ["SigningPubKey"] = wallet.PublicKey,
                },
                new TruncatingSigner(new WalletSigner(wallet)),
                timeout.Token);

            Assert.Equal("error:invalidTransaction", outcome.EngineResult);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
```

- [ ] **Step 3: Run the integration set**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration"` (in the background; it takes minutes)
Expected: all pass. Before Task 1 the new standalone test would have thrown `RippledException`; if it fails with a different error code, read rippled's `Submit.cpp` (`E:\GIT\XRPL\rippled\src\xrpld\rpc\handlers\transaction\Submit.cpp`) and fix the blob, not the expectation's shape.

- [ ] **Step 4: Commit**

```bash
git add tests/XrplFile.Core.Tests/LeftoverTicketsStandaloneTests.cs tests/XrplFile.Core.Tests/XrplClientLedgerSinkStandaloneTests.cs
git commit -m "test(core): prove the tidy-up's deadline spends and the sink's error answers on the standalone node"
```

---

### Task 11: Whole-branch verification

- [ ] **Step 1: Run every suite**

Run, each to completion:
- `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
- `dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration"`
- `dotnet run --project tests/XrplFile.App.Core.Tests`
- `dotnet run --project tests/XrplFile.Keys.Tests`
- `dotnet build XrplFile.sln` (warnings are errors)

Expected: all green, zero warnings.

- [ ] **Step 2: Check the spec against the code**

For each of I13–I18, open the section it cites and confirm one test names its behaviour. Record any gap as a new step in the owning task rather than a note.
