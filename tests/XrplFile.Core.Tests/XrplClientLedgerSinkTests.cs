using System.Text.Json;
using Xrpl.Models.Methods;
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

    /// <summary>A node whose answers to "submit" and "tx" are scripted, in call order.</summary>
    private sealed class ScriptedNode
    {
        private readonly Queue<Func<JsonElement>> _answers = new();

        public List<(string Command, string? TxBlob)> Requests { get; } = [];

        public void Answer(string json) => _answers.Enqueue(() => JsonDocument.Parse(json).RootElement);

        public void Throw(Exception exception) => _answers.Enqueue(() => throw exception);

        public Task<JsonElement> HandleAsync(BaseRequest request, CancellationToken cancellationToken)
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
