using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>The published-files list and the sentences about the list file itself.</summary>
public static class HistoryText
{
    public static readonly TextId Title = Id();
    public static readonly TextId ClearButton = Id();
    public static readonly TextId Lede = Id();
    public static readonly TextId Empty = Id();
    public static readonly TextId PaidBy = Id();
    public static readonly TextId OpenButton = Id();
    public static readonly TextId ForgetButton = Id();

    public static readonly TextId ListUnreadable = Id();
    public static readonly TextId ListNotWritten = Id();
    public static readonly TextId ListReplaced = Id();
    public static readonly TextId HeldBack = Id();
    public static readonly TextId FailureAndNote = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("History", name);
}
