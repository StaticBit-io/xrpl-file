namespace XrplFile;

/// <summary>One memo on its way into the ledger: what a transaction will carry.</summary>
public sealed record MemoSubmission(string MemoTypeHex, string MemoDataHex, string? MemoFormatHex);

/// <summary>Where a memo landed: its position in the batch, and the transaction that carried it.</summary>
public sealed record SubmittedMemo(int Index, string TxHash);

/// <summary>
/// How a set of memos reaches the ledger.
///
/// The contract is deliberately strong: when the call returns, every memo is in a validated
/// ledger and its transaction hash is known. How that was achieved — one at a time on the
/// account's sequence, or many at once on tickets — is the implementation's business and
/// nobody else's. A strategy is disposable because a ticketed one may hold tickets that were
/// minted and never spent — a submission handed back its ticket, or the call was cut short — and
/// disposing does not reclaim them: they stay live on the account, each locking an owner reserve,
/// until a later publication from the account picks them up or spends them in its tidy-up, or the
/// account is deleted.
/// </summary>
public interface ISubmissionStrategy : IAsyncDisposable
{
    /// <summary>
    /// The fee, in drops, every transaction this strategy puts on the ledger carries — the memos,
    /// and the tickets where a strategy mints them. It is fixed for the strategy's lifetime: a
    /// caller who wants the network's current price reads it before constructing the strategy.
    ///
    /// It is on the interface because it is the one number somebody else has to agree with. A run
    /// is approved against an estimate priced at some fee; a strategy that charges another turns
    /// that approval into a statement about a run nobody is going to submit, which is why
    /// <see cref="PublicationSession.StartAsync"/> compares the two and refuses to start.
    /// </summary>
    long FeeDropsPerTransaction { get; }

    /// <summary>
    /// The classic address every transaction this strategy submits is signed by.
    ///
    /// It is on the interface for the same reason the fee is: somebody else has to agree with it.
    /// A publication is recorded, reported and read under one writer, and a strategy signing as
    /// another account would put the rest of a file into a history nobody is going to look in — so
    /// <see cref="PublicationSession.StartAsync"/> and <see cref="PublicationSession.ResumeAsync"/>
    /// compare this against the writer they were given, and refuse when it differs.
    /// </summary>
    string Account { get; }

    Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken);

    /// <summary>
    /// Spends this library's live tickets on the account, each on an AccountSet that sets nothing,
    /// and waits for the results (spec I6). Called after a publication succeeds, from either
    /// strategy (spec I7): leftovers belong to the account, not to the run that left them.
    ///
    /// Cancellation ends it early and is not an error: what settled is counted and everything else
    /// is <see cref="TicketRelease.NotReleased"/>. Any other failure is thrown.
    ///
    /// It has no time bound of its own: it waits until every spend settles or the token is
    /// cancelled. A caller supplies the deadline through the token, as
    /// <see cref="PublicationSession"/> does with <see cref="SessionOptions.TicketReleaseTimeout"/>.
    /// </summary>
    Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken);
}
