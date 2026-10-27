using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class PublicationDurationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_spec_example_of_a_1_7_GB_file_takes_about_three_to_seven_hours_through_Batch()
    {
        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(new FakeLedgerSource(), "rWriter", "rPayer", 1_689_809_534, Token);

        DurationRange range = PublicationDuration.ForBatch(estimate);

        // 1,689,977 memos at 160 and 70 a second, 106 rounds of 12 s, four sampled blocks of 1,024 records at 0.7 s per 400,
        // and the hash pass over 1,689,810 chunks at 9,000 and 4,500 a second.
        double fixedPart = 106 * 12 + 4 * 1_024 * 0.7 / 400;
        Assert.Equal(1_689_977 / 160.0 + fixedPart + 1_689_810 / 9_000.0, range.Shortest.TotalSeconds, precision: 3);
        Assert.Equal(1_689_977 / 70.0 + fixedPart + 1_689_810 / 4_500.0, range.Longest.TotalSeconds, precision: 3);
        Assert.Equal("3 h 21 min to 7 h 10 min", DurationText.Of(range));
    }

    [Fact]
    public async Task The_300_MB_acceptance_file_is_priced_around_what_it_took()
    {
        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(new FakeLedgerSource(), "rWriter", "rPayer", 300_343_426, Token);

        DurationRange range = PublicationDuration.ForBatch(estimate);

        TimeSpan netWallTime = TimeSpan.FromSeconds(37 * 60 + 53);
        Assert.True(range.Shortest <= netWallTime);
        Assert.True(netWallTime <= range.Longest);
        Assert.True(range.Longest > CostSummary.ConfirmationDuration);
    }

    [Fact]
    public async Task A_resume_quote_prices_only_the_work_after_continue()
    {
        BatchPublicationEstimate remaining = await PublicationEstimator.EstimateBatchAsync(new FakeLedgerSource(), "rWriter", "rPayer", 202_040_000, Token);

        DurationRange quote = PublicationDuration.ForBatchRemaining(remaining);
        DurationRange fresh = PublicationDuration.ForBatch(remaining);

        Assert.Equal(202_040 / 9_000.0, (fresh.Shortest - quote.Shortest).TotalSeconds, precision: 3);
        Assert.Equal(202_040 / 4_500.0, (fresh.Longest - quote.Longest).TotalSeconds, precision: 3);
    }

    [Fact]
    public async Task A_ticketed_file_is_priced_at_two_and_one_memos_a_second_with_a_full_read_back()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(new FakeLedgerSource(), "rWriter", 1_000_000, 100, Token);

        DurationRange range = PublicationDuration.ForOneAccount(estimate);

        Assert.Equal(1_001 / 2.0 + 1_000 * 0.7 / 400, range.Shortest.TotalSeconds, precision: 3);
        Assert.Equal(1_001 / 1.0 + 1_000 * 0.7 / 400, range.Longest.TotalSeconds, precision: 3);
        Assert.Equal("8 min to 17 min", DurationText.Of(range));
    }

    [Fact]
    public async Task A_sequence_file_is_one_memo_per_ledger()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(new FakeLedgerSource(), "rWriter", 50_000, 100, Token);

        DurationRange range = PublicationDuration.ForOneAccount(estimate);

        Assert.Equal(range.Shortest, range.Longest);
        Assert.Equal(51 * 4 + 50 * 0.7 / 400, range.Longest.TotalSeconds, precision: 3);
        Assert.Equal("3 min", DurationText.Of(range));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(1_651, 4)]
    public void The_sample_size_follows_the_writer(int blocks, int sampled)
    {
        Assert.Equal(sampled, PublicationDuration.SampledBlocks(blocks));
    }
}
