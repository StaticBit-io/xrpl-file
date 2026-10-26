namespace XrplFile.Tests;

/// <summary>A landed memo as the tests of the ticket and sequence paths read it.</summary>
public sealed record LandedMemoFacts(int Index, string TxHash, long? LedgerIndex);

public static class SubmittedMemosTesting
{
    /// <summary>Every memo with its kept hash and ledger; for the paths that keep a hash for every memo.</summary>
    public static IReadOnlyList<LandedMemoFacts> Each(this SubmittedMemos memos)
    {
        ArgumentNullException.ThrowIfNull(memos);

        List<LandedMemoFacts> each = new(memos.Count);

        for (int index = 0; index < memos.Count; index++)
        {
            if (!memos.TryHashOf(index, out string? hash)) throw new InvalidOperationException($"memo {index} kept no hash");
            each.Add(new LandedMemoFacts(index, hash, memos.TryLedgerOf(index, out long ledger) ? ledger : null));
        }

        return each;
    }
}
