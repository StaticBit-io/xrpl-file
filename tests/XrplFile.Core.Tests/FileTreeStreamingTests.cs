using Xunit;

namespace XrplFile.Tests;

public class FileTreeStreamingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(1_000)]
    [InlineData(1_001)]
    [InlineData(1_024_000)]
    [InlineData(1_024_001)]
    [InlineData(2_048_000)]
    [InlineData(2_048_999)]
    [InlineData(3_000_017)]
    public async Task The_streaming_root_equals_the_in_memory_root(long size)
    {
        byte[] file = VectorStream.Bytes("tree", size);
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
        await using VectorPublicationSource source = new("tree", size);

        IReadOnlyList<byte[]> streamed = await FileTree.BlockHashesAsync(source, null, Token);

        Assert.Equal(FileTree.BlockCount(chunks.Count), streamed.Count);
        Assert.Equal(FileTree.BlockHashes(chunks), streamed);
        Assert.Equal(PublicationSession.RootOf(file), FileTree.RootHexOf(streamed));
        Assert.Equal(PublicationSession.RootOf(file), await PublicationSession.RootOfAsync(source, null, Token));
    }

    [Fact]
    public async Task Progress_counts_the_chunks_hashed_after_every_block()
    {
        await using VectorPublicationSource source = new("tree", 2_500_500);
        List<long> reports = [];

        await FileTree.BlockHashesAsync(source, new SynchronousProgress<long>(reports.Add), Token);

        Assert.Equal([1_024L, 2_048L, 2_501L], reports);
    }

    [Fact]
    public async Task The_streaming_pass_reads_every_chunk_once()
    {
        CountingPublicationSource source = new(new VectorPublicationSource("tree", 2_500_500));

        await FileTree.BlockHashesAsync(source, null, Token);

        Assert.Equal(2_501, source.Reads);
    }

    [Fact]
    public async Task An_empty_source_has_no_root()
    {
        await using MemoryPublicationSource source = new(Array.Empty<byte>());

        await Assert.ThrowsAsync<ArgumentException>(() => FileTree.BlockHashesAsync(source, null, Token));
    }

    [Fact]
    public void Entries_pair_each_block_hash_with_its_first_ledger()
    {
        byte[][] hashes = [MerkleTree.Leaf([1]), MerkleTree.Leaf([2])];

        IReadOnlyList<BlockEntry> entries = ManifestPlan.Entries(hashes, [40, 41]);

        Assert.Equal(
            [new BlockEntry(0, Convert.ToHexString(hashes[0]), 40), new BlockEntry(1, Convert.ToHexString(hashes[1]), 41)],
            entries);
        Assert.Throws<ArgumentException>(() => ManifestPlan.Entries(hashes, [40]));
    }
}
