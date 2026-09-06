using System.Security.Cryptography;

namespace XrplFile;

/// <summary>
/// The hash tree a file's identity is built from.
///
/// Leaves and nodes hash in different domains: a leaf is SHA256(0x00 ‖ data), a node is
/// SHA256(0x01 ‖ left ‖ right). Without the prefix, sixty-four bytes of payload hash to the same
/// value as the node over their two halves. An unpaired element at an odd level is carried up
/// alone, never paired with a copy of itself: with the copy, [a, b, c] and [a, b, c, c] share a root.
/// </summary>
public static class MerkleTree
{
    public const int HashBytes = 32;

    private const byte LeafPrefix = 0x00;
    private const byte NodePrefix = 0x01;

    public static byte[] Leaf(ReadOnlySpan<byte> data)
    {
        byte[] prefixed = new byte[1 + data.Length];
        prefixed[0] = LeafPrefix;
        data.CopyTo(prefixed.AsSpan(1));
        return SHA256.HashData(prefixed);
    }

    public static byte[] Node(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        byte[] prefixed = new byte[1 + left.Length + right.Length];
        prefixed[0] = NodePrefix;
        left.CopyTo(prefixed.AsSpan(1));
        right.CopyTo(prefixed.AsSpan(1 + left.Length));
        return SHA256.HashData(prefixed);
    }

    /// <summary>The root over these hashes, in this order.</summary>
    public static byte[] Root(IReadOnlyList<byte[]> hashes)
    {
        if (hashes.Count == 0)
        {
            throw new ArgumentException("a tree needs at least one leaf", nameof(hashes));
        }

        List<byte[]> level = [.. hashes];

        while (level.Count > 1)
        {
            List<byte[]> next = new((level.Count + 1) / 2);

            for (int i = 0; i + 1 < level.Count; i += 2)
            {
                next.Add(Node(level[i], level[i + 1]));
            }

            if (level.Count % 2 == 1)
            {
                next.Add(level[^1]);
            }

            level = next;
        }

        return level[0];
    }
}
