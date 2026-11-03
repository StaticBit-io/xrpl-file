using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>The Publish screen (file, key, payer) and the sentences for a failed publication, which funding and closing show too.</summary>
public static class PublishText
{
    public static readonly TextId ErrorNodeUnreachable = Id();
    public static readonly TextId ErrorInsufficientBalance = Id();
    public static readonly TextId ErrorSubmitRefused = Id();
    public static readonly TextId ErrorBudgetExceeded = Id();
    public static readonly TextId ErrorFeeMismatch = Id();
    public static readonly TextId ErrorValidationTimeout = Id();
    public static readonly TextId ErrorResumeMismatch = Id();
    public static readonly TextId ErrorSessionAlreadyRun = Id();
    public static readonly TextId ErrorTooLarge = Id();
    public static readonly TextId ErrorInvalidLink = Id();
    public static readonly TextId ErrorManifestNotFound = Id();
    public static readonly TextId ErrorNodeLacksHistory = Id();
    public static readonly TextId ErrorCorruptManifest = Id();
    public static readonly TextId ErrorListMismatch = Id();
    public static readonly TextId ErrorBlockMismatch = Id();
    public static readonly TextId ErrorChunkMissing = Id();
    public static readonly TextId ErrorBatchNotEnabled = Id();
    public static readonly TextId ErrorPayerInsufficientBalance = Id();
    public static readonly TextId ErrorAccountInUseWriterOrPayer = Id();
    public static readonly TextId ErrorAccountInUseWriter = Id();
    public static readonly TextId ErrorAccountInUsePayer = Id();
    public static readonly TextId ErrorFeeTooHigh = Id();
    public static readonly TextId ErrorWriterWindowConflict = Id();
    public static readonly TextId ErrorApprovalExceeded = Id();
    public static readonly TextId ErrorManifestRejected = Id();
    public static readonly TextId ErrorWithLink = Id();
    public static readonly TextId ErrorWithBlock = Id();

    public static readonly TextId ErrorFeeTooLargeForApplication = Id();
    public static readonly TextId ErrorFeeTooHighNow = Id();
    public static readonly TextId ErrorHistoryLagged = Id();
    public static readonly TextId ErrorPayerShortBeforeResuming = Id();
    public static readonly TextId ErrorBatchOffBeforeResuming = Id();
    public static readonly TextId ErrorNodeUnreachableBeforeResuming = Id();
    public static readonly TextId ErrorNodeUnreachableWhileReadingBack = Id();
    public static readonly TextId ErrorNodeUnreachableWhileSubmitting = Id();
    public static readonly TextId ErrorNodeUnreachableWhileSubmittingTickets = Id();

    public static readonly TextId Title = Id();
    public static readonly TextId FileTitle = Id();
    public static readonly TextId ChooseFileButton = Id();
    public static readonly TextId ChooseAnotherButton = Id();
    public static readonly TextId NoFileChosen = Id();
    public static readonly TextId AccountTitle = Id();
    public static readonly TextId NoAccounts = Id();
    public static readonly TextId PaidByLabel = Id();
    public static readonly TextId PayerChoice = Id();
    public static readonly TextId PayerNobody = Id();
    public static readonly TextId PayerNobodyNote = Id();
    public static readonly TextId PayerBatchUnavailable = Id();
    public static readonly TextId EstimateButton = Id();

    public static readonly TextId PayerRequired = Id();
    public static readonly TextId ChooseFile = Id();
    public static readonly TextId ChooseKey = Id();
    public static readonly TextId FileEmpty = Id();
    public static readonly TextId FileUnreadable = Id();
    public static readonly TextId FileDialogFailed = Id();
    public static readonly TextId PayerKeyGone = Id();
    public static readonly TextId NetworkNotLinkable = Id();
    public static readonly TextId NoNodeUrl = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Publish", name);
}
