using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace XrplFile;

/// <summary>One block of a file as its published list describes it.</summary>
/// <param name="Index">The block's position; block 0 holds chunks 0 to 1023.</param>
/// <param name="HashHex">The Merkle root over the block's chunk leaves, upper-case hex.</param>
/// <param name="FirstLedger">The ledger holding the block's first chunk — where a reader starts paging for this block.</param>
public sealed record BlockEntry(int Index, string HashHex, long FirstLedger);

/// <summary>
/// The two-level tree that is a file's identity: chunks group into blocks of 1024, a block hashes
/// to the root over its chunk leaves, the file hashes to the root over the block hashes in order.
/// Two levels so that any block can be verified on its own against the published list.
/// </summary>
public static class FileTree
{
    public const int BlockChunks = 1024;

    public static int BlockOf(long chunkIndex) => (int)(chunkIndex / BlockChunks);

    public static int BlockCount(long chunkCount) =>
        chunkCount <= 0 ? 0 : (int)((chunkCount + BlockChunks - 1) / BlockChunks);

    public static long ChunkCount(long size, int chunkBytes) =>
        size <= 0 ? 0 : (size + chunkBytes - 1) / chunkBytes;

    public static IReadOnlyList<byte[]> Split(ReadOnlyMemory<byte> file, int chunkBytes)
    {
        List<byte[]> chunks = new((int)ChunkCount(file.Length, chunkBytes));

        for (int at = 0; at < file.Length; at += chunkBytes)
        {
            chunks.Add(file.Slice(at, Math.Min(chunkBytes, file.Length - at)).ToArray());
        }

        return chunks;
    }

    public static byte[] BlockHash(IReadOnlyList<byte[]> chunks) =>
        MerkleTree.Root([.. chunks.Select(chunk => MerkleTree.Leaf(chunk))]);

    /// <summary>Every block's hash, from chunks in memory. For small files and tests; publication streams (spec S3).</summary>
    public static IReadOnlyList<byte[]> BlockHashes(IReadOnlyList<byte[]> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        int blocks = BlockCount(chunks.Count);
        List<byte[]> hashes = new(blocks);

        for (int block = 0; block < blocks; block++)
        {
            hashes.Add(BlockHash([.. chunks.Skip(block * BlockChunks).Take(BlockChunks)]));
        }

        return hashes;
    }

    /// <summary>
    /// Every block's hash in one forward pass over the source, keeping only the 32 bytes of each
    /// (spec S3): 1,651 blocks of a 1.7 GB file are 53 KB. <paramref name="progress"/> hears the
    /// number of chunks hashed after each block.
    /// </summary>
    public static async Task<IReadOnlyList<byte[]>> BlockHashesAsync(
        IPublicationSource source, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        long chunks = ChunkCount(source.Length, MemoLayout.ChunkBytes);
        if (chunks == 0) throw new ArgumentException("an empty file has no chunks and no root", nameof(source));

        int blocks = BlockCount(chunks);
        List<byte[]> hashes = new(blocks);
        List<byte[]> leaves = new(BlockChunks);
        byte[] buffer = new byte[MemoLayout.ChunkBytes];

        for (int block = 0; block < blocks; block++)
        {
            leaves.Clear();
            long first = (long)block * BlockChunks;
            long end = Math.Min(first + BlockChunks, chunks);

            for (long chunk = first; chunk < end; chunk++)
            {
                int length = await source.ReadChunkAsync(chunk, buffer, cancellationToken).ConfigureAwait(false);
                leaves.Add(MerkleTree.Leaf(buffer.AsSpan(0, length)));
            }

            hashes.Add(MerkleTree.Root(leaves));
            progress?.Report(end);
        }

        return hashes;
    }

    /// <summary>The file's root over its block hashes, in block order.</summary>
    public static string RootHexOf(IReadOnlyList<byte[]> blockHashes) => Convert.ToHexString(MerkleTree.Root(blockHashes));

    public static string RootHex(IReadOnlyList<BlockEntry> entries) =>
        Convert.ToHexString(MerkleTree.Root(
            [.. entries.OrderBy(entry => entry.Index).Select(entry => Convert.FromHexString(entry.HashHex))]));

    /// <summary>The list cut into memo-sized JSON payloads, greedily.</summary>
    public static IReadOnlyList<string> EncodeEntries(IReadOnlyList<BlockEntry> entries)
    {
        List<string> payloads = [];
        List<Wire> current = [];

        foreach (BlockEntry entry in entries)
        {
            Wire wire = new(entry.Index, entry.HashHex, entry.FirstLedger);
            current.Add(wire);

            if (Encoding.UTF8.GetByteCount(Serialize(current)) > MemoLayout.ListBytes)
            {
                current.RemoveAt(current.Count - 1);
                payloads.Add(Serialize(current));
                current = [wire];
            }
        }

        if (current.Count > 0) payloads.Add(Serialize(current));

        return payloads;
    }

    /// <summary>
    /// The list back from its memos, in index order, or a FormatException. Refused rather than
    /// repaired: a gap, a duplicate or a malformed hash means this is not the list the root was
    /// computed over.
    /// </summary>
    public static IReadOnlyList<BlockEntry> DecodeEntries(IEnumerable<string> payloads)
    {
        List<BlockEntry> entries = [];

        foreach (string payload in payloads)
        {
            Wire[]? wires;

            try
            {
                wires = JsonSerializer.Deserialize<Wire[]>(payload);
            }
            catch (JsonException e)
            {
                throw new FormatException("a block-list memo is not a JSON array of entries", e);
            }

            foreach (Wire? wire in wires ?? [])
            {
                // "[null]" deserializes to an array holding a null, which used to be dereferenced
                // a line below; a member the writer left out arrives as null rather than as the
                // zero a non-nullable field would have quietly invented. Both are the same
                // failure: this is not the list the root was computed over.
                if (wire is null) throw new FormatException("a block-list memo carries an entry that is not an object");

                if (wire.h is null || wire.h.Length != MerkleTree.HashBytes * 2 || !wire.h.All(Uri.IsHexDigit))
                {
                    throw new FormatException($"block {Named(wire.i)} carries a hash that is not 32 bytes of hex");
                }

                if (wire.i is null || wire.l is null)
                {
                    throw new FormatException($"block {Named(wire.i)} has no index or no ledger");
                }

                entries.Add(new BlockEntry(wire.i.Value, wire.h.ToUpperInvariant(), wire.l.Value));
            }
        }

        if (entries.Count == 0) throw new FormatException("the block list is empty");

        entries.Sort((a, b) => a.Index.CompareTo(b.Index));

        for (int expected = 0; expected < entries.Count; expected++)
        {
            if (entries[expected].Index != expected)
            {
                throw new FormatException(
                    $"the block list is not 0..{entries.Count - 1}: found {entries[expected].Index} where {expected} was expected");
            }
        }

        return entries;
    }

    /// <summary>
    /// One block-list memo's entries, or false when it is not a JSON array of well-formed entries
    /// (spec S20). Unlike <see cref="DecodeEntries"/> it does not ask for the whole list: a resume
    /// reads lists one memo at a time and keeps only those that name this file's blocks.
    /// </summary>
    internal static bool TryDecodeFragment(string payload, [NotNullWhen(true)] out IReadOnlyList<BlockEntry>? entries)
    {
        entries = null;
        Wire[]? wires;

        try
        {
            wires = JsonSerializer.Deserialize<Wire[]>(payload);
        }
        catch (JsonException)
        {
            return false;
        }

        if (wires is null || wires.Length == 0) return false;

        List<BlockEntry> decoded = new(wires.Length);

        foreach (Wire? wire in wires)
        {
            if (wire?.i is not { } index || wire.l is not { } ledger) return false;
            if (wire.h is not { } hash || hash.Length != MerkleTree.HashBytes * 2 || !hash.All(Uri.IsHexDigit)) return false;

            decoded.Add(new BlockEntry(index, hash.ToUpperInvariant(), ledger));
        }

        entries = decoded;
        return true;
    }

    private static string Serialize(List<Wire> wires) => JsonSerializer.Serialize(wires);

    private static string Named(int? index) => index?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";

    /// <summary>
    /// The shape on the wire. Single letters because every byte here is a memo byte; every member
    /// nullable because a decoder must be able to tell an absent member from a zero one wrote.
    /// </summary>
#pragma warning disable IDE1006 // lower-case names are the JSON member names, not a naming mistake
    private sealed record Wire(int? i, string? h, long? l);
#pragma warning restore IDE1006
}
