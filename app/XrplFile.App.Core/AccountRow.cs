using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// One throwaway account as the Accounts tab shows it. <see cref="BalanceDrops"/> is
/// <see langword="null"/> until a node has actually answered — a cached
/// <see cref="DeletableFromLedger"/> left over from an earlier session is not itself an answer, so
/// it never manufactures a balance on its own (<see cref="AccountStage.Unknown"/>). Once populated,
/// <see cref="BalanceDrops"/>, <see cref="DeletableFromLedger"/> and <see cref="LedgersRemaining"/>
/// are only as fresh as the last <see cref="AccountsModel.RefreshFromLedgerAsync"/>; nothing decides
/// to submit anything from them (design G8).
/// </summary>
public sealed record AccountRow(
    string Reference,
    string Label,
    string Address,
    string Network,
    AccountStage Stage,
    long? BalanceDrops,
    long? DeletableFromLedger,
    long? LedgersRemaining,
    AccountClosure? Closure)
{
    /// <summary>
    /// Whether this row still has anything to offer — false only once <see cref="Stage"/> is
    /// <see cref="AccountStage.Closed"/>, since checking, funding or closing a closed account is
    /// nothing a node or the vault has any answer for. The Accounts screen reads this rather than
    /// comparing <see cref="Stage"/> to <see cref="AccountStage.Closed"/> itself.
    /// </summary>
    public bool HasActions => Stage != AccountStage.Closed;
}
