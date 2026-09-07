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
}

public sealed class XrplFileException : Exception
{
    public XrplFileError Error { get; }

    /// <summary>The block the failure is about, for BlockMismatch and ChunkMissing; otherwise null.</summary>
    public int? Block { get; }

    public XrplFileException(XrplFileError error, string message, int? block = null, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
        Block = block;
    }
}
