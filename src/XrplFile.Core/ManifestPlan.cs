namespace XrplFile;

/// <summary>
/// How a manifest is built from what landed: one place, so a resume looks for exactly the bytes the
/// writer would send (spec B27).
/// </summary>
internal static class ManifestPlan
{
    /// <summary>Block entries: hash over the chunks in memory, first ledger the earliest of the block's own chunks.</summary>
    public static IReadOnlyList<BlockEntry> Entries(IReadOnlyList<byte[]> chunks, IReadOnlyDictionary<int, long> chunkLedgers)
    {
        int blocks = FileTree.BlockCount(chunks.Count);
        List<BlockEntry> entries = new(blocks);

        for (int block = 0; block < blocks; block++)
        {
            int firstChunk = block * FileTree.BlockChunks;
            List<byte[]> members = [.. chunks.Skip(firstChunk).Take(FileTree.BlockChunks)];
            long ledger = Enumerable.Range(firstChunk, members.Count).Min(i => chunkLedgers[i]);
            entries.Add(new BlockEntry(block, Convert.ToHexString(FileTree.BlockHash(members)), ledger));
        }

        return entries;
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
