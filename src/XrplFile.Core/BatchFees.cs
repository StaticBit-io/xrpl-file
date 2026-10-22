namespace XrplFile;

/// <summary>What a batch costs and how far a round's price may move (spec B15, 4.5).</summary>
public static class BatchFees
{
    /// <summary>Above this open-ledger fee, in drops, an estimate refuses (spec B15).</summary>
    public const long MaxOpenLedgerFeeDrops = 200;

    /// <summary>ceil(<c>open_ledger_fee</c> × 1.5), never below the base fee.</summary>
    public static long Unit(OpenLedgerFee fee)
    {
        ArgumentNullException.ThrowIfNull(fee);

        return Math.Max(fee.BaseFeeDrops, (fee.OpenLedgerFeeDrops * 3 + 1) / 2);
    }

    /// <summary>The most a round may pay per unit: twice the unit the estimate approved.</summary>
    public static long Ceiling(long approvedUnitDrops) => approvedUnitDrops * 2;

    /// <summary>Two units for the batch, one per inner and one for its one BatchSigner.</summary>
    public static long BatchFee(int inners, long unitDrops) => (inners + 3L) * unitDrops;
}
