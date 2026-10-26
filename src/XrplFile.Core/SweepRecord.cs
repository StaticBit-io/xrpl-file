namespace XrplFile;

/// <summary>A record's result, as identity, coverage and fees need it (spec S6).</summary>
internal enum ResultClass : byte
{
    Success,

    /// <summary>A <c>tec</c>: applied, sequence and fee consumed, nothing written.</summary>
    Claimed,

    Other,
}

internal enum MarkerKind : byte
{
    None,
    Chunk,
    List,
    Manifest,
    Other,
}

/// <summary>What a record's data says about the memo of this round that wears its marker (spec S6).</summary>
internal enum DataVerdict : byte
{
    /// <summary>No memo of this round wears its marker.</summary>
    NotChecked,
    Matches,
    Conflicts,
}

/// <summary>
/// A history record as a Batch round keeps it (spec S6): classified the moment a sweep reads it, and
/// kept with the fields identity (B12), coverage (B30) and fee attribution (B29) need. The memo's
/// data is compared once and dropped, so a round's history holds no chunk bytes.
/// </summary>
internal sealed record SweepRecord(
    string Hash,
    string Account,
    long LedgerIndex,
    int? TransactionIndex,
    bool Validated,
    ResultClass Result,
    string? TransactionType,
    uint Sequence,
    uint? TicketSequence,
    uint? TicketCount,
    long? Fee,
    string? ParentBatchID,
    MarkerKind Marker,
    long MarkerIndex,
    int CallIndex,
    DataVerdict Verdict)
{
    public bool Succeeded => Result == ResultClass.Success;

    public static SweepRecord From(LedgerTransaction tx, RoundMarkers? markers)
    {
        ArgumentNullException.ThrowIfNull(tx);

        (MarkerKind kind, long index) = Classify(tx.MemoTypeHex);
        int call = -1;
        DataVerdict verdict = DataVerdict.NotChecked;

        if (markers is not null && markers.TryIndexOf(tx.MemoTypeHex, out int found))
        {
            call = found;
            verdict = markers.Matches(found, tx.MemoDataHex) ? DataVerdict.Matches : DataVerdict.Conflicts;
        }

        ResultClass result = tx.Succeeded ? ResultClass.Success
            : tx.Result is { } code && code.StartsWith("tec", StringComparison.Ordinal) ? ResultClass.Claimed
            : ResultClass.Other;

        return new SweepRecord(
            tx.Hash, tx.Account, tx.LedgerIndex, tx.TransactionIndex, tx.Validated, result, tx.TransactionType, tx.Sequence,
            tx.TicketSequence, tx.TicketCount, tx.Fee, tx.ParentBatchID, kind, index, call, verdict);
    }

    public static (MarkerKind Kind, long Index) Classify(string? memoTypeHex)
    {
        if (memoTypeHex is null) return (MarkerKind.None, 0);
        if (MemoLayout.ChunkIndexOf(memoTypeHex) is { } chunk) return (MarkerKind.Chunk, chunk);
        if (MemoLayout.ListIndexOf(memoTypeHex) is { } list) return (MarkerKind.List, list);
        if (MemoLayout.IsManifest(memoTypeHex)) return (MarkerKind.Manifest, 0);

        return (MarkerKind.Other, 0);
    }
}
