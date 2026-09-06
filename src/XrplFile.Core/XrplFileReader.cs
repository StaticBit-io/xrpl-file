namespace XrplFile;

public sealed class ReaderOptions
{
    /// <summary>The largest file the reader assembles. Checked against the manifest before any chunk is read.</summary>
    public long MaxBytes { get; init; } = 20_000_000;
}

/// <summary>A manifest together with where it was found.</summary>
public sealed record ManifestRecord(Manifest Manifest, string Writer, long LedgerIndex, string TxHash);

public sealed record ReadResult(Manifest Manifest, string Writer, IReadOnlyList<BlockEntry> Blocks, byte[] Bytes);

/// <summary>
/// Reads a file out of the ledger and verifies every byte against the manifest's root. The
/// algorithm is the one the format document describes, and the browser library repeats it
/// rule for rule; the shared vectors keep the two in step.
/// </summary>
public sealed class XrplFileReader(ILedgerSource source, ReaderOptions? options = null)
{
    private readonly ILedgerSource _source = source;
    private readonly ReaderOptions _options = options ?? new ReaderOptions();

    public async Task<ReadResult> ReadAsync(XrplFileLink link, CancellationToken cancellationToken)
    {
        ManifestRecord record = await ManifestAsync(link, cancellationToken).ConfigureAwait(false);
        Manifest manifest = record.Manifest;

        if (manifest.Size > _options.MaxBytes)
        {
            throw new XrplFileException(XrplFileError.TooLarge, $"the file is {manifest.Size} bytes and this reader stops at {_options.MaxBytes}");
        }

        await RequireHistoryFromAsync(manifest.FirstLedger, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<BlockEntry> blocks = await BlockListAsync(record, cancellationToken).ConfigureAwait(false);

        byte[] bytes = new byte[manifest.Size];
        long at = 0;

        foreach (BlockEntry block in blocks)
        {
            byte[] content = await BlockAsync(record, block, cancellationToken).ConfigureAwait(false);
            int take = (int)Math.Min(content.Length, manifest.Size - at);
            Array.Copy(content, 0, bytes, at, take);
            at += take;
        }

        return new ReadResult(manifest, record.Writer, blocks, bytes);
    }

    public async Task<ManifestRecord> ManifestAsync(XrplFileLink link, CancellationToken cancellationToken)
    {
        LedgerTransaction? tx = await _source.TransactionAsync(link.TxHash, cancellationToken).ConfigureAwait(false);

        if (tx is null)
        {
            long? earliest = await _source.EarliestLedgerAsync(cancellationToken).ConfigureAwait(false);

            if (earliest is null || earliest > Endpoints.MainnetFirstLedger)
            {
                throw new XrplFileException(XrplFileError.NodeLacksHistory,
                    $"the node does not know transaction {link.TxHash} and its history starts at ledger {earliest?.ToString() ?? "an unknown point"}; " +
                    "a node with full history may still have it");
            }

            throw new XrplFileException(XrplFileError.ManifestNotFound, $"no transaction {link.TxHash} on this network");
        }

        if (!tx.Validated || !tx.Succeeded)
        {
            throw new XrplFileException(XrplFileError.ManifestNotFound, $"transaction {link.TxHash} is not a validated, successful transaction");
        }

        if (!MemoLayout.IsManifest(tx.MemoTypeHex) || tx.MemoDataHex is null)
        {
            throw new XrplFileException(XrplFileError.ManifestNotFound, $"transaction {link.TxHash} does not carry an {MemoLayout.ManifestMarker} memo");
        }

        Manifest manifest;

        try
        {
            manifest = Manifest.Decode(MemoLayout.FromHexUtf8(tx.MemoDataHex));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw new XrplFileException(XrplFileError.CorruptManifest, $"the manifest in {link.TxHash} is unreadable: {e.Message}", inner: e);
        }

        string? inconsistency = manifest.Inconsistency();
        if (inconsistency is not null)
        {
            throw new XrplFileException(XrplFileError.CorruptManifest, $"the manifest in {link.TxHash} is inconsistent: {inconsistency}");
        }

        if (manifest.LastLedger >= tx.LedgerIndex)
        {
            throw new XrplFileException(XrplFileError.CorruptManifest, $"the manifest in {link.TxHash} names a ledger range that ends after the manifest itself");
        }

        return new ManifestRecord(manifest, tx.Account, tx.LedgerIndex, tx.Hash);
    }

    public async Task<IReadOnlyList<BlockEntry>> BlockListAsync(ManifestRecord record, CancellationToken cancellationToken)
    {
        Manifest manifest = record.Manifest;

        if (manifest.Blocks == 1)
        {
            return [new BlockEntry(0, manifest.Root, manifest.FirstLedger)];
        }

        // Backwards from the end of the range: the lists were written after the chunks, so they
        // are the first thing a backward read meets.
        Dictionary<long, string> payloads = [];

        await foreach (LedgerTransaction tx in _source.AccountTransactionsAsync(record.Writer, manifest.FirstLedger, manifest.LastLedger, forward: false, cancellationToken).ConfigureAwait(false))
        {
            if (!IsMine(tx, record.Writer, manifest)) continue;

            long? index = MemoLayout.ListIndexOf(tx.MemoTypeHex);
            if (index is null || tx.MemoDataHex is null) continue;

            // Earlier in ledger order wins, and a backward read meets the later copy first.
            payloads[index.Value] = tx.MemoDataHex;

            if (payloads.Count == manifest.Lists && Enumerable.Range(0, manifest.Lists).All(i => payloads.ContainsKey(i))) break;
        }

        if (payloads.Count != manifest.Lists)
        {
            throw new XrplFileException(XrplFileError.ListMismatch, $"the manifest promises {manifest.Lists} block-list memos and {payloads.Count} were found");
        }

        IReadOnlyList<BlockEntry> entries;

        try
        {
            entries = FileTree.DecodeEntries(Enumerable.Range(0, manifest.Lists).Select(i => MemoLayout.FromHexUtf8(payloads[i])));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw new XrplFileException(XrplFileError.ListMismatch, $"the block list is unreadable: {e.Message}", inner: e);
        }

        if (entries.Count != manifest.Blocks)
        {
            throw new XrplFileException(XrplFileError.ListMismatch, $"the manifest says {manifest.Blocks} blocks and the list has {entries.Count}");
        }

        string root = FileTree.RootHex(entries);
        if (!string.Equals(root, manifest.Root, StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.ListMismatch, $"the block list hashes to {root}, the manifest says {manifest.Root}");
        }

        return entries;
    }

    public async Task<byte[]> BlockAsync(ManifestRecord record, BlockEntry block, CancellationToken cancellationToken)
    {
        Manifest manifest = record.Manifest;
        long firstChunk = (long)block.Index * FileTree.BlockChunks;
        long lastChunk = Math.Min(firstChunk + FileTree.BlockChunks, manifest.ChunkCount) - 1;
        int count = (int)(lastChunk - firstChunk + 1);

        byte[]?[] chunks = new byte[count][];
        int found = 0;

        await foreach (LedgerTransaction tx in _source.AccountTransactionsAsync(record.Writer, block.FirstLedger, manifest.LastLedger, forward: true, cancellationToken).ConfigureAwait(false))
        {
            if (!IsMine(tx, record.Writer, manifest)) continue;

            long? index = MemoLayout.ChunkIndexOf(tx.MemoTypeHex);
            if (index is null || index < firstChunk || index > lastChunk || tx.MemoDataHex is null) continue;

            int slot = (int)(index.Value - firstChunk);
            if (chunks[slot] is not null) continue;

            try
            {
                chunks[slot] = MemoLayout.Bytes(tx.MemoDataHex);
            }
            catch (FormatException)
            {
                continue;
            }

            if (++found == count) break;
        }

        if (found != count)
        {
            int missing = Array.FindIndex(chunks, chunk => chunk is null);
            throw new XrplFileException(XrplFileError.ChunkMissing,
                $"chunk {firstChunk + missing} of block {block.Index} is not in ledgers {block.FirstLedger}..{manifest.LastLedger}", block.Index);
        }

        string hash = Convert.ToHexString(FileTree.BlockHash(chunks!));
        if (!string.Equals(hash, block.HashHex, StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.BlockMismatch,
                $"block {block.Index} hashes to {hash}, the list says {block.HashHex}", block.Index);
        }

        int total = chunks.Sum(chunk => chunk!.Length);
        byte[] bytes = new byte[total];
        int at = 0;

        foreach (byte[]? chunk in chunks)
        {
            Array.Copy(chunk!, 0, bytes, at, chunk!.Length);
            at += chunk.Length;
        }

        return bytes;
    }

    private async Task RequireHistoryFromAsync(long ledger, CancellationToken cancellationToken)
    {
        long? earliest = await _source.EarliestLedgerAsync(cancellationToken).ConfigureAwait(false);

        if (earliest is not null && earliest > ledger)
        {
            throw new XrplFileException(XrplFileError.NodeLacksHistory,
                $"the file starts in ledger {ledger} and the node's history starts at {earliest}; use a node with full history");
        }
    }

    private static bool IsMine(LedgerTransaction tx, string writer, Manifest manifest) =>
        tx.Succeeded && tx.Validated
        && string.Equals(tx.Account, writer, StringComparison.Ordinal)
        && tx.LedgerIndex >= manifest.FirstLedger && tx.LedgerIndex <= manifest.LastLedger;
}
