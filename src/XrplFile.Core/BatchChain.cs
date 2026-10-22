namespace XrplFile;

/// <summary>The chain check of spec B8, on one snapshot.</summary>
internal static class BatchChain
{
    public const string Diverged = "diverged";

    public const string Stalled = "stalled";

    /// <summary>
    /// <see cref="Diverged"/> when the writer's sequence differs from what the outers the payer
    /// consumed imply, or the payer consumed more than was registered; <see cref="Stalled"/> when the
    /// snapshot is past the deadline of the first registered outer the payer has not consumed; null
    /// otherwise. It does not try to say why: identity is settlement's question (spec B12).
    /// </summary>
    public static string? Verdict(
        RoundPlan plan, LedgerSnapshot start, IReadOnlyList<BatchCandidate> outers, LedgerSnapshot snapshot, string writer, string payer)
    {
        long consumed = (long)snapshot.SequenceOf(payer) - start.SequenceOf(payer);

        if (consumed < 0 || consumed > outers.Count) return Diverged;

        long expected = start.SequenceOf(writer);
        for (int k = 0; k < consumed; k++) expected += plan.Batches[k].Inners.Count;

        if (snapshot.SequenceOf(writer) != expected) return Diverged;

        if (consumed < outers.Count && snapshot.LedgerIndex > outers[(int)consumed].LastLedger) return Stalled;

        return null;
    }
}
