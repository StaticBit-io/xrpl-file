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
        Assert.Equal("tesSUCCESS", tx.Result);
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
        Assert.Equal("tecUNFUNDED", tx.Result);
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
        Assert.Null(tx.Result);
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

    [Fact]
    public async Task The_fees_and_reserves_come_from_server_state()
    {
        ScriptedNode node = new();
        node.Answer("""
            {"state":{"validated_ledger":{"base_fee":10,"reserve_base":1000000,"reserve_inc":200000,"seq":106497159}}}
            """);

        NetworkFees fees = await new XrplClientLedgerSource(node.HandleAsync).FeesAsync(TestContext.Current.CancellationToken);

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
        node.Answer("""
            {"state":{"validated_ledger":{"base_fee":10,"reserve_base":1000000,"reserve_inc":200000},"load_factor":2000,"load_base":256}}
            """);

        NetworkFees fees = await new XrplClientLedgerSource(node.HandleAsync).FeesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(78, fees.OpenLedgerFeeDrops);
    }

    [Fact]
    public async Task An_account_balance_comes_from_account_info()
    {
        ScriptedNode node = new();
        node.Answer("""{"account_data":{"Account":"rWriter","Balance":"25000000","Sequence":7}}""");

        long drops = await new XrplClientLedgerSource(node.HandleAsync)
            .BalanceDropsAsync("rWriter", TestContext.Current.CancellationToken);

        Assert.Equal(25_000_000, drops);
    }

    [Fact]
    public async Task An_account_the_node_does_not_know_holds_nothing()
    {
        ScriptedNode node = new();
        node.Answer("""{"error":"actNotFound"}""");

        long drops = await new XrplClientLedgerSource(node.HandleAsync)
            .BalanceDropsAsync("rNobody", TestContext.Current.CancellationToken);

        Assert.Equal(0, drops);
    }

    [Fact]
    public async Task An_account_the_node_throws_act_not_found_for_holds_nothing()
    {
        // Against a real node, actNotFound never arrives as an {"error":...} member of a returned
        // answer: RequestManager.HandleResponse rejects the request's promise with a
        // RippledException for any response whose status is "error", before this class ever sees a
        // JSON element. The JSON-embedded shape above is a fixture convenience only the tests use;
        // this is the shape Task 7's funding wait actually polls, and it must also read as zero.
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.RippledException(
            "actNotFound", new Xrpl.Models.Subscriptions.ErrorResponse { Error = "actNotFound" }));

        long drops = await new XrplClientLedgerSource(node.HandleAsync)
            .BalanceDropsAsync("rNobody", TestContext.Current.CancellationToken);

        Assert.Equal(0, drops);
    }

    [Fact]
    public async Task A_node_error_other_than_act_not_found_leaves_the_balance_lookup_unreachable()
    {
        // "actNotFoundToo" is not a real rippled error code -- it stands in for whatever unrelated
        // code could one day carry "actNotFound" as a substring, exactly the risk a message-prose
        // scan cannot rule out. Comparing the raw error field for an exact match, the way this
        // source now does, correctly treats it as a different error; scanning exception prose for
        // that substring would wrongly treat it as an answer and hand back zero instead of
        // surfacing the failure.
        ScriptedNode node = new();
        node.Answer("""{"error":"actNotFoundToo"}""");

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplClientLedgerSource(node.HandleAsync).BalanceDropsAsync("rWriter", TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Contains("actNotFoundToo", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_current_ledger_is_the_validated_index()
    {
        ScriptedNode node = new();
        node.Answer("""{"ledger_index":106497159,"validated":true,"ledger":{"ledger_index":"106497159"}}""");

        long ledger = await new XrplClientLedgerSource(node.HandleAsync)
            .CurrentLedgerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(106497159, ledger);
    }

    [Fact]
    public async Task A_node_that_will_not_say_what_it_charges_is_unreachable()
    {
        ScriptedNode node = new();
        node.Answer("""{"error":"noNetwork"}""");

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplClientLedgerSource(node.HandleAsync).FeesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
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
