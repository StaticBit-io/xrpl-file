using System.Text;
using Xunit;

namespace XrplFile.Tests;

public class FileTreeTests
{
    private static List<byte[]> Chunks(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Encoding.UTF8.GetBytes($"chunk-{i}".PadRight(MemoLayout.ChunkBytes, 'x')))];

    [Fact]
    public void A_block_hash_matches_the_platform_known_answer()
    {
        Assert.Equal(
            "EED1B8043341A7F523DFF401B394D9C43B073226091649CD4F4C8BA018D5BFBD",
            Convert.ToHexString(FileTree.BlockHash(Chunks(5))));
    }

    [Fact]
    public void The_root_over_block_entries_matches_the_platform_known_answer()
    {
        List<BlockEntry> entries =
            [.. Enumerable.Range(0, 3).Select(i => new BlockEntry(i, Convert.ToHexString(MerkleTree.Leaf([(byte)i])), 1000 + i * 40))];

        Assert.Equal("3B6CCCD7E3E023FF393006F030315EE7AD9EB111B022B41FBA7E5B7A3973F688", FileTree.RootHex(entries));
    }

    [Fact]
    public void Entries_are_ordered_by_index_whatever_order_they_arrive_in()
    {
        List<BlockEntry> ordered =
            [.. Enumerable.Range(0, 3).Select(i => new BlockEntry(i, Convert.ToHexString(MerkleTree.Leaf([(byte)i])), 1000))];
        List<BlockEntry> shuffled = [ordered[2], ordered[0], ordered[1]];

        Assert.Equal(FileTree.RootHex(ordered), FileTree.RootHex(shuffled));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1024, 1)]
    [InlineData(1025, 2)]
    [InlineData(2501, 3)]
    public void Block_count_is_the_ceiling_over_1024(long chunks, int blocks)
    {
        Assert.Equal(blocks, FileTree.BlockCount(chunks));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1000, 1)]
    [InlineData(1001, 2)]
    [InlineData(2500500, 2501)]
    public void Chunk_count_is_the_ceiling_over_the_chunk_size(long size, long chunks)
    {
        Assert.Equal(chunks, FileTree.ChunkCount(size, MemoLayout.ChunkBytes));
    }

    [Fact]
    public void Split_cuts_the_file_into_full_chunks_and_a_short_tail()
    {
        byte[] file = new byte[2500];
        Random.Shared.NextBytes(file);

        IReadOnlyList<byte[]> chunks = FileTree.Split(file, 1000);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(1000, chunks[0].Length);
        Assert.Equal(500, chunks[2].Length);
        Assert.Equal(file, chunks.SelectMany(chunk => chunk).ToArray());
    }

    [Fact]
    public void Encoded_entries_fit_the_list_budget_and_decode_back()
    {
        List<BlockEntry> entries =
            [.. Enumerable.Range(0, 100).Select(i => new BlockEntry(i, Convert.ToHexString(MerkleTree.Leaf([(byte)i])), 106497159 + i * 11))];

        IReadOnlyList<string> payloads = FileTree.EncodeEntries(entries);

        Assert.True(payloads.Count >= 10, $"100 entries should need at least ten memos, got {payloads.Count}");
        Assert.All(payloads, payload => Assert.True(Encoding.UTF8.GetByteCount(payload) <= MemoLayout.ListBytes));
        Assert.Equal(entries, FileTree.DecodeEntries(payloads));
    }

    [Fact]
    public void An_entry_is_compact_json_with_single_letter_names()
    {
        string payload = Assert.Single(FileTree.EncodeEntries([new BlockEntry(0, new string('A', 64), 1000)]));

        Assert.Equal("[{\"i\":0,\"h\":\"" + new string('A', 64) + "\",\"l\":1000}]", payload);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[{\"i\":1,\"h\":\"" + "AA" + "\",\"l\":1}]")]
    [InlineData("[{\"i\":1,\"h\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"l\":1}]")]
    [InlineData("[]")]
    // A null entry: Deserialize<Wire[]> yields an array holding a null, and reading a member off
    // it used to raise NullReferenceException past the reader's catch instead of ListMismatch.
    [InlineData("[null]")]
    // An entry with no index or no ledger: non-nullable members would read both as 0, and the
    // reader would then page block 0 from ledger 0 as though the writer had said so.
    [InlineData("[{\"h\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"l\":1}]")]
    [InlineData("[{\"i\":0,\"h\":\"0000000000000000000000000000000000000000000000000000000000000000\"}]")]
    [InlineData("[{\"i\":null,\"h\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"l\":null}]")]
    public void A_broken_list_is_refused_not_repaired(string payload)
    {
        Assert.Throws<FormatException>(() => FileTree.DecodeEntries([payload]));
    }

    [Fact]
    public void A_duplicate_index_is_refused()
    {
        string hash = new string('B', 64);
        string payload = "[{\"i\":0,\"h\":\"" + hash + "\",\"l\":1},{\"i\":0,\"h\":\"" + hash + "\",\"l\":2}]";

        Assert.Throws<FormatException>(() => FileTree.DecodeEntries([payload]));
    }

    [Fact]
    public void A_lower_case_hash_comes_back_upper_case()
    {
        string payload = "[{\"i\":0,\"h\":\"" + new string('c', 64) + "\",\"l\":1}]";

        Assert.Equal(new string('C', 64), FileTree.DecodeEntries([payload])[0].HashHex);
    }

    [Fact]
    public void One_list_memo_decodes_on_its_own_and_garbage_does_not()
    {
        List<BlockEntry> entries =
            [.. Enumerable.Range(0, 30).Select(i => new BlockEntry(i, Convert.ToHexString(MerkleTree.Leaf([(byte)i])), 106497159 + i))];
        IReadOnlyList<string> payloads = FileTree.EncodeEntries(entries);
        Assert.True(payloads.Count > 1);

        Assert.True(FileTree.TryDecodeFragment(payloads[^1], out IReadOnlyList<BlockEntry>? fragment));
        Assert.Equal(entries.Skip(entries.Count - fragment.Count).Take(fragment.Count), fragment);
        Assert.False(FileTree.TryDecodeFragment("not json", out _));
        Assert.False(FileTree.TryDecodeFragment("[{\"i\":0}]", out _));
        Assert.False(FileTree.TryDecodeFragment("[]", out _));
    }
}
