using System.Text;
using Xunit;

namespace XrplFile.Tests;

public class MerkleTreeTests
{
    private static byte[] Leaf(string text) => MerkleTree.Leaf(Encoding.UTF8.GetBytes(text));

    private static List<byte[]> Leaves(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Leaf($"leaf-{i}"))];

    [Fact]
    public void A_leaf_is_sha256_over_a_zero_prefix_and_the_bytes()
    {
        byte[] expected = System.Security.Cryptography.SHA256.HashData(new byte[] { 0x00, (byte)'a' });

        Assert.Equal(expected, MerkleTree.Leaf("a"u8));
    }

    [Fact]
    public void A_node_is_sha256_over_a_one_prefix_and_both_children()
    {
        byte[] left = Leaf("l");
        byte[] right = Leaf("r");
        byte[] expected = System.Security.Cryptography.SHA256.HashData(new byte[] { 0x01 }.Concat(left).Concat(right).ToArray());

        Assert.Equal(expected, MerkleTree.Node(left, right));
    }

    [Fact]
    public void A_node_cannot_pass_as_a_leaf()
    {
        byte[] left = Leaf("l");
        byte[] right = Leaf("r");
        byte[] node = MerkleTree.Node(left, right);
        byte[] leafOverSameBytes = MerkleTree.Leaf(left.Concat(right).ToArray());

        Assert.NotEqual(node, leafOverSameBytes);
    }

    [Theory]
    [InlineData(1, "305DF59F9590C3C9AC63D2B2743C388E3792449078CEBF7FB3DBE6471643B2B7")]
    [InlineData(3, "CF763A041C81CEEF1578A6083F75C61BEF2E0014F2A3E683A97FCFCA5BE7F19A")]
    [InlineData(5, "00D21829A5503145348ABCF712513EACF2A274211AD83E970202BB5B6D80B286")]
    [InlineData(1025, "59564AA4292CEA48C658B67D5D4E0E04694BF5517C6D41122963399C66F97B5A")]
    public void The_root_matches_the_platform_known_answers(int count, string expected)
    {
        Assert.Equal(expected, Convert.ToHexString(MerkleTree.Root(Leaves(count))));
    }

    [Fact]
    public void An_unpaired_element_is_promoted_not_duplicated()
    {
        List<byte[]> three = Leaves(3);
        List<byte[]> fourWithCopy = [.. three, three[2]];

        Assert.NotEqual(MerkleTree.Root(three), MerkleTree.Root(fourWithCopy));
        Assert.Equal(MerkleTree.Node(MerkleTree.Node(three[0], three[1]), three[2]), MerkleTree.Root(three));
    }

    [Fact]
    public void One_leaf_is_its_own_root()
    {
        byte[] only = Leaf("only");

        Assert.Equal(only, MerkleTree.Root([only]));
    }

    [Fact]
    public void An_empty_tree_is_refused()
    {
        Assert.Throws<ArgumentException>(() => MerkleTree.Root([]));
    }
}
