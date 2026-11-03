using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class CostSummaryTests
{
    private static CostSummary Summary(TimeSpan longest, long worstCase) =>
        new(new DurationRange(longest / 2, longest), 10, [new AccountCost(AccountRole.Writer, "w", 2_000_000, worstCase)]);

    [Fact]
    public void An_hour_and_one_XRP_are_the_lines()
    {
        Assert.False(Summary(TimeSpan.FromHours(1), 1_000_000).NeedsConfirmation);
        Assert.True(Summary(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1), 1_000_000).NeedsConfirmation);
        Assert.True(Summary(TimeSpan.FromMinutes(5), 1_000_001).NeedsConfirmation);
    }

    [Fact]
    public void Either_account_crossing_the_line_is_enough()
    {
        CostSummary summary = new(new DurationRange(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)), 10,
            [new AccountCost(AccountRole.Payer, "p", 3_000_000, 1_000_001), new AccountCost(AccountRole.Writer, "w", 2_000_000, 5)]);

        Assert.True(summary.NeedsConfirmation);
    }

    [Fact]
    public async Task A_resume_summary_prices_only_the_remaining_work()
    {
        BatchPublicationEstimate remaining = await PublicationEstimator.EstimateBatchAsync(
            new FakeLedgerSource(), "rWriter", "rPayer", 202_040_000, TestContext.Current.CancellationToken);
        PublishRequest request = new(202_040_000, "application/octet-stream", "big.bin", "ref-w", "rWriter", "testnet", "ref-p", "rPayer", "big.bin");

        CostSummary summary = CostSummary.OfResume(remaining, request);

        Assert.Equal(PublicationDuration.ForBatchRemaining(remaining), summary.Duration);
        Assert.Equal(remaining.Memos + remaining.Batches, summary.Transactions);
        Assert.Equivalent(CostSummary.Of(remaining, request).Accounts, summary.Accounts);
    }
}
