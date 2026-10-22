namespace XrplFile;

/// <summary>
/// Accounts' next sequences, all read at one validated ledger (spec B6). Two separate "validated"
/// reads can straddle a close, so every comparison of two accounts' sequences uses one of these.
/// </summary>
/// <param name="LedgerIndex">The validated ledger every sequence here was read at.</param>
/// <param name="Sequences">Each account's next sequence at that ledger; zero for an account the ledger does not carry.</param>
public sealed record LedgerSnapshot(long LedgerIndex, IReadOnlyDictionary<string, uint> Sequences)
{
    /// <summary>The account's next sequence in this snapshot; zero for an account the snapshot does not carry.</summary>
    /// <param name="account">The account's classic address.</param>
    public uint SequenceOf(string account) => Sequences.TryGetValue(account, out uint sequence) ? sequence : 0;
}
