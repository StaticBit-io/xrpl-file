using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>Publishing: so far only the sentences for a failed publication that other screens (funding, closing) also show.</summary>
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

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Publish", name);
}
