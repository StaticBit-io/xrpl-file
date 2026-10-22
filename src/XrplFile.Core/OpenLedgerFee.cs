namespace XrplFile;

/// <summary>What the <c>fee</c> method says now (spec B15): the open ledger's price and the base fee under it.</summary>
/// <param name="OpenLedgerFeeDrops">What a transaction costs to enter the open ledger now, in drops.</param>
/// <param name="BaseFeeDrops">The base fee under it, in drops.</param>
public sealed record OpenLedgerFee(long OpenLedgerFeeDrops, long BaseFeeDrops);
