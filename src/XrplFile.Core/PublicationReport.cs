namespace XrplFile;

/// <summary>
/// What a finished run produced, in the shape the war-and-peace uploader wrote: enough to show a
/// person what happened, and enough to check it against the ledger afterwards.
///
/// Two scopes meet in here, and every member below says which one it is in. The *publication* is
/// the whole file as it now stands in the ledger, however many sessions it took; the *session* is
/// the one <see cref="PublicationSession.RunAsync"/> call this report came from. For a run that was
/// never interrupted they are the same thing. For a resumed one they are not, and the difference
/// cannot be measured from here: a session does not know how long the session before it ran or what
/// it paid, so nothing here invents it.
/// </summary>
/// <param name="Transactions">
/// Publication scope. Everything the publication put on the ledger, not only this session: the
/// chunk memos, the block-list memos, the manifest, and the ticket mints a ticketed strategy pays
/// for on top of them. A session started fresh counts the mints its own strategy made during the
/// session (<see cref="ISubmissionStrategy.TicketMints"/>), so tickets picked up from an earlier run
/// cost nothing here; a resumed session counts the estimate's <see cref="PublicationEstimate.Mints"/>,
/// because the earlier sessions' mints are on no record it reads. A resumed session counts the
/// chunks an earlier session paid for here too, because it is the file being described. The tidy-up
/// is not in it: see <see cref="TicketReleaseFeeDrops"/>.
/// </param>
/// <param name="FeeDropsEstimated">
/// Publication scope. What the approved estimate implies the whole file cost, the tidy-up left out:
/// <see cref="Transactions"/> at the fee that estimate was priced with, which
/// <see cref="PublicationSession.StartAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, string, string, string, string, SessionOptions, CancellationToken)"/> and <see cref="PublicationSession.ResumeAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, SessionOptions, CancellationToken)"/>
/// have already made the strategy agree with. It is not what the ledger debited, and it is not
/// named as if it were: every retry a strategy makes is a transaction this number cannot see,
/// nothing here reads a fee back off the ledger, and on a resume it prices an earlier session's
/// transactions at this session's fee. Reconciling against the account is still the account's job.
/// What the publication and its tidy-up cost is <c>FeeDropsEstimated + TicketReleaseFeeDrops</c>,
/// and it may have cost up to <see cref="TicketReleaseFeeDropsInDoubt"/> more.
/// For a Batch run this is the writer's part only — its solo transactions at the approved unit — and
/// <see cref="PayerFeeDrops"/> is what the payer paid; <see cref="Transactions"/> then counts the memos
/// (chunks, lists, manifest), inners and solos alike.
/// </param>
/// <param name="StartedAt">
/// Session scope: when this session began. A resumed publication's earlier sessions are not in it
/// and cannot be — their clock is on neither the ledger nor the state file.
/// </param>
/// <param name="FinishedAt">Session scope: when this session ended.</param>
/// <param name="ChunkHashes">
/// Publication scope. Every chunk of the file, in index order, whichever session put it there: on a
/// resume this names transactions an earlier session submitted alongside this one's.
/// </param>
/// <param name="ListHashes">Publication scope, like <see cref="ChunkHashes"/>: every block-list memo of the file, in index order.</param>
/// <param name="ManifestHash">The manifest, which only the session that finishes the publication writes.</param>
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
    /// <summary>
    /// How long this session took — session scope, from <see cref="StartedAt"/> to
    /// <see cref="FinishedAt"/>. On a resumed publication it is not how long the file took to
    /// publish, only how long the part after the interruption did.
    /// </summary>
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

    /// <summary>Leftover tickets of this library's that the tidy-up after the publication spent (spec 4.6).</summary>
    public int TicketsReleased { get; init; }

    /// <summary>Leftover tickets the tidy-up found already consumed by something else. Their reserve is free.</summary>
    public int TicketsAlreadyGone { get; init; }

    /// <summary>
    /// Leftover tickets still live after the tidy-up — refused, or unsettled when its time ran out.
    /// All three counts zero with no <see cref="TicketReleaseFailure"/> means the tidy-up found
    /// nothing <em>or</em> ran out of time before its census could be read (see
    /// <see cref="TicketRelease"/>): the report cannot tell those apart, so zeros do not prove the
    /// account holds none of this library's tickets.
    /// </summary>
    public int TicketsNotReleased { get; init; }

    /// <summary>
    /// Why the tidy-up failed, or null. The publication succeeded regardless: the manifest is in the
    /// ledger and the link works (spec I8), the same way a state file that could not be saved does
    /// not cost the report.
    /// </summary>
    public Exception? TicketReleaseFailure { get; init; }

    /// <summary>
    /// What the tidy-up's spends certainly cost: <see cref="TicketRelease.Charged"/> at the fee the run
    /// was approved at, which every spend carried (spec 4.9, I17). Null when the tidy-up's counts were
    /// lost — it failed, did not stop within its grace, or ended in a cancellation instead of returning
    /// them; the most it could have cost is the worst case the estimate already covered (spec I10).
    /// </summary>
    public long? TicketReleaseFeeDrops { get; init; }

    /// <summary>
    /// What the tidy-up's spends may still cost: <see cref="TicketRelease.InDoubt"/> at the same fee.
    /// Such a spend may apply until its LastLedgerSequence passes. Null when the counts were lost.
    /// </summary>
    public long? TicketReleaseFeeDropsInDoubt { get; init; }

    /// <summary>The Batch run's payer, or null for a run with one account.</summary>
    public string? Payer { get; init; }

    /// <summary>
    /// Session scope: what the payer paid in this session — the <c>Fee</c> of every outer this session
    /// signed that settlement found applied, matched by hash (spec B29). Null when there is no payer,
    /// or when settlement could not attribute it; never a guess.
    /// </summary>
    public long? PayerFeeDrops { get; init; }
}
