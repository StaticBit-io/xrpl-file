using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>The Settings screen.</summary>
public static class SettingsText
{
    public static readonly TextId LanguageTitle = Id();
    public static readonly TextId LanguageLabel = Id();
    public static readonly TextId LanguageNotRemembered = Id();

    public static readonly TextId Title = Id();

    public static readonly TextId VaultLocationTitle = Id();
    public static readonly TextId VaultLocationIntro = Id();
    public static readonly TextId ShowInFileManager = Id();

    public static readonly TextId DifferentVaultTitle = Id();
    public static readonly TextId DifferentVaultIntro = Id();
    public static readonly TextId DifferentVaultDeviceUnlock = Id();
    public static readonly TextId ChooseVaultFile = Id();

    public static readonly TextId RememberTitle = Id();
    public static readonly TextId RememberIntro = Id();
    public static readonly TextId DeviceUnlockSwitchLabel = Id();

    public static readonly TextId NodeUrlsTitle = Id();
    public static readonly TextId NodeUrlsIntro = Id();
    public static readonly TextId CustomUrlFor = Id();
    public static readonly TextId SaveNodeUrls = Id();
    public static readonly TextId AddNetworkLabel = Id();
    public static readonly TextId AddNetworkHint = Id();
    public static readonly TextId NewNetworkNamePlaceholder = Id();
    public static readonly TextId AddNetworkButton = Id();
    public static readonly TextId NodesSaved = Id();

    public static readonly TextId ChangePasswordButton = Id();

    public static readonly TextId NodeNotANetwork = Id();
    public static readonly TextId NodeUrlMissing = Id();
    public static readonly TextId NodeUrlNotWebSocket = Id();
    public static readonly TextId NodeRowUrlNotWebSocket = Id();

    public static readonly TextId ServerHost = Id();
    public static readonly TextId ServerRecentHistoryOnly = Id();
    public static readonly TextId ServerBuiltIn = Id();
    public static readonly TextId ServerCustom = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Settings", name);
}
