namespace XrplFile;

/// <summary>
/// What one session may spend (spec S25): the writer and the payer it is for, the fee-unit ceiling
/// of spec B15, and each account's ceiling in drops — its worst-case fees. Immutable: a resume is a
/// new session with new spending and carries an approval of its own.
/// </summary>
/// <param name="Writer">The writer the approval is for.</param>
/// <param name="Payer">The payer, or null for a one-account run.</param>
/// <param name="FeeUnitCeilingDrops">A Batch round's unit may rise to this; a one-account run's per-transaction fee may not exceed it.</param>
/// <param name="WriterCeilingDrops">The most the writer may spend in fees.</param>
/// <param name="PayerCeilingDrops">The most the payer may spend in fees; zero without one.</param>
public sealed record SpendingApproval(string Writer, string? Payer, long FeeUnitCeilingDrops, long WriterCeilingDrops, long PayerCeilingDrops)
{
    /// <summary>A one-account estimate as it was shown: its fee and its worst case.</summary>
    public static SpendingApproval For(PublicationEstimate estimate, string writer)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return new SpendingApproval(writer, null, estimate.FeeDropsPerTransaction, estimate.FeeDropsWorstCase, 0);
    }

    /// <summary>A Batch estimate as it was shown: its ceiling (B15) and each account's worst case (B26).</summary>
    public static SpendingApproval For(BatchPublicationEstimate estimate, string writer, string payer)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return new SpendingApproval(writer, payer, estimate.FeeCeilingDrops, estimate.WriterFeeDropsWorstCase, estimate.PayerFeeDropsWorstCase);
    }

    internal void Require(PublicationEstimate estimate, string writer)
    {
        RequireAccounts(writer, payer: null);

        if (estimate.FeeDropsPerTransaction > FeeUnitCeilingDrops || estimate.FeeDropsWorstCase > WriterCeilingDrops)
        {
            throw Exceeded($"the run prices a transaction at {estimate.FeeDropsPerTransaction} drops and {estimate.FeeDropsWorstCase} at worst; "
                + $"{FeeUnitCeilingDrops} and {WriterCeilingDrops} were approved");
        }
    }

    /// <summary>
    /// Only exceeding a ceiling refuses (spec S25, B15): the unit may rise up to the approved ceiling,
    /// and each worst case is taken at that ceiling, the most B26 lets the run spend, so it exceeds
    /// its approved figure only when the run is larger than the one approved.
    /// </summary>
    internal void Require(BatchPublicationEstimate estimate, string writer, string payer)
    {
        RequireAccounts(writer, payer);
        BatchPublicationEstimate priced = AtCeiling(estimate);

        if (estimate.FeeUnitDrops > FeeUnitCeilingDrops
            || priced.WriterFeeDropsWorstCase > WriterCeilingDrops
            || priced.PayerFeeDropsWorstCase > PayerCeilingDrops)
        {
            throw Exceeded($"the run's unit is {estimate.FeeUnitDrops} drops, and at the approved ceiling the writer's worst case is {priced.WriterFeeDropsWorstCase} "
                + $"and the payer's {priced.PayerFeeDropsWorstCase}; {FeeUnitCeilingDrops}, {WriterCeilingDrops} and {PayerCeilingDrops} were approved");
        }
    }

    /// <summary>
    /// The estimate priced at the approved fee-unit ceiling. B26's worst cases are a count of units
    /// times the ceiling, so they scale exactly.
    /// </summary>
    internal BatchPublicationEstimate AtCeiling(BatchPublicationEstimate estimate) => estimate with
    {
        FeeCeilingDrops = FeeUnitCeilingDrops,
        PayerFeeDropsWorstCase = checked(estimate.PayerFeeDropsWorstCase / estimate.FeeCeilingDrops * FeeUnitCeilingDrops),
        WriterFeeDropsWorstCase = checked(estimate.WriterFeeDropsWorstCase / estimate.FeeCeilingDrops * FeeUnitCeilingDrops),
    };

    /// <summary>The strategy is built from the approval (spec S25), and B26's runtime count enforces each ceiling.</summary>
    internal BatchBudget Budget(BatchPublicationEstimate estimate) =>
        new(estimate.FeeUnitDrops, FeeUnitCeilingDrops, PayerCeilingDrops, WriterCeilingDrops);

    private void RequireAccounts(string writer, string? payer)
    {
        if (!string.Equals(writer, Writer, StringComparison.Ordinal) || !string.Equals(payer, Payer, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"the approval is for writer {Writer} and payer {Payer ?? "none"}, and the run is for {writer} and {payer ?? "none"}", "approval");
        }
    }

    private static XrplFileException Exceeded(string detail) =>
        new(XrplFileError.ApprovalExceeded, $"fees or balances changed since this was approved: {detail}");
}

/// <summary>What a resume will still spend, priced after reconciliation from the work that remains (spec S25).</summary>
/// <param name="Writer">The run's writer.</param>
/// <param name="Payer">The payer this resume runs with, or null for a one-account run.</param>
/// <param name="MissingChunks">The chunks reconciliation did not find in the ledger.</param>
/// <param name="OneAccount">The estimate of a one-account resume, else null.</param>
/// <param name="Batch">The estimate of a Batch resume, else null.</param>
public sealed record ResumeQuote(string Writer, string? Payer, long MissingChunks, PublicationEstimate? OneAccount, BatchPublicationEstimate? Batch);

/// <summary>
/// How a resume gets its approval (spec S25): handed the quote, it returns what the person approved,
/// or throws <see cref="OperationCanceledException"/> when they declined, which ends the resume before anything is sent.
/// </summary>
public delegate Task<SpendingApproval> ResumeApproval(ResumeQuote quote, CancellationToken cancellationToken);
