using System.Text.Json;
using Xunit;

namespace XrplFile.Tests;

public class XrplClientLedgerSourceTests
{
    [Fact]
    public void An_api_v2_record_is_parsed_from_tx_json_and_the_record_level_fields()
    {
        JsonElement record = JsonDocument.Parse("""
            {"hash":"abc1","ledger_index":106497159,"validated":true,
             "meta":{"TransactionResult":"tesSUCCESS"},
             "tx_json":{"Account":"rWriter","TransactionType":"AccountSet",
               "Memos":[{"Memo":{"MemoType":"78662F632F30","MemoData":"CAFE","MemoFormat":"6170706C69636174696F6E2F6A736F6E"}}]}}
            """).RootElement;

        LedgerTransaction tx = XrplClientLedgerSource.ParseRecord(record);

        Assert.Equal("ABC1", tx.Hash);
        Assert.Equal(106497159, tx.LedgerIndex);
        Assert.Equal("rWriter", tx.Account);
        Assert.True(tx.Succeeded);
        Assert.True(tx.Validated);
        Assert.Equal("78662F632F30", tx.MemoTypeHex);
        Assert.Equal("CAFE", tx.MemoDataHex);
        Assert.Equal("6170706C69636174696F6E2F6A736F6E", tx.MemoFormatHex);
    }

    [Fact]
    public void An_api_v1_record_is_parsed_from_tx()
    {
        JsonElement record = JsonDocument.Parse("""
            {"validated":true,"meta":{"TransactionResult":"tecUNFUNDED"},
             "tx":{"hash":"abc2","ledger_index":5,"Account":"rWriter","Memos":[{"Memo":{"MemoData":"00"}}]}}
            """).RootElement;

        LedgerTransaction tx = XrplClientLedgerSource.ParseRecord(record);

        Assert.Equal("ABC2", tx.Hash);
        Assert.Equal(5, tx.LedgerIndex);
        Assert.False(tx.Succeeded);
        Assert.Null(tx.MemoTypeHex);
        Assert.Equal("00", tx.MemoDataHex);
    }

    [Fact]
    public void A_record_without_memos_or_meta_still_parses()
    {
        JsonElement record = JsonDocument.Parse("""{"hash":"x","ledger_index":1,"tx_json":{"Account":"r"}}""").RootElement;

        LedgerTransaction tx = XrplClientLedgerSource.ParseRecord(record);

        Assert.False(tx.Succeeded);
        Assert.False(tx.Validated);
        Assert.Null(tx.MemoDataHex);
    }

    [Fact]
    public void Transactions_are_read_from_the_account_tx_answer()
    {
        JsonElement answer = JsonDocument.Parse("""
            {"account":"r","transactions":[
              {"hash":"a","ledger_index":1,"validated":true,"meta":{"TransactionResult":"tesSUCCESS"},"tx_json":{"Account":"r"}},
              {"hash":"b","ledger_index":2,"validated":true,"meta":{"TransactionResult":"tesSUCCESS"},"tx_json":{"Account":"r"}}],
             "marker":{"ledger":2,"seq":3}}
            """).RootElement;

        IReadOnlyList<LedgerTransaction> parsed = XrplClientLedgerSource.ParseTransactions(answer);

        Assert.Equal(new[] { "A", "B" }, parsed.Select(tx => tx.Hash).ToArray());
    }

    [Theory]
    [InlineData("32570-106738922", 32570L)]
    [InlineData("32570-100,5000-106738922", 32570L)]
    [InlineData("100000000-106738922", 100000000L)]
    [InlineData("empty", null)]
    [InlineData("", null)]
    public void The_earliest_ledger_is_the_low_bound_of_the_first_range(string ranges, long? expected)
    {
        JsonElement answer = JsonDocument.Parse($$$"""{"info":{"complete_ledgers":"{{{ranges}}}"}}""").RootElement;

        Assert.Equal(expected, XrplClientLedgerSource.EarliestAvailableLedger(answer));
    }

    [Fact]
    public void Endpoints_are_full_history_nodes_per_network()
    {
        Assert.Equal(new[] { "wss://xrplcluster.com", "wss://s2.ripple.com" }, Endpoints.For("mainnet"));
        Assert.Equal(new[] { "wss://s.altnet.rippletest.net:51233" }, Endpoints.For("testnet"));
        Assert.Empty(Endpoints.For("21338"));
    }

    // The three ILedgerSource methods over a scripted node. The document promises NodeUnreachable
    // for a node that did not answer, and the browser mirror raises it in all three; this source
    // used to let the SDK's own exception -- DisconnectedException, RippledException -- out to a
    // caller who cannot switch on it.

    public static TheoryData<string, Func<XrplClientLedgerSource, CancellationToken, Task>> EveryCall => new()
    {
        { "tx", (source, token) => source.TransactionAsync(new string('A', 64), token) },
        { "account_tx", (source, token) => Drain(source, token) },
        { "server_info", (source, token) => source.EarliestLedgerAsync(token) },
    };

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task A_dropped_connection_is_reported_as_an_unreachable_node(string command, Func<XrplClientLedgerSource, CancellationToken, Task> call)
    {
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.DisconnectedException("the socket went away mid-request"));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => call(new XrplClientLedgerSource(node.HandleAsync), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Contains(command, error.Message, StringComparison.Ordinal);
        Assert.IsType<Xrpl.Client.Exceptions.DisconnectedException>(error.InnerException);
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task A_request_that_timed_out_is_reported_as_an_unreachable_node(string command, Func<XrplClientLedgerSource, CancellationToken, Task> call)
    {
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("the node did not answer in time"));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => call(new XrplClientLedgerSource(node.HandleAsync), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Contains(command, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task A_node_error_other_than_txn_not_found_is_reported_as_an_unreachable_node(string command, Func<XrplClientLedgerSource, CancellationToken, Task> call)
    {
        ScriptedNode node = new();
        node.Answer("""{"error":"invalidParams","error_message":"Missing field 'account'."}""");

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => call(new XrplClientLedgerSource(node.HandleAsync), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Contains("invalidParams", error.Message, StringComparison.Ordinal);
        Assert.Contains(command, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transaction_the_node_does_not_have_is_null_not_an_error()
    {
        ScriptedNode answered = new();
        answered.Answer("""{"error":"txnNotFound"}""");
        ScriptedNode threw = new();
        threw.Throw(new Xrpl.Client.Exceptions.RippledException(
            "txnNotFound", new Xrpl.Models.Subscriptions.ErrorResponse { Error = "txnNotFound" }));

        Assert.Null(await new XrplClientLedgerSource(answered.HandleAsync).TransactionAsync(new string('A', 64), TestContext.Current.CancellationToken));
        Assert.Null(await new XrplClientLedgerSource(threw.HandleAsync).TransactionAsync(new string('A', 64), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancellation_stays_a_cancellation()
    {
        ScriptedNode node = new();
        node.Throw(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new XrplClientLedgerSource(node.HandleAsync).EarliestLedgerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_answered_page_still_yields_its_transactions()
    {
        ScriptedNode node = new();
        node.Answer("""
            {"account":"r","transactions":[
              {"hash":"a","ledger_index":1,"validated":true,"meta":{"TransactionResult":"tesSUCCESS"},"tx_json":{"Account":"r"}}]}
            """);

        List<LedgerTransaction> read = [];
        await foreach (LedgerTransaction tx in new XrplClientLedgerSource(node.HandleAsync)
            .AccountTransactionsAsync("r", 1, 2, forward: true, TestContext.Current.CancellationToken))
        {
            read.Add(tx);
        }

        Assert.Equal(["A"], read.Select(tx => tx.Hash).ToArray());
        Assert.Equal("account_tx", node.Commands[0]);
    }

    private static async Task Drain(XrplClientLedgerSource source, CancellationToken cancellationToken)
    {
        await foreach (LedgerTransaction _ in source.AccountTransactionsAsync("rWriter", 1, 2, forward: true, cancellationToken))
        {
        }
    }

    /// <summary>A node whose answers are scripted, in call order.</summary>
    private sealed class ScriptedNode
    {
        private readonly Queue<Func<JsonElement>> _answers = new();

        public List<string> Commands { get; } = [];

        public void Answer(string json) => _answers.Enqueue(() => JsonDocument.Parse(json).RootElement);

        public void Throw(Exception exception) => _answers.Enqueue(() => throw exception);

        public Task<JsonElement> HandleAsync(Xrpl.Models.Methods.BaseRequest request, CancellationToken cancellationToken)
        {
            Commands.Add(request.Command);

            if (_answers.Count == 0) throw new InvalidOperationException("the script ran out of answers");

            return Task.FromResult(_answers.Dequeue()());
        }
    }
}
