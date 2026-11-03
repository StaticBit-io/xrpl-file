using XrplFile.App.Core.Localization;

namespace XrplFile.App.Core;

/// <summary>
/// Turns a count of ledgers into roughly how long that is, for display (design 5.2, 6: "how many
/// ledgers remain and roughly how long that is", "ledgers remaining, and about how long"). A ledger
/// number on its own means nothing to a person deciding whether to wait — 254 ledgers, the wait a
/// freshly funded account starts at, says nothing by itself, and the design says so in as many
/// words. Kept in one place, with its own tests, rather than written inline in markup: a component
/// holds no decisions, and the rough conversion factor below is one.
/// </summary>
public static class LedgerTiming
{
    /// <summary>
    /// Roughly how long one ledger takes to close in ordinary operation. Not a protocol-guaranteed
    /// value — rippled targets it, and network conditions move it — so everything built from it is
    /// an estimate for a person to read, never a value anything here waits against or decides from
    /// (design G8's own rule about cached figures applies here too).
    /// </summary>
    public static readonly TimeSpan ApproximateLedgerInterval = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Roughly how long <paramref name="ledgersRemaining"/> ledgers takes to close, or null when
    /// there is no count to estimate from.
    /// </summary>
    public static TimeSpan? Estimate(long? ledgersRemaining) =>
        ledgersRemaining is { } remaining ? ApproximateLedgerInterval * remaining : null;

    /// <summary>
    /// <paramref name="span"/> in words roughly precise enough to be useful and no more — "about 3
    /// hours", not a timestamp accurate to the second for a number that is itself only an estimate.
    /// Null in, null out.
    /// </summary>
    public static UiText? Describe(TimeSpan? span)
    {
        if (span is not { } value) return null;

        if (value < TimeSpan.FromMinutes(1)) return CommonText.ApproxLessThanMinute;

        if (value < TimeSpan.FromHours(1))
        {
            long minutes = Math.Max(1, (long)Math.Round(value.TotalMinutes));
            return UiText.Of(CommonText.ApproxMinutes, ("count", minutes));
        }

        if (value < TimeSpan.FromDays(1))
        {
            long hours = Math.Max(1, (long)Math.Round(value.TotalHours));
            return UiText.Of(CommonText.ApproxHours, ("count", hours));
        }

        long days = Math.Max(1, (long)Math.Round(value.TotalDays));
        return UiText.Of(CommonText.ApproxDays, ("count", days));
    }
}
