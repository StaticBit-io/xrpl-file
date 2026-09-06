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
}
