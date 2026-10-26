namespace XrplFile.App.Core;

/// <summary>A span as the screens show it: seconds under a minute and a half, then the nearest minute, then hours and minutes.</summary>
public static class DurationText
{
    /// <summary>Rounded, not ceilinged: 64 s used to read "2 min".</summary>
    public static string Of(TimeSpan span)
    {
        double seconds = Math.Max(1, Math.Ceiling(span.TotalSeconds));
        if (seconds < 90) return $"{seconds:0} s";

        long minutes = (long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (minutes < 60) return $"{minutes} min";

        long rest = minutes % 60;
        return rest == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {rest} min";
    }

    /// <summary>"5 h 53 min to 7 h 4 min", or one figure when both ends read the same.</summary>
    public static string Of(DurationRange range)
    {
        string shortest = Of(range.Shortest);
        string longest = Of(range.Longest);

        return string.Equals(shortest, longest, StringComparison.Ordinal) ? shortest : $"{shortest} to {longest}";
    }
}
