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
    public const string FeeTooLargeForApplication = "The node is quoting a price this application cannot use. Try a different node.";

    /// <summary>
    /// <see cref="XrplFileError.FeeTooHigh"/> raised before a run exists -- by an estimate, or by a
    /// fresh start that never wrote a state file -- where "listed under unfinished runs" would be false.
    /// </summary>
    public const string FeeTooHighNow =
        "Network fees are above what this application publishes at right now. Try again when they are back down.";

    /// <summary>
    /// <see cref="XrplFileError.NodeLacksHistory"/> raised while publishing (spec B30), or by a resume
    /// whose reconciliation waited for it (spec 5.2 step 3): the node's history lagged the run, which is
    /// not the reader's "history far enough back" problem.
    /// </summary>
    public const string HistoryLaggedWhilePublishing =
        "The node's history fell behind the publication, so what landed could not be confirmed. The run is listed under "
        + "unfinished runs; resume it, or choose another node in Settings first.";

    /// <summary>
    /// <see cref="XrplFileError.PayerInsufficientBalance"/> raised by a resume (spec 5.2 step 5): a Batch
    /// run cannot be moved to tickets (B18), and the screen offers another payer only when the recorded
    /// key has left the vault, so "choose another payer" would send the person to a control that is not there.
    /// </summary>
    public const string PayerShortBeforeResuming =
        "The payer does not hold enough XRP to finish this publication. Fund it, then resume.";

    /// <summary>
    /// <see cref="XrplFileError.BatchNotEnabled"/> raised by a resume: the run is a Batch run and stays one
    /// (B18), so "Choose Nobody under Paid by" is not a way out of it.
    /// </summary>
    public const string BatchNotEnabledBeforeResuming =
        "This network no longer enables Batch; the run can be resumed once it does.";

    private static string AccountInUse(string role) =>
        $"The {role} account sent a transaction of its own during the publication. Is this key in use somewhere else? "
        + "The run is listed under unfinished runs; resume it once nothing else uses the key.";

    public static string For(XrplFileError error) => error switch
    {
        XrplFileError.NodeUnreachable =>
            "The node did not answer. Nothing was spent. Check the node URL in Settings and try again.",
        XrplFileError.InsufficientBalance =>
            "The account does not hold enough to cover this publication. Fund it, or publish a smaller file.",
        XrplFileError.SubmitRefused =>
            "The node refused a transaction. Neither funding the account nor retrying will change that on its own.",
        XrplFileError.BudgetExceeded =>
            "The run would have cost more than it was started with, so it was stopped rather than allowed to overspend.",
        XrplFileError.FeeMismatch =>
            "The node's price changed between the estimate and the run. Take a fresh estimate and start again.",
        XrplFileError.ValidationTimeout =>
            "Transactions were submitted but not seen validated in time. The run is listed under unfinished runs, and "
            + "resuming does not pay again for the chunks and block lists that already landed.",
        XrplFileError.ResumeMismatch =>
            "This is not the file, or not the key, that the interrupted run started with.",
        XrplFileError.SessionAlreadyRun =>
            "A publication session was run twice. That is a defect in this application, not something you did.",
        XrplFileError.TooLarge => PublishModel.TooLargeMessage,
        XrplFileError.InvalidLink =>
            "The link is not a valid xrpl-file link.",
        XrplFileError.ManifestNotFound =>
            "The node has no manifest at that transaction hash.",
        XrplFileError.NodeLacksHistory =>
            "The node does not keep history far enough back to read this file.",
        XrplFileError.CorruptManifest =>
            "The manifest read back from the ledger is not one this version understands.",
        XrplFileError.ListMismatch =>
            "A block list read back from the ledger does not match what was written.",
        XrplFileError.BlockMismatch =>
            "A block read back from the ledger does not match what was written. The manifest was not published, so no link points at it.",
        XrplFileError.ChunkMissing =>
            "A chunk that was written could not be read back from the ledger.",
        XrplFileError.BatchNotEnabled =>
            "This network does not enable Batch. Choose Nobody under Paid by to publish with tickets.",
        XrplFileError.PayerInsufficientBalance =>
            "The payer does not hold enough XRP for this publication. Fund it, or choose another payer.",
        XrplFileError.AccountInUse => AccountInUse("writer or payer"),
        XrplFileError.FeeTooHigh =>
            "Network fees rose above what this publication was approved for. The run is listed under unfinished runs; "
            + "resume it when fees are back down.",
        XrplFileError.WriterWindowConflict =>
            "The writer account carries other data for this file's chunks — was it used for another publication while this one was interrupted? Publish this file again from a fresh writer key.",
        XrplFileError.ApprovalExceeded =>
            "Fees or balances changed since this was approved. Estimate it again.",
        XrplFileError.ManifestRejected =>
            "The manifest landed, but reading it back did not give this file's description, so its link must not be shared. Publish the file again from a fresh writer key.",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "no sentence for this error"),
    };

    /// <summary>
    /// The same sentence, with the block named when the library said which one. The block is what
    /// makes a read-back failure actionable at all — "a block did not read back" and "block 41 did
    /// not read back" are different amounts of help.
    /// </summary>
    public static string For(XrplFileException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string sentence = exception is { Error: XrplFileError.AccountInUse, Role: { } role }
            ? AccountInUse(role == AccountRole.Writer ? "writer" : "payer")
            : For(exception.Error);

        if (exception is { Error: XrplFileError.ManifestRejected, Link: { } link }) sentence = $"{sentence} The link that was written: {link}.";

        return exception.Block is int block ? $"{sentence} (block {block})" : sentence;
    }

    /// <summary>
    /// The sentence for a failed publication run. <see cref="XrplFileError.NodeUnreachable"/>'s own
    /// "Nothing was spent" is true only before the session submits anything; past that point a run
    /// may already have paid for transactions and minted tickets, and its state file lists it under
    /// unfinished runs. Resuming re-uses the chunks and block lists already in the ledger, but not a
    /// manifest whose run never recorded "done", so that is all the sentence promises. Every other
    /// error keeps its own sentence: none of them claims anything about what was spent.
    /// </summary>
    public static string ForRun(XrplFileException exception, RunFailurePoint point, bool usesTickets, long ownerReserveDrops)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.Error == XrplFileError.FeeTooHigh && point == RunFailurePoint.BeforeSubmitting) return FeeTooHighNow;

        if (exception.Error == XrplFileError.NodeLacksHistory && point is RunFailurePoint.WhileSubmitting or RunFailurePoint.BeforeResuming)
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
            RunFailurePoint.BeforeResuming =>
                "The node did not answer. This attempt spent nothing, and the run is still listed under unfinished runs. "
                + "Check the node URL in Settings and try again.",
            RunFailurePoint.WhileSubmitting =>
                "The node stopped answering partway through the run. Transactions sent before that may already be in the "
                + (usesTickets
                    ? $"ledger and paid for, and tickets it reserved may be on this account, each holding {XrpAmount.Format(ownerReserveDrops)} "
                        + "of owner reserve until a later publication from this account spends them or the account is closed. "
                    : "ledger and paid for. ")
                + "The run is listed under unfinished runs: check the node URL in Settings, then resume it. Resuming does "
                + "not pay again for the chunks and block lists that already landed.",
            _ => throw new ArgumentOutOfRangeException(nameof(point), point, "no sentence for this failure point"),
        };
    }
}
