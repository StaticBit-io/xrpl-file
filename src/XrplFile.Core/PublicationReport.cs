namespace XrplFile;

/// <summary>
/// What a finished run produced, in the shape the war-and-peace uploader wrote: enough to show a
/// person what happened, and enough to check it against the ledger afterwards.
/// </summary>
/// <param name="Transactions">
/// Everything the run put on the ledger: the chunk memos, the block-list memos, the manifest, and
/// the <see cref="PublicationEstimate.Mints"/> a ticketed strategy pays for on top of them. The
/// mints come from the estimate the run was approved against rather than from the ledger — a
/// strategy that rides the account's sequence submits none, and for that one this is the
/// estimate's shape rather than the run's.
/// </param>
/// <param name="FeeDropsEstimated">
/// What the approved estimate implies the run cost: <see cref="Transactions"/> at the fee that
/// estimate was priced with, which <see cref="PublicationSession.StartAsync"/> has already made
/// the strategy agree with. It is not what the ledger debited, and it is not named as if it were:
/// every retry a strategy makes is a transaction this number cannot see, and nothing here reads a
/// fee back off the ledger. Reconciling against the account is still the account's job.
/// </param>
public sealed record PublicationReport(
    XrplFileLink Link,
    Manifest Manifest,
    string Writer,
    long Transactions,
    long FeeDropsEstimated,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<string> ChunkHashes,
    IReadOnlyList<string> ListHashes,
    string ManifestHash)
{
    public TimeSpan Took => FinishedAt - StartedAt;

    /// <summary>
    /// Why the run's state file could not be written, or null when it was.
    ///
    /// The money is spent by the time the last save happens, and this report is the only record of
    /// what it bought — so a save that fails must not take the report down with it. It is surfaced
    /// here instead, because the consequence outlives the call: the state file may still say the
    /// run is unfinished, and <see cref="PublicationSession.Unfinished"/> will go on listing it.
    /// </summary>
    public Exception? StateSaveFailure { get; init; }
}
