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

            foreach (Wire wire in wires ?? [])
            {
                if (wire.h is null || wire.h.Length != MerkleTree.HashBytes * 2 || !wire.h.All(Uri.IsHexDigit))
                {
                    throw new FormatException($"block {wire.i} carries a hash that is not 32 bytes of hex");
                }

                entries.Add(new BlockEntry(wire.i, wire.h.ToUpperInvariant(), wire.l));
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

    private static string Serialize(List<Wire> wires) => JsonSerializer.Serialize(wires);

    /// <summary>The shape on the wire. Single letters because every byte here is a memo byte.</summary>
#pragma warning disable IDE1006 // lower-case names are the JSON member names, not a naming mistake
    private sealed record Wire(int i, string h, long l);
#pragma warning restore IDE1006
}
