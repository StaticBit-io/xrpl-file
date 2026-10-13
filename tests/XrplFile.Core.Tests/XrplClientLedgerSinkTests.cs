using System.Text.Json;
using Xrpl.Client.Exceptions;
using Xrpl.Models.Methods;
using Xrpl.Models.Subscriptions;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// Drives XrplClientLedgerSink's internal request-delegate seam with a scripted node: a queue of
/// per-call answers, each either raw JSON or an exception, consumed in order. Only "submit" and
/// "tx" ever go out, so SequenceAsync (the typed account_info path) is not exercised here.
/// </summary>
public class XrplClientLedgerSinkTests
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);

    [Fact]
    public async Task A_plain_success_is_reported_with_one_call()
    {
        ScriptedNode node = new();
        node.Answer("""{"engine_result":"tesSUCCESS"}""");

        SubmitOutcome outcome = await Submit(node);

        Assert.Equal("tesSUCCESS", outcome.EngineResult);
        Assert.Single(node.Requests);
        Assert.Equal("submit", node.Requests[0].Command);
    }

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

    [Fact]
    public async Task A_submit_timeout_resolved_by_a_validated_lookup_does_not_resubmit()
    {
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("submit timed out"));
        node.Answer("""{"validated":true,"meta":{"TransactionResult":"tesSUCCESS"}}""");

        SubmitOutcome outcome = await Submit(node);

        Assert.Equal("tesSUCCESS", outcome.EngineResult);
        Assert.Equal(2, node.Requests.Count);
        Assert.Equal(["submit", "tx"], node.Requests.Select(r => r.Command).ToArray());
    }

    [Fact]
    public async Task A_submit_timeout_resolved_by_a_failed_lookup_reports_the_real_result()
    {
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("submit timed out"));
        node.Answer("""{"validated":true,"meta":{"TransactionResult":"tecINSUFFICIENT_RESERVE"}}""");

        SubmitOutcome outcome = await Submit(node);

        Assert.Equal("tecINSUFFICIENT_RESERVE", outcome.EngineResult);
    }

    [Fact]
    public async Task An_unknown_transaction_after_a_timeout_is_resubmitted()
    {
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("submit timed out"));
        node.Answer("""{"error":"txnNotFound"}""");
        node.Answer("""{"engine_result":"tefPAST_SEQ"}""");
        node.Answer("""{"validated":true,"meta":{"TransactionResult":"tesSUCCESS"}}""");

        SubmitOutcome outcome = await Submit(node);

        Assert.Equal("tesSUCCESS", outcome.EngineResult);
        Assert.Equal(["submit", "tx", "submit", "tx"], node.Requests.Select(r => r.Command).ToArray());
    }

    [Fact]
    public async Task A_lookup_that_also_times_out_is_treated_as_not_known_yet()
    {
        // This is finding 2: it must fail against a sink that lets the lookup's own timeout
        // escape instead of treating it as "not known yet" and resubmitting.
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("submit timed out"));
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("tx lookup timed out"));
        node.Answer("""{"engine_result":"tesSUCCESS"}""");

        SubmitOutcome outcome = await Submit(node);

        Assert.Equal("tesSUCCESS", outcome.EngineResult);
        Assert.Equal(["submit", "tx", "submit"], node.Requests.Select(r => r.Command).ToArray());
    }

    [Fact]
    public async Task Three_unresolved_timeouts_report_the_node_as_unreachable()
    {
        ScriptedNode node = new();
        for (int i = 0; i < 3; i++)
        {
            node.Throw(new Xrpl.Client.Exceptions.TimeoutException("submit timed out"));
            node.Throw(new Xrpl.Client.Exceptions.TimeoutException("tx lookup timed out"));
        }

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => Submit(node));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Equal(6, node.Requests.Count);
    }

    [Fact]
    public async Task The_same_signed_blob_is_sent_on_every_resubmit()
    {
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("submit timed out"));
        node.Answer("""{"error":"txnNotFound"}""");
        node.Answer("""{"engine_result":"tefPAST_SEQ"}""");
        node.Answer("""{"validated":true,"meta":{"TransactionResult":"tesSUCCESS"}}""");

        await Submit(node);

        string?[] blobs = [.. node.Requests.Where(r => r.Command == "submit").Select(r => r.TxBlob)];
        Assert.Equal(2, blobs.Length);
        Assert.All(blobs, blob => Assert.False(string.IsNullOrEmpty(blob)));
        Assert.Equal(blobs[0], blobs[1]);
    }

    [Fact]
    public async Task A_still_open_transaction_with_no_meta_is_reported_as_queued()
    {
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("submit timed out"));
        node.Answer("""{"validated":false}""");

        SubmitOutcome outcome = await Submit(node);

        Assert.Equal("terQUEUED", outcome.EngineResult);
    }

    private static Task<SubmitOutcome> Submit(ScriptedNode node)
    {
        XrplClientLedgerSink sink = new(node.HandleAsync);

        Dictionary<string, object> transaction = new()
        {
            ["TransactionType"] = "AccountSet",
            ["Account"] = Wallet.ClassicAddress,
            ["Sequence"] = 1u,
            ["Fee"] = "12",
            ["SigningPubKey"] = Wallet.PublicKey,
            ["Memos"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["Memo"] = new Dictionary<string, object> { ["MemoType"] = "78662F6D", ["MemoData"] = "CAFE" },
                },
            },
        };

        return sink.SubmitAsync(transaction, Signer, TestContext.Current.CancellationToken);
    }

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
        Assert.Equal(["submit", "tx"], node.Requests.Select(r => r.Command).ToArray());
    }

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
}
