namespace XrplFile.App.Core;

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
            "Transactions were submitted but not seen validated in time. What already landed is recorded, and resuming does not pay for it again.",
        XrplFileError.ResumeMismatch =>
            "This is not the file, or not the key, that the interrupted run started with.",
        XrplFileError.SessionAlreadyRun =>
            "A publication session was run twice. That is a defect in this application, not something you did.",
        XrplFileError.TooLarge =>
            "The file is larger than the reader's own limit, so the link it produced cannot be read back at that limit.",
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

        string sentence = For(exception.Error);
        return exception.Block is int block ? $"{sentence} (block {block})" : sentence;
    }
}
