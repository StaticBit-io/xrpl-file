namespace XrplFile.App.Core;

/// <summary>
/// Which submission strategy a publication uses, and how it is built. The application decides this,
/// not the person (design F1): the choice turns on reserve arithmetic, the <c>AccountDelete</c>
/// clock and retry behaviour, none of which can be put in front of somebody who wants to publish a
/// file.
/// </summary>
public static class PublishPolicy
{
    /// <summary>
    /// How many tickets one batch mints. The library's own default, restated here because the
    /// threshold below is this number and the two must move together.
    /// </summary>
    public const int TicketsPerBatch = 100;

    /// <summary>
    /// At or above this many memos the run uses tickets; below it, sequence numbers (design F2).
    ///
    /// The line is the batch unit itself. Under one batch, minting locks a whole batch's worth of
    /// owner reserve to run what a single sequence pass runs with none — and for an account funded
    /// for exactly one publication, that reserve, not the fees, is what decides how much XRP has to
    /// be moved. Above it, two costs that sequence numbers carry start to dominate instead: every
    /// sequence-consuming transaction pushes the account's earliest <c>AccountDelete</c> ledger out
    /// by one (rippled refuses while <c>Sequence + 255</c> exceeds the validated index, and ticketed
    /// transactions do not move <c>Sequence</c> at all), and a sequence gap stalls everything behind
    /// it where a ticket comes back and is retried on its own.
    /// </summary>
    public const long TicketThreshold = 100;

    /// <summary>
    /// The memos a run actually writes: its chunks, its block list and its manifest.
    /// Deliberately not <see cref="PublicationEstimate.Transactions"/>, which includes the
    /// <c>TicketCreate</c> mints — those exist only under the strategy being chosen here, so using
    /// them would let the ticket strategy argue itself into being picked.
    /// </summary>
    public static long MemoCount(PublicationEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        return estimate.Chunks + estimate.Lists + 1;
    }

    public static bool UsesTickets(PublicationEstimate estimate) => MemoCount(estimate) >= TicketThreshold;

    /// <summary>
    /// The same decision from a file's size alone, for a run whose estimate is gone — an unfinished
    /// one in the list. Exact, not approximate: a block list exists only past 1,024 chunks, far
    /// above the threshold, so below it the chunks and the manifest are every memo there is, and
    /// above it both forms are already true.
    /// </summary>
    public static bool UsesTickets(long fileBytes) =>
        FileTree.ChunkCount(fileBytes, MemoLayout.ChunkBytes) + 1 >= TicketThreshold;

    /// <summary>
    /// Whether a Batch run of a file this size would contain no Batch transaction at all, so a payer
    /// would pay nothing and the writer would send every memo itself (Batch spec 6, amendment
    /// 2026-10-22). Such a request is published without a payer, on the one-account policy above.
    /// The chunk pass alone decides it: the manifest is always a pass of one, and a block list exists
    /// only past 1,024 chunks, so with fewer than two chunks every pass is a single memo sent solo.
    /// </summary>
    public static bool TooSmallForBatch(long fileBytes) =>
        FileTree.ChunkCount(fileBytes, MemoLayout.ChunkBytes) < 2;

    /// <summary>
    /// The request a publication actually runs with: the same one, except that a payer is dropped
    /// when <see cref="TooSmallForBatch"/> says it would pay for nothing.
    /// </summary>
    public static PublishRequest Effective(PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.PayerAccount is not null && TooSmallForBatch(request.Size)
            ? request with { PayerReference = null, PayerAccount = null, PayerLabel = null }
            : request;
    }

    /// <summary>
    /// Whether the run would use tickets on an account where none can be minted or reused: every
    /// live-ticket slot is taken by tickets this application did not leave. A run that would not
    /// use tickets is unaffected.
    /// </summary>
    public static bool HasNoTicketRoom(PublicationEstimate estimate) => UsesTickets(estimate) && estimate.TicketSlots == 0;

    /// <summary>
    /// Builds the strategy for this estimate, priced from it.
    /// </summary>
    /// <remarks>
    /// The fee comes from the estimate rather than from either options type's own default because
    /// <see cref="PublicationSession.StartAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, string, string, string, string, SessionOptions, CancellationToken)"/> re-estimates and refuses a strategy priced
    /// differently with <see cref="XrplFileError.FeeMismatch"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The node's price does not fit the <see cref="int"/> both options types use. Every caller
    /// checks <see cref="PublicationEstimate.FeeDropsPerTransaction"/> against
    /// <see cref="int.MaxValue"/> before ever reaching here -- <c>EstimateModel</c> for a fresh
    /// estimate, and <c>PublicationRun.RunAsync</c> for the one it re-takes before both a fresh
    /// start and a resume, which is what actually covers <c>UnfinishedModel.ResumeAsync</c> (it
    /// reaches this method through <c>PublicationRun.Resume</c> without ever going through
    /// <c>EstimateModel</c> at all) -- so reaching this is a defect regardless of which caller got
    /// here.
    /// </exception>
    public static ISubmissionStrategy Create(
        PublicationEstimate estimate, ILedgerConnection connection, ITransactionSigner signer)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(estimate.FeeDropsPerTransaction, int.MaxValue);

        int fee = (int)estimate.FeeDropsPerTransaction;

        return UsesTickets(estimate) ? Tickets(connection, signer, fee) : Sequence(connection, signer, fee);
    }

    /// <summary>
    /// The Batch strategy for a run with a payer (spec 6.1, B4): built by the session, which hands in
    /// the budget from its estimate and itself as the fence recorder.
    /// </summary>
    public static BatchStrategy CreateBatch(
        BatchBudget budget, IBatchFenceRecorder fence, ILedgerConnection connection, ITransactionSigner writer, ITransactionSigner payer,
        BatchStrategyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new BatchStrategy(connection.Sink, connection.Source, writer, payer, budget, fence, options);
    }

    /// <summary>
    /// The one-account strategy a run resumes on (spec B16): the one it recorded, whatever its size;
    /// a run recorded before strategies were, on the existing policy.
    /// </summary>
    public static ISubmissionStrategy CreateFor(
        string? recordedStrategy, PublicationEstimate estimate, ILedgerConnection connection, ITransactionSigner signer)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(estimate.FeeDropsPerTransaction, int.MaxValue);

        int fee = (int)estimate.FeeDropsPerTransaction;

        return recordedStrategy switch
        {
            null => Create(estimate, connection, signer),
            PublicationState.TicketStrategyName => Tickets(connection, signer, fee),
            PublicationState.SequenceStrategyName => Sequence(connection, signer, fee),
            _ => throw new ArgumentOutOfRangeException(nameof(recordedStrategy), recordedStrategy, "not a one-account strategy"),
        };
    }

    private static TicketStrategy Tickets(ILedgerConnection connection, ITransactionSigner signer, int fee) =>
        new(connection.Sink, connection.Source, signer, new TicketStrategyOptions { FeeDrops = fee, TicketsPerBatch = TicketsPerBatch });

    private static SequenceStrategy Sequence(ILedgerConnection connection, ITransactionSigner signer, int fee) =>
        new(connection.Sink, signer, new SequenceStrategyOptions { FeeDrops = fee }, connection.Source);
}
