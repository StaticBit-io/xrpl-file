namespace XrplFile;

/// <summary>What went wrong, as a value a caller can switch on. Messages are for people.</summary>
public enum XrplFileError
{
    InvalidLink,
    ManifestNotFound,
    NodeLacksHistory,
    CorruptManifest,
    ListMismatch,
    BlockMismatch,
    ChunkMissing,
    TooLarge,
    SubmitRefused,
    ValidationTimeout,
    NodeUnreachable,
    InsufficientBalance,
    BudgetExceeded,

    /// <summary>
    /// The estimate a run was approved against and the strategy that would submit it price a
    /// transaction differently, so the budget and balance checks describe a different run.
    /// </summary>
    FeeMismatch,

    /// <summary>A session that has run, or is running, was asked to run again.</summary>
    SessionAlreadyRun,

    /// <summary>
    /// A resume was asked to pick up a state file it cannot honestly pick up: the run it names
    /// already finished, the file handed to it is not the one the run was started for, or the
    /// strategy handed to it signs as an account other than the run's writer.
    /// </summary>
    ResumeMismatch,
}

public sealed class XrplFileException : Exception
{
    public XrplFileError Error { get; }

    /// <summary>The block the failure is about, for BlockMismatch and ChunkMissing; otherwise null.</summary>
    public int? Block { get; }

    /// <summary>
    /// The node's own engine result for <see cref="XrplFileError.SubmitRefused"/> — <c>"tecTOO_SOON"</c>,
    /// <c>"temDST_IS_SRC"</c>, and so on — or null when this exception was not raised from a refused
    /// submission. Only a <c>tec*</c> code actually claims a fee; a caller that has to say whether an
    /// attempt cost anything needs this rather than the bare fact that it was refused.
    /// </summary>
    public string? EngineResult { get; }

    public XrplFileException(
        XrplFileError error, string message, int? block = null, Exception? inner = null, string? engineResult = null)
        : base(message, inner)
    {
        Error = error;
        Block = block;
        EngineResult = engineResult;
    }
}
