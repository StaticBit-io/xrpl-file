using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class PublicationDurationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_spec_example_of_a_1_7_GB_file_takes_about_six_to_seven_hours_through_Batch()
    {
        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(new FakeLedgerSource(), "rWriter", "rPayer", 1_689_809_534, Token);

        DurationRange range = PublicationDuration.ForBatch(estimate);

        // 1,689,977 memos at 85 and 70 a second, 106 rounds of 12 s, and four sampled blocks of 1,024 records at 0.7 s per 400.
        double fixedPart = 106 * 12 + 4 * 1_024 * 0.7 / 400;
        Assert.Equal(1_689_977 / 85.0 + fixedPart, range.Shortest.TotalSeconds, precision: 3);
        Assert.Equal(1_689_977 / 70.0 + fixedPart, range.Longest.TotalSeconds, precision: 3);
        Assert.Equal("5 h 53 min to 7 h 4 min", DurationText.Of(range));
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
