using Xunit;

namespace XrplFile.Tests;

public class BatchOutageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static BatchStrategyOptions Options => BatchTestNode.FastOptions with { NodeOutageLimit = TimeSpan.FromMilliseconds(300) };

    [Fact]
    public async Task A_blob_whose_answer_was_lost_is_resent_and_nothing_lands_twice()
    {
        BatchTestNode node = new(sink =>
        {
            // No ledger closes until every lost answer has been resent: otherwise settlement resolves
            // the three batches by the payer's sequence before their resends go out.
            sink.AutoClose = false;
            sink.ApplyThenDropAt.Add(0);
            sink.ApplyThenDropAt.Add(1);
            sink.ApplyThenDropAt.Add(2);
        });
        await using BatchStrategy strategy = node.Strategy(Options with { NodeOutageLimit = TimeSpan.FromSeconds(10) });

        Task<IReadOnlyList<SubmittedMemo>> run = strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);

        // All three scripted drops have happened, and every dropped blob has been transmitted again.
        // Counting a particular answer would depend on which copy reaches the fake first.
        await BatchTestNode.EventuallyAsync(() =>
        {
            IReadOnlyList<FakeBatchLedgerSink.Transmission> sent = node.Sink.Transmissions;
            return sent.Count(t => t.Ordinal <= 2) == 3
                && sent.Where(t => t.Ordinal <= 2).All(dropped => sent.Any(t => t.Ordinal > dropped.Ordinal && t.Hash == dropped.Hash));
        });
        node.Sink.AutoClose = true;

        IReadOnlyList<SubmittedMemo> landed = await run;

        IReadOnlyList<FakeBatchLedgerSink.Transmission> all = node.Sink.Transmissions;
        Assert.All(
            all.Where(t => t.Ordinal <= 2).GroupBy(t => t.Hash),
            dropped => Assert.True(all.Count(t => t.Hash == dropped.Key) > dropped.Count(), "a dropped blob was not sent again"));

        Assert.Equal(24, landed.Count);
        Assert.Equal(3, strategy.Trace.Count(entry => entry.EndsWith(":no answer", StringComparison.Ordinal)));
        Assert.All((await node.LandedByMarkerAsync()).Values, copies => Assert.Single(copies));
        Assert.Single(strategy.Trace, entry => entry.StartsWith("round:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshots_that_fail_for_a_moment_are_taken_again()
    {
        BatchTestNode node = new();
        node.Ledger.SnapshotFailures = 4;
        await using BatchStrategy strategy = node.Strategy(Options with { NodeOutageLimit = TimeSpan.FromSeconds(10) });

        Assert.Equal(16, (await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token)).Count);
    }

    [Fact]
    public async Task A_node_that_stays_unreachable_past_the_limit_fails_the_run()
    {
        BatchTestNode node = new();
        node.Ledger.SnapshotFailures = int.MaxValue;
        await using BatchStrategy strategy = node.Strategy(Options);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }

    [Fact]
    public async Task A_validated_ledger_that_stops_advancing_fails_the_run_instead_of_hanging()
    {
        BatchTestNode node = new();
        node.Sink.AutoClose = false;
        await using BatchStrategy strategy = node.Strategy(Options);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token).WaitAsync(TimeSpan.FromSeconds(20), Token));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Contains("stayed at", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sender_waiting_for_allowance_still_fails_when_the_node_stops()
    {
        BatchTestNode node = new();
        node.Sink.AutoClose = false;

        // Two batches ahead at most: the sender is parked in its allowance wait when the chain check dies.
        await using BatchStrategy strategy = node.Strategy(Options with { MaxAhead = 2 });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => strategy.SubmitAllAsync(BatchTestNode.Memos(80), null, Token).WaitAsync(TimeSpan.FromSeconds(20), Token));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }
}
