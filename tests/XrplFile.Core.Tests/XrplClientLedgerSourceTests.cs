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
    public async Task An_account_the_node_does_not_know_has_no_transactions_rather_than_a_failure()
    {
        // The state a throwaway account's funder lookup starts in: nobody has paid the reserve in,
        // so there is no account for the node to list and it says actNotFound. That is an answer
        // -- nothing has happened yet -- and reading it as a failure to read is what made
        // FunderAsync throw where it was asked to propose nobody. Both shapes the code can meet:
        // embedded in a returned answer, and thrown by RequestManager before any JSON arrives.
        ScriptedNode answered = new();
        answered.Answer("""{"error":"actNotFound"}""");
        ScriptedNode threw = new();
        threw.Throw(new Xrpl.Client.Exceptions.RippledException(
            "actNotFound", new Xrpl.Models.Subscriptions.ErrorResponse { Error = "actNotFound" }));

        Assert.Empty(await Read(new XrplClientLedgerSource(answered.HandleAsync), TestContext.Current.CancellationToken));
        Assert.Empty(await Read(new XrplClientLedgerSource(threw.HandleAsync), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_account_state_says_what_the_account_owns_as_well_as_what_it_holds()
    {
        ScriptedNode node = new();
        node.Answer("""{"account_data":{"Account":"rWriter","Balance":"25000000","Sequence":7,"OwnerCount":42,"TicketCount":9}}""");

        AccountState state = await new XrplClientLedgerSource(node.HandleAsync)
            .AccountStateAsync("rWriter", TestContext.Current.CancellationToken);

        Assert.Equal(25_000_000, state.BalanceDrops);
        Assert.Equal(42, state.OwnerCount);

        // Tickets are owner objects like any other, but a mint has to know how many of the 250 the
        // protocol allows are already gone, and OwnerCount cannot say.
        Assert.Equal(9, state.TicketCount);
    }

    [Fact]
    public async Task An_account_data_without_an_owner_count_owns_nothing()
    {
        ScriptedNode node = new();
        node.Answer("""{"account_data":{"Account":"rWriter","Balance":"25000000"}}""");

        AccountState state = await new XrplClientLedgerSource(node.HandleAsync)
            .AccountStateAsync("rWriter", TestContext.Current.CancellationToken);

        Assert.Equal(0, state.OwnerCount);

        // AccountRoot omits TicketCount entirely when the account holds none; absent is zero.
        Assert.Equal(0, state.TicketCount);
    }

    [Fact]
    public async Task An_account_the_node_does_not_know_holds_nothing_and_owns_nothing()
    {
        ScriptedNode node = new();
        node.Answer("""{"error":"actNotFound"}""");

        AccountState state = await new XrplClientLedgerSource(node.HandleAsync)
            .AccountStateAsync("rNobody", TestContext.Current.CancellationToken);

        Assert.Equal(0, state.BalanceDrops);
        Assert.Equal(0, state.OwnerCount);
        Assert.Equal(0, state.TicketCount);
    }

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
    public async Task A_destination_the_node_does_not_carry_is_absent()
    {
        ScriptedNode node = new();
        node.Answer("""{"error":"actNotFound"}""");

        DestinationState state = await new XrplClientLedgerSource(node.HandleAsync)
            .DestinationAsync("rNobody", TestContext.Current.CancellationToken);

        Assert.Equal(DestinationState.Absent, state);
    }

    [Fact]
    public async Task A_malformed_destination_address_is_absent_not_an_unreachable_node()
    {
        // A classic address carries a checksum: a typo does not usually resolve to a different
        // real account, it fails to decode, and rippled answers actMalformed rather than
        // actNotFound. Design G12 keeps "that destination does not exist" apart from "the node did
        // not answer" -- the commonest destination mistake there is must land on the first sentence,
        // not send a person to check their node URL in Settings for what was actually a typo.
        ScriptedNode answered = new();
        answered.Answer("""{"error":"actMalformed"}""");
        ScriptedNode threw = new();
        threw.Throw(new Xrpl.Client.Exceptions.RippledException(
            "actMalformed", new Xrpl.Models.Subscriptions.ErrorResponse { Error = "actMalformed" }));

        Assert.Equal(
            DestinationState.Absent,
            await new XrplClientLedgerSource(answered.HandleAsync).DestinationAsync("rNot-an-address", TestContext.Current.CancellationToken));
        Assert.Equal(
            DestinationState.Absent,
            await new XrplClientLedgerSource(threw.HandleAsync).DestinationAsync("rNot-an-address", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_node_error_other_than_absent_leaves_the_destination_lookup_unreachable()
    {
        ScriptedNode node = new();
        node.Answer("""{"error":"noNetwork"}""");

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplClientLedgerSource(node.HandleAsync).DestinationAsync("rWriter", TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
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

    private static async Task<List<LedgerTransaction>> Read(XrplClientLedgerSource source, CancellationToken cancellationToken)
    {
        List<LedgerTransaction> transactions = [];

        await foreach (LedgerTransaction tx in source.AccountTransactionsAsync("rWriter", 1, 2, forward: true, cancellationToken))
        {
            transactions.Add(tx);
        }

        return transactions;
    }

    [Fact]
    public async Task Live_tickets_are_read_page_by_page_with_their_creators()
    {
        ScriptedNode node = new();
        node.Answer("""
            {"account":"rWriter","account_objects":[
              {"LedgerEntryType":"Ticket","TicketSequence":11,"PreviousTxnID":"aa11","Account":"rWriter"},
              {"LedgerEntryType":"Ticket","TicketSequence":12,"PreviousTxnID":"aa11","Account":"rWriter"}],
             "ledger_index":77,"validated":true,"marker":"next-page"}
            """);
        node.Answer("""
            {"account":"rWriter","account_objects":[
              {"LedgerEntryType":"Ticket","TicketSequence":40,"PreviousTxnID":"bb40","Account":"rWriter"}],
             "ledger_index":77,"validated":true}
            """);

        IReadOnlyList<LiveTicket> tickets = await new XrplClientLedgerSource(node.HandleAsync)
            .LiveTicketsAsync("rWriter", TestContext.Current.CancellationToken);

        Assert.Equal(
            [new LiveTicket(11, "AA11"), new LiveTicket(12, "AA11"), new LiveTicket(40, "BB40")],
            tickets);
        Assert.Equal(["account_objects", "account_objects"], node.Commands);

        // What was asked matters as much as what came back: only tickets, the first page from the
        // validated ledger and every later one from that same ledger, asked for by the marker the
        // page before it carried.
        Xrpl.Models.Methods.AccountObjectsRequest[] asked = [.. node.Requests.Cast<Xrpl.Models.Methods.AccountObjectsRequest>()];
        Assert.All(asked, request => Assert.Equal(Xrpl.Models.LedgerEntryType.Ticket, request.Type));
        Assert.Null(asked[0].LedgerIndex.Index);
        Assert.Equal(Xrpl.Models.Ledger.LedgerIndexType.Validated, asked[0].LedgerIndex.LedgerIndexType);
        Assert.Equal(77u, asked[1].LedgerIndex.Index);
        Assert.Null(asked[0].Marker);
        Assert.Equal("next-page", Assert.IsType<JsonElement>(asked[1].Marker).GetString());
    }

    [Fact]
    public async Task A_null_marker_ends_the_ticket_list()
    {
        ScriptedNode node = new();
        node.Answer("""
            {"account":"rWriter","account_objects":[
              {"LedgerEntryType":"Ticket","TicketSequence":11,"PreviousTxnID":"aa11","Account":"rWriter"}],
             "ledger_index":77,"marker":null}
            """);

        IReadOnlyList<LiveTicket> tickets = await new XrplClientLedgerSource(node.HandleAsync)
            .LiveTicketsAsync("rWriter", TestContext.Current.CancellationToken);

        Assert.Equal([new LiveTicket(11, "AA11")], tickets);
        Assert.Equal(["account_objects"], node.Commands);
    }

    [Fact]
    public async Task A_marker_repeated_by_the_node_is_a_failure_not_a_loop()
    {
        ScriptedNode node = new();
        node.Answer("""
            {"account":"rWriter","account_objects":[
              {"LedgerEntryType":"Ticket","TicketSequence":11,"PreviousTxnID":"aa11","Account":"rWriter"}],
             "ledger_index":77,"marker":"stuck"}
            """);
        node.Answer("""
            {"account":"rWriter","account_objects":[
              {"LedgerEntryType":"Ticket","TicketSequence":11,"PreviousTxnID":"aa11","Account":"rWriter"}],
             "ledger_index":77,"marker":"stuck"}
            """);

        XrplFileException failure = await Assert.ThrowsAsync<XrplFileException>(() => new XrplClientLedgerSource(node.HandleAsync)
            .LiveTicketsAsync("rWriter", TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, failure.Error);
        Assert.Equal(["account_objects", "account_objects"], node.Commands);
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

    [Fact]
    public void A_ticket_sequence_outside_what_a_sequence_can_be_is_skipped_not_wrapped()
    {
        // 4294967297 wraps to 1 in an unchecked cast, and would be read as a ticket the account may
        // hold; a ticket sequence is a 32-bit unsigned number, so anything else is not a ticket.
        JsonElement answer = JsonDocument.Parse("""
            {"account_objects":[
              {"LedgerEntryType":"Ticket","TicketSequence":4294967297,"PreviousTxnID":"aa"},
              {"LedgerEntryType":"Ticket","TicketSequence":-1,"PreviousTxnID":"bb"},
              {"LedgerEntryType":"Ticket","TicketSequence":4294967295,"PreviousTxnID":"cc"}]}
            """).RootElement;

        Assert.Equal([new LiveTicket(uint.MaxValue, "CC")], XrplClientLedgerSource.ParseTickets(answer));
    }

    /// <summary>A node whose answers are scripted, in call order.</summary>
    private sealed class ScriptedNode
    {
        private readonly Queue<Func<JsonElement>> _answers = new();

        public List<string> Commands { get; } = [];

        public List<Xrpl.Models.Methods.BaseRequest> Requests { get; } = [];

        public void Answer(string json) => _answers.Enqueue(() => JsonDocument.Parse(json).RootElement);

        public void Throw(Exception exception) => _answers.Enqueue(() => throw exception);

        private readonly Queue<string?> _warnings = new();

        public void AnswerWithWarning(string json, string? warning)
        {
            Answer(json);
            _warnings.Enqueue(warning);
        }

        public async Task<NodeAnswer> HandleWithWarningsAsync(Xrpl.Models.Methods.BaseRequest request, CancellationToken cancellationToken) =>
            new(await HandleAsync(request, cancellationToken), _warnings.Count > 0 ? _warnings.Dequeue() : null);

        public Task<JsonElement> HandleAsync(Xrpl.Models.Methods.BaseRequest request, CancellationToken cancellationToken)
        {
            Commands.Add(request.Command);
            Requests.Add(request);

            if (_answers.Count == 0) throw new InvalidOperationException("the script ran out of answers");

            return Task.FromResult(_answers.Dequeue()());
        }
    }
}
