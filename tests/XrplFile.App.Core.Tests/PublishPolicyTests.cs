using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class PublishPolicyTests
{
    private static PublicationEstimate Estimate(long chunks, int lists, int mints = 0) =>
        new(
            Chunks: chunks,
            Blocks: 1,
            Lists: lists,
            Mints: mints,
            Transactions: chunks + lists + 1 + mints,
            FeeDropsPerTransaction: 12,
            FeeDropsTotal: (chunks + lists + 1 + mints) * 12,
            MaxAttempts: 3,
            FeeDropsWorstCase: (chunks + lists + 1 + mints) * 3 * 12,
            PeakReserveDrops: 0,
            BaseReserveDrops: 1_000_000,
            OwnerReserveDrops: 200_000,
            RequiredDrops: 2_000_000,
            BalanceDrops: 100_000_000,
            OwnerCount: 0);

    [Fact]
    public void The_memo_count_excludes_the_mints()
    {
        // 90 chunks, 1 list, 1 manifest = 92 memos, whatever the ticket machinery would add on top.
        Assert.Equal(92, PublishPolicy.MemoCount(Estimate(chunks: 90, lists: 1, mints: 40)));
    }

    [Fact]
    public void A_run_below_the_threshold_uses_sequence()
    {
        Assert.False(PublishPolicy.UsesTickets(Estimate(chunks: 97, lists: 1)));
    }

    [Fact]
    public void A_run_at_the_threshold_uses_tickets()
    {
        Assert.Equal(100, PublishPolicy.MemoCount(Estimate(chunks: 98, lists: 1)));
        Assert.True(PublishPolicy.UsesTickets(Estimate(chunks: 98, lists: 1)));
    }

    [Fact]
    public void A_run_above_the_threshold_uses_tickets()
    {
        Assert.True(PublishPolicy.UsesTickets(Estimate(chunks: 5000, lists: 5)));
    }

    [Fact]
    public void A_run_that_would_use_mints_to_cross_the_threshold_still_uses_sequence()
    {
        // 80 memos, and enough mints that PublicationEstimate.Transactions is past a hundred.
        PublicationEstimate estimate = Estimate(chunks: 78, lists: 1, mints: 30);

        Assert.True(estimate.Transactions > PublishPolicy.TicketThreshold);
        Assert.False(PublishPolicy.UsesTickets(estimate));
    }
}
