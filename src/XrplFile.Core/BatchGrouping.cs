namespace XrplFile;

/// <summary>One memo inside a planned batch, and the writer's sequence it rides.</summary>
internal sealed record PlannedInner(int MemoIndex, uint Sequence);

/// <summary>A batch of a round: its place in the round, the payer's sequence it takes, and its inners.</summary>
internal sealed record PlannedBatch(int Ordinal, uint OuterSequence, IReadOnlyList<PlannedInner> Inners);

/// <summary>What one round sends: its batches, or the one memo it sends on its own (spec B11).</summary>
internal sealed record RoundPlan(IReadOnlyList<PlannedBatch> Batches, int? SoloMemo);

internal static class BatchGrouping
{
    public const int MaxInners = 8;

    public const int MinInners = 2;

    /// <summary>
    /// Batch sizes for this many memos: eight to a batch, and a trailing batch of one borrows a memo
    /// from the batch before it, so 8 + 1 becomes 7 + 2. Fewer than two memos make no batch.
    /// </summary>
    public static IReadOnlyList<int> Sizes(int memos)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(memos);

        if (memos < MinInners) return [];

        List<int> sizes = [];

        for (int left = memos; left > 0; left -= MaxInners) sizes.Add(Math.Min(MaxInners, left));

        if (sizes[^1] == 1)
        {
            sizes[^2] -= 1;
            sizes[^1] = 2;
        }

        return sizes;
    }

    /// <summary>
    /// Assigns sequences from one snapshot (spec 4.1 step 2): batch k takes the payer's sequence plus
    /// k, and its inners take the writer's sequences in order, after the inners of every batch before
    /// it. A round of exactly one memo is a solo and no batch.
    /// </summary>
    public static RoundPlan Plan(IReadOnlyList<int> memoIndices, uint writerSequence, uint payerSequence)
    {
        ArgumentNullException.ThrowIfNull(memoIndices);

        if (memoIndices.Count == 1) return new RoundPlan([], memoIndices[0]);

        IReadOnlyList<int> sizes = Sizes(memoIndices.Count);
        List<PlannedBatch> batches = new(sizes.Count);
        int position = 0;
        uint inner = writerSequence;

        for (int k = 0; k < sizes.Count; k++)
        {
            List<PlannedInner> inners = new(sizes[k]);
            for (int i = 0; i < sizes[k]; i++) inners.Add(new PlannedInner(memoIndices[position++], inner++));

            batches.Add(new PlannedBatch(k, payerSequence + (uint)k, inners));
        }

        return new RoundPlan(batches, null);
    }
}
