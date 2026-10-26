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

    /// <summary>A Batch run on a network whose ledger does not enable <c>BatchV1_1</c> (spec 4.7).</summary>
    BatchNotEnabled,

    /// <summary>The payer's spendable balance is below its part of a Batch run (spec 4.7).</summary>
    PayerInsufficientBalance,

    /// <summary>
    /// A transaction of the writer or the payer that the run did not send applied during a round
    /// (spec B12). <see cref="XrplFileException.Role"/> says which account.
    /// </summary>
    AccountInUse,

    /// <summary>
    /// The open-ledger fee is above 200 drops at estimate time, or above the approved ceiling for a
    /// minute during a run (spec B15).
    /// </summary>
    FeeTooHigh,
}

/// <summary>Which of a Batch run's two accounts an error is about.</summary>
public enum AccountRole
{
    Writer,
    Payer,
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

    /// <summary>For <see cref="XrplFileError.AccountInUse"/>: the account that was in use; otherwise null.</summary>
    public AccountRole? Role { get; }

    /// <summary>
    /// True when a resume was refused because the file differs from the one the run recorded (its
    /// size or its root), and only then. A caller may then ask for the file again; for any other
    /// <see cref="XrplFileError.ResumeMismatch"/> the file is not what is wrong.
    /// </summary>
    public bool SourceChanged { get; init; }

    public XrplFileException(
        XrplFileError error, string message, int? block = null, Exception? inner = null, string? engineResult = null,
        AccountRole? role = null)
        : base(message, inner)
    {
        Error = error;
        Block = block;
        EngineResult = engineResult;
        Role = role;
    }
}
