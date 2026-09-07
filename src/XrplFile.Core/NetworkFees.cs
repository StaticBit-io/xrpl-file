namespace XrplFile;

/// <summary>
/// What a transaction costs and what an object locks up, as the node reports them now.
///
/// None of these is a constant. The reserves have been lowered twice and the fee moves with the
/// network's load, so an estimate that carries its own numbers is an estimate that is wrong on
/// the day it matters.
/// </summary>
/// <param name="OpenLedgerFeeDrops">What one transaction costs at the current load.</param>
/// <param name="BaseReserveDrops">What an account must hold to exist.</param>
/// <param name="OwnerReserveDrops">What each owned object, a live ticket included, locks up.</param>
public sealed record NetworkFees(long OpenLedgerFeeDrops, long BaseReserveDrops, long OwnerReserveDrops);
