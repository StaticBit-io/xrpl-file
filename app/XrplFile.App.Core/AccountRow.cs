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
    AccountClosure? Closure);
