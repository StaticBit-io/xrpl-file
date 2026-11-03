using XrplFile.App.Core.Localization;

namespace XrplFile.App.Core;

/// <summary>A span as the screens show it: seconds under a minute and a half, then the nearest minute, then hours and minutes.</summary>
public static class DurationText
{
    /// <summary>Rounded, not ceilinged: 64 s used to read "2 min".</summary>
    public static UiText Of(TimeSpan span)
    {
        long seconds = (long)Math.Max(1, Math.Ceiling(span.TotalSeconds));
        if (seconds < 90) return UiText.Of(CommonText.DurationSeconds, ("count", seconds));

        long minutes = (long)Math.Round(seconds / 60d, MidpointRounding.AwayFromZero);
        if (minutes < 60) return UiText.Of(CommonText.DurationMinutes, ("count", minutes));

        long rest = minutes % 60;
        return rest == 0
            ? UiText.Of(CommonText.DurationHours, ("count", minutes / 60))
            : UiText.Of(CommonText.DurationHoursMinutes, ("hours", minutes / 60), ("minutes", rest));
    }

    /// <summary>"5 h 53 min to 7 h 4 min", or one figure when both ends read the same.</summary>
    public static UiText Of(DurationRange range)
    {
        UiText shortest = Of(range.Shortest);
        UiText longest = Of(range.Longest);

        return shortest.Equals(longest) ? shortest : UiText.Of(CommonText.DurationRange, ("shortest", shortest), ("longest", longest));
    }
}
