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

    public static readonly TextId ApproxLessThanMinute = Id();
    public static readonly TextId ApproxMinutes = Id();
    public static readonly TextId ApproxHours = Id();
    public static readonly TextId ApproxDays = Id();

    public static readonly TextId Cancel = Id();
    public static readonly TextId Done = Id();
    public static readonly TextId CopyLinkButton = Id();

    public static readonly TextId FieldLabel = Id();
    public static readonly TextId FieldNetwork = Id();

    public static readonly TextId Bytes = Id();

    // The "Save as type" entries of the system file dialogs.
    public static readonly TextId FileTypeBackup = Id();
    public static readonly TextId FileTypeAny = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Common", name);
}
