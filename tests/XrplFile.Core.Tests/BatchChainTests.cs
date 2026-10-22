using Xunit;

namespace XrplFile.Tests;

public class BatchChainTests
{
    private static readonly RoundPlan Plan = BatchGrouping.Plan([.. Enumerable.Range(0, 20)], writerSequence: 100, payerSequence: 500);

    private static readonly LedgerSnapshot Start = Snapshot(1000, writer: 100, payer: 500);

    private static LedgerSnapshot Snapshot(long ledger, uint writer, uint payer) =>
        new(ledger, new Dictionary<string, uint> { ["rW"] = writer, ["rP"] = payer });

    private static IReadOnlyList<BatchCandidate> Registered(int count) =>
        [.. Enumerable.Range(0, count).Select(k => new BatchCandidate(
            k, true, "rP", (uint)(500 + k), 1020, 165, [.. Plan.Batches[k].Inners.Select(inner => inner.MemoIndex)],
            new SignedTransaction("00", $"H{k}")))];

    [Fact]
    public void A_writer_that_kept_up_with_the_outers_the_payer_consumed_is_fine()
    {
        Assert.Null(BatchChain.Verdict(Plan, Start, Registered(3), Snapshot(1003, writer: 116, payer: 502), "rW", "rP"));
    }

    [Fact]
    public void A_writer_behind_what_the_consumed_outers_imply_has_diverged()
    {
        Assert.Equal(BatchChain.Diverged, BatchChain.Verdict(Plan, Start, Registered(3), Snapshot(1003, writer: 108, payer: 502), "rW", "rP"));
    }

    [Fact]
    public void A_payer_past_everything_registered_has_diverged()
    {
        Assert.Equal(BatchChain.Diverged, BatchChain.Verdict(Plan, Start, Registered(1), Snapshot(1003, writer: 108, payer: 502), "rW", "rP"));
    }

    [Fact]
    public void A_signed_outer_past_its_deadline_and_not_consumed_has_stalled_the_round()
    {
        Assert.Equal(BatchChain.Stalled, BatchChain.Verdict(Plan, Start, Registered(2), Snapshot(1021, writer: 108, payer: 501), "rW", "rP"));
    }

    [Fact]
    public void An_unsigned_next_batch_never_stalls_the_round()
    {
        Assert.Null(BatchChain.Verdict(Plan, Start, Registered(1), Snapshot(1021, writer: 108, payer: 501), "rW", "rP"));
    }
}
