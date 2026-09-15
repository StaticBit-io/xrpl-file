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
    /// Builds the strategy for this estimate, priced from it.
    /// </summary>
    /// <remarks>
    /// The fee comes from the estimate rather than from either options type's own default because
    /// <see cref="PublicationSession.StartAsync"/> re-estimates and refuses a strategy priced
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

        return UsesTickets(estimate)
            ? new TicketStrategy(
                connection.Sink,
                connection.Source,
                signer,
                new TicketStrategyOptions { FeeDrops = fee, TicketsPerBatch = TicketsPerBatch })
            : new SequenceStrategy(connection.Sink, signer, new SequenceStrategyOptions { FeeDrops = fee });
    }
}
