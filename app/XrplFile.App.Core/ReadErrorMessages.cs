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
    public const string TemporaryWriteFailed =
        "The file could not be written to a temporary location while it was being read.";

    /// <summary>
    /// A read failed for a reason this class has no more specific sentence for — either an exception
    /// <see cref="ReadRun.RunAsync"/>'s catch-all caught, or an <see cref="XrplFileError"/> added
    /// since this class was last taught a sentence for one (<see cref="For(XrplFileError)"/>'s own
    /// <c>default</c> throws; this is what that throw is turned into before it can escape). The same
    /// sentence serves both call sites: to the person reading, "this application does not know why"
    /// means the same thing either way.
    /// </summary>
    public const string ReadFailedGeneric =
        "Something went wrong that this application does not have a specific message for.";

    /// <summary>
    /// The save dialog itself could not open or write the chosen file — a full disk, a revoked
    /// permission, a destination that went away while the dialog was open
    /// (<see cref="ReadRun.SaveAsync"/>'s own <c>IOException</c>/<c>UnauthorizedAccessException</c>
    /// catch). Distinct from <see cref="TemporaryWriteFailed"/>: that one is about the read's own
    /// scratch file, before saving was ever asked for; this one is about the place the person chose.
    /// </summary>
    public const string SaveFailed =
        "The file could not be opened to save, or could not be written to where you chose.";

    /// <summary>
    /// The save dialog failed for a reason <see cref="ReadRun.SaveAsync"/>'s catch-all caught rather
    /// than its own named exceptions. Deliberately not the same text as <see cref="ReadFailedGeneric"/>:
    /// this failure is specific to saving a read that already succeeded, and telling the person "while
    /// saving" is the difference between "try reading it again" and "try saving it again".
    /// </summary>
    public const string SaveFailedGeneric =
        "Something went wrong while saving that this application does not have a specific message for.";

    public static string For(XrplFileError error) => error switch
    {
        XrplFileError.InvalidLink =>
            "That is not an xrpl-file link.",
        XrplFileError.ManifestNotFound =>
            "This network has no transaction at that hash. The link may be wrong, or it may belong to a different network.",
        XrplFileError.NodeLacksHistory =>
            "This node's history does not reach back far enough to hold the file. A node that keeps more history can read it.",
        XrplFileError.CorruptManifest =>
            "There is something at that hash, but it is not a file description this version understands.",
        XrplFileError.ListMismatch =>
            "The list of the file's blocks does not match what the file's own description says it should be. Nothing was saved.",
        XrplFileError.BlockMismatch =>
            "A block of the file does not match what the file's own description says it should be. Nothing was saved.",
        XrplFileError.ChunkMissing =>
            "Part of the file is not in the ledger. Nothing was saved.",
        XrplFileError.TooLarge =>
            "This file is larger than this application will read.",
        XrplFileError.NodeUnreachable =>
            "The node did not answer. Check the node URL in Settings and try again.",
        XrplFileError.InsufficientBalance or XrplFileError.BudgetExceeded or XrplFileError.FeeMismatch
            or XrplFileError.SubmitRefused or XrplFileError.ValidationTimeout or XrplFileError.ResumeMismatch
            or XrplFileError.SessionAlreadyRun or XrplFileError.BatchNotEnabled or XrplFileError.PayerInsufficientBalance
            or XrplFileError.AccountInUse or XrplFileError.FeeTooHigh or XrplFileError.WriterWindowConflict
            or XrplFileError.ManifestRejected or XrplFileError.ApprovalExceeded =>
            "Something went wrong that reading a file cannot cause. This is a defect in this application, not something you did.",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "no sentence for this error"),
    };

    /// <summary>The same sentence, with the block named when the library said which one.</summary>
    public static string For(XrplFileException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string sentence = For(exception.Error);
        return exception.Block is int block ? $"{sentence} (block {block})" : sentence;
    }
}
