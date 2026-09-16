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
            or XrplFileError.SessionAlreadyRun =>
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
