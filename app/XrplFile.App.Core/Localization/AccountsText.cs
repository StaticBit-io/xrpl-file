using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>The Accounts list, its rows, the delete dialog and the New account screen.</summary>
public static class AccountsText
{
    public static readonly TextId Title = Id();
    public static readonly TextId ImportKeyButton = Id();
    public static readonly TextId NewAccountButton = Id();

    public static readonly TextId PublishingTitle = Id();
    public static readonly TextId PublishingDescription = Id();
    public static readonly TextId PublishingEmpty = Id();
    public static readonly TextId KeysTitle = Id();
    public static readonly TextId KeysDescription = Id();
    public static readonly TextId KeysEmpty = Id();

    public static readonly TextId ColumnAccount = Id();
    public static readonly TextId ColumnNetwork = Id();
    public static readonly TextId ColumnBalance = Id();
    public static readonly TextId ColumnStatus = Id();

    public static readonly TextId NotExportedTag = Id();
    public static readonly TextId NotExportedHint = Id();

    public static readonly TextId BalanceAmount = Id();
    public static readonly TextId BalanceChecking = Id();
    public static readonly TextId BalanceNotRead = Id();

    public static readonly TextId StatusNotRead = Id();
    public static readonly TextId StatusNotChecked = Id();
    public static readonly TextId StatusNotOnLedger = Id();
    public static readonly TextId StatusClosableIn = Id();
    public static readonly TextId StatusAtLedger = Id();
    public static readonly TextId StatusReadyToClose = Id();
    public static readonly TextId StatusClosed = Id();
    public static readonly TextId StatusClosedSent = Id();

    public static readonly TextId FundButton = Id();
    public static readonly TextId MoreActionsFor = Id();
    public static readonly TextId ActionsFor = Id();
    public static readonly TextId MenuCheckBalance = Id();
    public static readonly TextId MenuRevealSecret = Id();
    public static readonly TextId MenuExportKey = Id();
    public static readonly TextId MenuCloseAccount = Id();
    public static readonly TextId MenuDeleteKey = Id();

    public static readonly TextId DeleteTitle = Id();
    public static readonly TextId DeleteIntro = Id();
    public static readonly TextId DeleteNeverExported = Id();
    public static readonly TextId DeleteKeyButton = Id();
    public static readonly TextId DismissButton = Id();

    public static readonly TextId ReadFailed = Id();

    public static readonly TextId NewTitle = Id();
    public static readonly TextId NewIntro = Id();
    public static readonly TextId NewGenerateButton = Id();
    public static readonly TextId NewNeedsLabel = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Accounts", name);
}
