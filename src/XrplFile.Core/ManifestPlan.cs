namespace XrplFile;

/// <summary>
/// How a manifest is built from what landed: one place, so a resume looks for exactly the bytes the
/// writer would send (spec B27).
/// </summary>
internal static class ManifestPlan
{
    /// <summary>
    /// Block entries from block hashes and each block's first ledger (spec S3): where a reader starts
    /// paging for the block, the earliest ledger holding one of its adopted chunks.
    /// </summary>
    public static IReadOnlyList<BlockEntry> Entries(IReadOnlyList<byte[]> blockHashes, IReadOnlyList<long> blockFirstLedgers)
    {
        ArgumentNullException.ThrowIfNull(blockHashes);
        ArgumentNullException.ThrowIfNull(blockFirstLedgers);

        if (blockHashes.Count != blockFirstLedgers.Count)
        {
            throw new ArgumentException($"{blockHashes.Count} block hashes and {blockFirstLedgers.Count} first ledgers", nameof(blockFirstLedgers));
        }

        List<BlockEntry> entries = new(blockHashes.Count);
        for (int block = 0; block < blockHashes.Count; block++)
        {
            entries.Add(new BlockEntry(block, Convert.ToHexString(blockHashes[block]), blockFirstLedgers[block]));
        }

        return entries;
    }

    /// <summary>Each block's earliest chunk ledger, from per-chunk ledgers. Bounded use only: the one-account paths (64 MiB).</summary>
    public static IReadOnlyList<long> FirstLedgers(int chunkCount, IReadOnlyDictionary<int, long> chunkLedgers)
    {
        int blocks = FileTree.BlockCount(chunkCount);
        long[] firsts = new long[blocks];

        for (int block = 0; block < blocks; block++)
        {
            int first = block * FileTree.BlockChunks;
            int end = Math.Min(first + FileTree.BlockChunks, chunkCount);
            firsts[block] = Enumerable.Range(first, end - first).Min(index => chunkLedgers[index]);
        }

        return firsts;
    }

    /// <summary>The block-list memos, only when there is more than one block.</summary>
    public static IReadOnlyList<MemoSubmission> ListMemos(IReadOnlyList<BlockEntry> entries) =>
        entries.Count <= 1
            ? []
            : [.. FileTree.EncodeEntries(entries).Select((payload, j) =>
                new MemoSubmission(MemoLayout.ListMarkerHex(j), MemoLayout.Utf8Hex(payload), MemoLayout.JsonFormatHex))];

    public static Manifest Build(
        string type, string? name, long size, IReadOnlyList<BlockEntry> entries, long firstLedger, long lastLedger, int lists) =>
        new(type, size, MemoLayout.ChunkBytes, FileTree.RootHex(entries), firstLedger, lastLedger, entries.Count, lists, name);

    public static MemoSubmission Memo(Manifest manifest) =>
        new(MemoLayout.ManifestMarkerHex, MemoLayout.Utf8Hex(manifest.Encode()), MemoLayout.JsonFormatHex);
}
