using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>Phrases shared by several screens: units, durations, generic buttons.</summary>
public static class CommonText
{
    public static readonly TextId DurationSeconds = Id();
    public static readonly TextId DurationMinutes = Id();
    public static readonly TextId DurationHours = Id();
    public static readonly TextId DurationHoursMinutes = Id();
    public static readonly TextId DurationRange = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Common", name);
}
