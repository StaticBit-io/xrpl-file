using Xunit;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// Design 5.2/6: a countdown must show how long the wait roughly is, not only how many ledgers
/// remain -- 254 ledgers, the wait a freshly funded account starts at, means nothing to a person
/// reading it. The arithmetic lives here, in a model, rather than in either razor component that
/// displays it.
/// </summary>
public sealed class LedgerTimingTests
{
    [Fact]
    public void No_count_estimates_to_nothing()
    {
        Assert.Null(LedgerTiming.Estimate(null));
        Assert.Null(LedgerTiming.Describe(null));
    }

    [Fact]
    public void A_fresh_accounts_wait_is_about_sixteen_minutes()
    {
        // 254 ledgers, the wait DeletionSequenceDelta starts a freshly funded account at, times the
        // four-second interval this estimate is built from.
        TimeSpan? estimate = LedgerTiming.Estimate(254);

        Assert.Equal(TimeSpan.FromSeconds(254 * 4), estimate);
        Assert.Equal("about 17 minutes", LedgerTiming.Describe(estimate));
    }

    [Fact]
    public void Zero_ledgers_remaining_is_still_an_estimate_not_null()
    {
        Assert.Equal(TimeSpan.Zero, LedgerTiming.Estimate(0));
        Assert.Equal("less than a minute", LedgerTiming.Describe(TimeSpan.Zero));
    }

    [Fact]
    public void A_handful_of_ledgers_reads_as_under_a_minute()
    {
        Assert.Equal("less than a minute", LedgerTiming.Describe(LedgerTiming.Estimate(1)));
    }

    [Fact]
    public void An_hour_long_wait_is_described_in_hours()
    {
        // 1,000 ledgers is a little over an hour at four seconds each.
        string? description = LedgerTiming.Describe(LedgerTiming.Estimate(1_000));

        Assert.Equal("about 1 hour", description);
    }

    [Fact]
    public void A_multi_day_wait_is_described_in_days()
    {
        // 30,000 ledgers is a little over a day at four seconds each.
        string? description = LedgerTiming.Describe(LedgerTiming.Estimate(30_000));

        Assert.Equal("about 1 day", description);
    }
}
