using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>The tutorials: the topic list, the bubble controls, and the title and body of every step.</summary>
public static class TutorialText
{
    public static readonly TextId ListTitle = Id();
    public static readonly TextId ListOfferTitle = Id();
    public static readonly TextId ListOfferIntro = Id();
    public static readonly TextId ListNotNow = Id();
    public static readonly TextId ListNotNowHint = Id();
    public static readonly TextId ListClose = Id();
    public static readonly TextId BusyPublishing = Id();
    public static readonly TextId BusyReading = Id();

    public static readonly TextId TopicCreateAccount = Id();
    public static readonly TextId TopicAddKey = Id();
    public static readonly TextId TopicExportKey = Id();
    public static readonly TextId TopicPublish = Id();
    public static readonly TextId TopicRead = Id();

    public static readonly TextId StepOf = Id();
    public static readonly TextId Next = Id();
    public static readonly TextId Finish = Id();
    public static readonly TextId Leave = Id();
    public static readonly TextId ShowCreateAccount = Id();
    public static readonly TextId LookupFailedNote = Id();
    public static readonly TextId ReadFailedNote = Id();

    public static readonly TextId CreateAccountSectionTitle = Id();
    public static readonly TextId CreateAccountSectionBody = Id();
    public static readonly TextId CreateAccountNewButtonTitle = Id();
    public static readonly TextId CreateAccountNewButtonBody = Id();
    public static readonly TextId CreateAccountFieldsTitle = Id();
    public static readonly TextId CreateAccountFieldsBody = Id();
    public static readonly TextId CreateAccountGenerateTitle = Id();
    public static readonly TextId CreateAccountGenerateBody = Id();
    public static readonly TextId CreateAccountFundTitle = Id();
    public static readonly TextId CreateAccountFundBody = Id();
    public static readonly TextId CreateAccountDoneTitle = Id();
    public static readonly TextId CreateAccountDoneBody = Id();

    public static readonly TextId AddKeyImportButtonTitle = Id();
    public static readonly TextId AddKeyImportButtonBody = Id();
    public static readonly TextId AddKeySeedTitle = Id();
    public static readonly TextId AddKeySeedBody = Id();
    public static readonly TextId AddKeyBackupTitle = Id();
    public static readonly TextId AddKeyBackupBody = Id();
    public static readonly TextId AddKeyDoneTitle = Id();
    public static readonly TextId AddKeyDoneBody = Id();

    public static readonly TextId ExportKeyRowTitle = Id();
    public static readonly TextId ExportKeyRowBody = Id();
    public static readonly TextId ExportKeyMenuTitle = Id();
    public static readonly TextId ExportKeyMenuBody = Id();
    public static readonly TextId ExportKeyPasswordTitle = Id();
    public static readonly TextId ExportKeyPasswordBody = Id();
    public static readonly TextId ExportKeyDoneTitle = Id();
    public static readonly TextId ExportKeyDoneBody = Id();
    public static readonly TextId ExportKeyNoKeyTitle = Id();
    public static readonly TextId ExportKeyNoKeyBody = Id();

    public static readonly TextId PublishFileTitle = Id();
    public static readonly TextId PublishFileBody = Id();
    public static readonly TextId PublishFromTitle = Id();
    public static readonly TextId PublishFromBody = Id();
    public static readonly TextId PublishPayerTitle = Id();
    public static readonly TextId PublishPayerBody = Id();
    public static readonly TextId PublishEstimateTitle = Id();
    public static readonly TextId PublishEstimateBody = Id();
    public static readonly TextId PublishHistoryTitle = Id();
    public static readonly TextId PublishHistoryBody = Id();
    public static readonly TextId PublishDoneTitle = Id();
    public static readonly TextId PublishDoneBody = Id();

    public static readonly TextId ReadLinkRealTitle = Id();
    public static readonly TextId ReadLinkRealBody = Id();
    public static readonly TextId ReadLinkShownTitle = Id();
    public static readonly TextId ReadLinkShownBody = Id();
    public static readonly TextId ReadResultTitle = Id();
    public static readonly TextId ReadResultBody = Id();
    public static readonly TextId ReadSaveTitle = Id();
    public static readonly TextId ReadSaveBody = Id();
    public static readonly TextId ReadDoneTitle = Id();
    public static readonly TextId ReadDoneBody = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Tutorial", name);
}
