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
            Array.Copy(content, 0, bytes, at, content.Length);
            at += content.Length;
        }

        // BlockAsync holds every block to the length the manifest implies for it, so the sum can
        // only come out short if the block list was, and that is checked before any block is read.
        if (at != manifest.Size)
        {
            throw new XrplFileException(XrplFileError.BlockMismatch,
                $"the blocks assemble to {at} bytes and the manifest says {manifest.Size}", blocks[^1].Index);
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

        // Backwards from the end of the range, stopping as soon as every index has a value: walking
        // a large file's chunks in reverse just to reach the lists would be far too expensive.
        // That early stop means the rule for block-list memos is "the last copy in canonical
        // (forward) order wins" -- the opposite of chunks, where the first copy wins. This is safe
        // because a conforming writer never resubmits a list index with different bytes, and a
        // non-conforming one is still caught below: DecodeEntries or the root check fails either way.
        Dictionary<long, string> payloads = [];

        await foreach (LedgerTransaction tx in _source.AccountTransactionsAsync(record.Writer, manifest.FirstLedger, manifest.LastLedger, forward: false, cancellationToken).ConfigureAwait(false))
        {
            if (!IsMine(tx, record.Writer, manifest)) continue;

            long? index = MemoLayout.ListIndexOf(tx.MemoTypeHex);
            if (index is null || index.Value >= manifest.Lists || tx.MemoDataHex is null) continue;

            // The first copy this backward walk meets for an index is the last one in canonical
            // order, and that is the one kept -- ignore any earlier (canonical) copy seen after.
            if (!payloads.ContainsKey(index.Value))
            {
                payloads[index.Value] = tx.MemoDataHex;
            }

            if (payloads.Count == manifest.Lists) break;
        }

        if (payloads.Count != manifest.Lists || !Enumerable.Range(0, manifest.Lists).All(i => payloads.ContainsKey(i)))
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

            byte[] decoded;

            try
            {
                decoded = MemoLayout.Bytes(tx.MemoDataHex);
            }
            catch (FormatException)
            {
                continue;
            }

            // A memo decoding to more bytes than the manifest's chunk size is not a chunk, the same
            // as one that fails to decode -- accepting it here would let the sum below overflow
            // before the block-hash check ever gets a chance to catch the corruption.
            if (decoded.Length > manifest.Chunk) continue;

            chunks[slot] = decoded;

            if (++found == count) break;
        }

        if (found != count)
        {
            int missing = Array.FindIndex(chunks, chunk => chunk is null);
            throw new XrplFileException(XrplFileError.ChunkMissing,
                $"chunk {firstChunk + missing} of block {block.Index} is not in ledgers {block.FirstLedger}..{manifest.LastLedger}", block.Index);
        }

        // The chunk rule accepts a MemoData shorter than the manifest's chunk size, because the
        // file's last chunk is short. That makes a block's length the manifest's business, not the
        // memos': every chunk but the file's last is exactly `chunk` bytes. A writer that cut its
        // file into short chunks and hashed them honestly produces a block whose hash is right and
        // whose bytes are fewer than promised -- and the zeros a caller's buffer would keep in
        // their place are covered by no hash at all. The length is checked before the hash because
        // it is the check the honest-hash case fails.
        long total = chunks.Sum(chunk => (long)chunk!.Length);
        long expected = Math.Min(manifest.Size - firstChunk * manifest.Chunk, (long)count * manifest.Chunk);

        if (total != expected)
        {
            throw new XrplFileException(XrplFileError.BlockMismatch,
                $"block {block.Index} assembles to {total} bytes and the manifest implies {expected}", block.Index);
        }

        string hash = Convert.ToHexString(FileTree.BlockHash(chunks!));
        if (!string.Equals(hash, block.HashHex, StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.BlockMismatch,
                $"block {block.Index} hashes to {hash}, the list says {block.HashHex}", block.Index);
        }

        byte[] bytes = new byte[(int)total];
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
