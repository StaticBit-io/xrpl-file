using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>The window around the screens: tabs, the lock button, and what is said when a screen or the start fails.</summary>
public static class ShellText
{
    public static readonly TextId StartFailed = Id();
    public static readonly TextId ScreenFailed = Id();
    public static readonly TextId Loading = Id();
    public static readonly TextId UnhandledError = Id();
    public static readonly TextId Reload = Id();

    public static readonly TextId MainNavigation = Id();
    public static readonly TextId LockVault = Id();
    public static readonly TextId Lock = Id();
    public static readonly TextId More = Id();

    public static readonly TextId TabAccounts = Id();
    public static readonly TextId TabPublish = Id();
    public static readonly TextId TabRead = Id();
    public static readonly TextId TabHistory = Id();
    public static readonly TextId TabSettings = Id();
    public static readonly TextId Learn = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Shell", name);
}
