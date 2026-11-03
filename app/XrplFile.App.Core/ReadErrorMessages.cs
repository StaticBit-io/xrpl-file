using XrplFile.App.Core.Localization;

namespace XrplFile.App.Core;

/// <summary>
/// What a person is told when reading a link fails (design 4.3). Deliberately not
/// <see cref="PublishErrorMessages"/>: the same <see cref="XrplFileError"/> values mean different
/// things depending on which side of the ledger you are standing on. To a publisher
/// <see cref="XrplFileError.BlockMismatch"/> means the ledger did not return what was just written
/// to it; to a reader it means the file that is in the ledger does not match its own manifest, and
/// nobody wrote anything just now.
/// </summary>
public static class ReadErrorMessages
{
    /// <summary>
    /// The temporary file a read was assembling could not be written to, for a filesystem reason
    /// (<see cref="ReadRun.RunAsync"/>'s own <c>IOException</c>/<c>UnauthorizedAccessException</c>
    /// catch). Not keyed by <see cref="XrplFileError"/>: nothing in that enum describes a local disk
    /// failure, and inventing a member for one would describe something the library never raises.
    /// </summary>
    public static UiText TemporaryWriteFailed => ReadText.ErrorTemporaryWriteFailed;

    /// <summary>
    /// A read failed for a reason this class has no more specific sentence for — either an exception
    /// <see cref="ReadRun.RunAsync"/>'s catch-all caught, or an <see cref="XrplFileError"/> added
    /// since this class was last taught a sentence for one (<see cref="For(XrplFileError)"/>'s own
    /// <c>default</c> throws; this is what that throw is turned into before it can escape). The same
    /// sentence serves both call sites: to the person reading, "this application does not know why"
    /// means the same thing either way.
    /// </summary>
    public static UiText ReadFailedGeneric => ReadText.ErrorGeneric;

    /// <summary>
    /// The save dialog itself could not open or write the chosen file — a full disk, a revoked
    /// permission, a destination that went away while the dialog was open
    /// (<see cref="ReadRun.SaveAsync"/>'s own <c>IOException</c>/<c>UnauthorizedAccessException</c>
    /// catch). Distinct from <see cref="TemporaryWriteFailed"/>: that one is about the read's own
    /// scratch file, before saving was ever asked for; this one is about the place the person chose.
    /// </summary>
    public static UiText SaveFailed => ReadText.ErrorSaveFailed;

    /// <summary>
    /// The save dialog failed for a reason <see cref="ReadRun.SaveAsync"/>'s catch-all caught rather
    /// than its own named exceptions. Deliberately not the same text as <see cref="ReadFailedGeneric"/>:
    /// this failure is specific to saving a read that already succeeded, and telling the person "while
    /// saving" is the difference between "try reading it again" and "try saving it again".
    /// </summary>
    public static UiText SaveFailedGeneric => ReadText.ErrorSaveFailedGeneric;

    public static UiText For(XrplFileError error) => error switch
    {
        XrplFileError.InvalidLink => ReadText.ErrorInvalidLink,
        XrplFileError.ManifestNotFound => ReadText.ErrorManifestNotFound,
        XrplFileError.NodeLacksHistory => ReadText.ErrorNodeLacksHistory,
        XrplFileError.CorruptManifest => ReadText.ErrorCorruptManifest,
        XrplFileError.ListMismatch => ReadText.ErrorListMismatch,
        XrplFileError.BlockMismatch => ReadText.ErrorBlockMismatch,
        XrplFileError.ChunkMissing => ReadText.ErrorChunkMissing,
        XrplFileError.TooLarge => ReadText.ErrorTooLarge,
        XrplFileError.NodeUnreachable => ReadText.ErrorNodeUnreachable,
        XrplFileError.InsufficientBalance or XrplFileError.BudgetExceeded or XrplFileError.FeeMismatch
            or XrplFileError.SubmitRefused or XrplFileError.ValidationTimeout or XrplFileError.ResumeMismatch
            or XrplFileError.SessionAlreadyRun or XrplFileError.BatchNotEnabled or XrplFileError.PayerInsufficientBalance
            or XrplFileError.AccountInUse or XrplFileError.FeeTooHigh or XrplFileError.WriterWindowConflict
            or XrplFileError.ManifestRejected or XrplFileError.ApprovalExceeded => ReadText.ErrorNotCausedByReading,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "no sentence for this error"),
    };

    /// <summary>The same sentence, with the block named when the library said which one.</summary>
    public static UiText For(XrplFileException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        UiText sentence = For(exception.Error);
        return exception.Block is int block ? UiText.Of(ReadText.ErrorWithBlock, ("sentence", sentence), ("block", block)) : sentence;
    }
}
