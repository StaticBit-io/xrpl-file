using XrplFile.App.Core.Localization;

namespace XrplFile.App.Core;

/// <summary>
/// How far a publication got before it failed, as far as money is concerned. Decided by
/// <see cref="PublicationRun"/>, which alone knows whether its session has begun submitting.
/// </summary>
public enum RunFailurePoint
{
    /// <summary>A fresh run that failed before its session existed: nothing was submitted.</summary>
    BeforeSubmitting,

    /// <summary>
    /// A resume that failed before its session existed: this attempt submitted nothing, but the
    /// interrupted run it was picking up already had, and its state file still lists it.
    /// </summary>
    BeforeResuming,

    /// <summary>The session had begun submitting, so some transactions may already be paid for.</summary>
    WhileSubmitting,

    /// <summary>
    /// The session was reading the file back before its manifest: every chunk and block list had
    /// landed and been paid for, and the manifest had not been sent.
    /// </summary>
    WhileReadingBack,
}

/// <summary>
/// What a person is told when a publication fails (design 7). Each value gets its own sentence, and
/// the test over <see cref="Enum.GetValues{TEnum}"/> is what keeps that true: a value added to
/// <see cref="XrplFileError"/> fails the suite rather than reaching a screen as a number.
///
/// Three of them must never be conflated, because their remedies have nothing to do with each
/// other: <see cref="XrplFileError.NodeUnreachable"/> is a URL to check,
/// <see cref="XrplFileError.InsufficientBalance"/> is an account to fund, and
/// <see cref="XrplFileError.SubmitRefused"/> is neither.
/// </summary>
public static class PublishErrorMessages
{
    /// <summary>
    /// A node price too large for either strategy option type to hold (design F1: both price a
    /// transaction in an <see cref="int"/>). Not one of <see cref="XrplFileError"/>'s own cases --
    /// the library never throws for this on its own, since <see cref="PublicationEstimate"/> carries
    /// the raw <see cref="long"/> without judging it -- so it is this application's own sentence,
    /// shared by every place that has to say it: <see cref="EstimateModel"/> for a fresh estimate
    /// and <see cref="PublicationRun"/> for the one it re-takes before a resume, so a person reads
    /// the same words for the same refusal regardless of which screen it happened on.
    /// </summary>
    public static UiText FeeTooLargeForApplication => PublishText.ErrorFeeTooLargeForApplication;

    /// <summary>
    /// <see cref="XrplFileError.FeeTooHigh"/> raised before a run exists -- by an estimate, or by a
    /// fresh start that never wrote a state file -- where "listed under unfinished runs" would be false.
    /// </summary>
    public static UiText FeeTooHighNow => PublishText.ErrorFeeTooHighNow;

    /// <summary>
    /// <see cref="XrplFileError.NodeLacksHistory"/> raised while publishing (spec B30), or by a resume
    /// whose reconciliation waited for it (spec 5.2 step 3): the node's history lagged the run, which is
    /// not the reader's "history far enough back" problem.
    /// </summary>
    public static UiText HistoryLaggedWhilePublishing => PublishText.ErrorHistoryLagged;

    /// <summary>
    /// <see cref="XrplFileError.PayerInsufficientBalance"/> raised by a resume (spec 5.2 step 5): a Batch
    /// run cannot be moved to tickets (B18), and the screen offers another payer only when the recorded
    /// key has left the vault, so "choose another payer" would send the person to a control that is not there.
    /// </summary>
    public static UiText PayerShortBeforeResuming => PublishText.ErrorPayerShortBeforeResuming;

    /// <summary>
    /// <see cref="XrplFileError.BatchNotEnabled"/> raised by a resume: the run is a Batch run and stays one
    /// (B18), so "Choose Nobody under Paid by" is not a way out of it.
    /// </summary>
    public static UiText BatchNotEnabledBeforeResuming => PublishText.ErrorBatchOffBeforeResuming;

    private static UiText AccountInUse(AccountRole? role) => role switch
    {
        AccountRole.Writer => PublishText.ErrorAccountInUseWriter,
        AccountRole.Payer => PublishText.ErrorAccountInUsePayer,
        _ => PublishText.ErrorAccountInUseWriterOrPayer,
    };

    public static UiText For(XrplFileError error) => error switch
    {
        XrplFileError.NodeUnreachable => PublishText.ErrorNodeUnreachable,
        XrplFileError.InsufficientBalance => PublishText.ErrorInsufficientBalance,
        XrplFileError.SubmitRefused => PublishText.ErrorSubmitRefused,
        XrplFileError.BudgetExceeded => PublishText.ErrorBudgetExceeded,
        XrplFileError.FeeMismatch => PublishText.ErrorFeeMismatch,
        XrplFileError.ValidationTimeout => PublishText.ErrorValidationTimeout,
        XrplFileError.ResumeMismatch => PublishText.ErrorResumeMismatch,
        XrplFileError.SessionAlreadyRun => PublishText.ErrorSessionAlreadyRun,
        XrplFileError.TooLarge => PublishText.ErrorTooLarge,
        XrplFileError.InvalidLink => PublishText.ErrorInvalidLink,
        XrplFileError.ManifestNotFound => PublishText.ErrorManifestNotFound,
        XrplFileError.NodeLacksHistory => PublishText.ErrorNodeLacksHistory,
        XrplFileError.CorruptManifest => PublishText.ErrorCorruptManifest,
        XrplFileError.ListMismatch => PublishText.ErrorListMismatch,
        XrplFileError.BlockMismatch => PublishText.ErrorBlockMismatch,
        XrplFileError.ChunkMissing => PublishText.ErrorChunkMissing,
        XrplFileError.BatchNotEnabled => PublishText.ErrorBatchNotEnabled,
        XrplFileError.PayerInsufficientBalance => PublishText.ErrorPayerInsufficientBalance,
        XrplFileError.AccountInUse => AccountInUse(null),
        XrplFileError.FeeTooHigh => PublishText.ErrorFeeTooHigh,
        XrplFileError.WriterWindowConflict => PublishText.ErrorWriterWindowConflict,
        XrplFileError.ApprovalExceeded => PublishText.ErrorApprovalExceeded,
        XrplFileError.ManifestRejected => PublishText.ErrorManifestRejected,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "no sentence for this error"),
    };

    /// <summary>
    /// The same sentence, with the block named when the library said which one. The block is what
    /// makes a read-back failure actionable at all — "a block did not read back" and "block 41 did
    /// not read back" are different amounts of help.
    /// </summary>
    public static UiText For(XrplFileException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        UiText sentence = exception is { Error: XrplFileError.AccountInUse, Role: { } role }
            ? AccountInUse(role)
            : For(exception.Error);

        if (exception is { Error: XrplFileError.ManifestRejected, Link: { } link })
        {
            sentence = UiText.Of(PublishText.ErrorWithLink, ("sentence", sentence), ("link", link.ToString()));
        }

        return exception.Block is int block ? UiText.Of(PublishText.ErrorWithBlock, ("sentence", sentence), ("block", block)) : sentence;
    }

    /// <summary>
    /// Where a run failed, refined by the last stage the writer reported: a submitting run last seen
    /// in "read-back" had nothing left to pay for but its manifest. The writer reports "read-back"
    /// before its first read and "manifest" before it sends the manifest, so the stage is never stale
    /// in the direction that would understate what was spent.
    /// </summary>
    public static RunFailurePoint PointAt(RunFailurePoint point, string? lastStage) =>
        point == RunFailurePoint.WhileSubmitting && string.Equals(lastStage, "read-back", StringComparison.Ordinal)
            ? RunFailurePoint.WhileReadingBack
            : point;

    /// <summary>
    /// The sentence for a failed publication run. <see cref="XrplFileError.NodeUnreachable"/>'s own
    /// "Nothing was spent" is true only before the session submits anything; past that point a run
    /// may already have paid for transactions and minted tickets, and its state file lists it under
    /// unfinished runs. Resuming re-uses the chunks and block lists already in the ledger, but not a
    /// manifest whose run never recorded "done", so that is all the sentence promises. Every other
    /// error keeps its own sentence: none of them claims anything about what was spent.
    /// </summary>
    public static UiText ForRun(XrplFileException exception, RunFailurePoint point, bool usesTickets, long ownerReserveDrops)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.Error == XrplFileError.FeeTooHigh && point == RunFailurePoint.BeforeSubmitting) return FeeTooHighNow;

        if (exception.Error == XrplFileError.NodeLacksHistory && point is RunFailurePoint.WhileSubmitting or RunFailurePoint.WhileReadingBack or RunFailurePoint.BeforeResuming)
        {
            return HistoryLaggedWhilePublishing;
        }

        if (point == RunFailurePoint.BeforeResuming)
        {
            if (exception.Error == XrplFileError.PayerInsufficientBalance) return PayerShortBeforeResuming;
            if (exception.Error == XrplFileError.BatchNotEnabled) return BatchNotEnabledBeforeResuming;
        }

        if (exception.Error != XrplFileError.NodeUnreachable) return For(exception);

        return point switch
        {
            RunFailurePoint.BeforeSubmitting => For(exception),
            RunFailurePoint.BeforeResuming => PublishText.ErrorNodeUnreachableBeforeResuming,
            RunFailurePoint.WhileReadingBack => PublishText.ErrorNodeUnreachableWhileReadingBack,
            RunFailurePoint.WhileSubmitting => usesTickets
                ? UiText.Of(PublishText.ErrorNodeUnreachableWhileSubmittingTickets, ("reserve", new XrpAmount(ownerReserveDrops)))
                : PublishText.ErrorNodeUnreachableWhileSubmitting,
            _ => throw new ArgumentOutOfRangeException(nameof(point), point, "no sentence for this failure point"),
        };
    }
}
