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

        await strategy.SubmitAllAsync(Memos(4), new SynchronousProgress<int>(seen.Add), TestContext.Current.CancellationToken);

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
