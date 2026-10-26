using Xunit;

namespace XrplFile.Tests;

public class LazyMemoListTests
{
    [Fact]
    public void It_builds_nothing_until_an_element_is_read_and_never_caches_one()
    {
        CountingPublicationSource source = new(new VectorPublicationSource("lazy", 5_000));
        LazyMemoList memos = new(source, IndexRanges.Of([(1, 2), (4, 1)]));

        Assert.Equal(3, memos.Count);
        Assert.Equal(0, source.Reads);

        MemoSubmission second = memos[1];
        Assert.Equal(1, source.Reads);
        _ = memos[1];
        Assert.Equal(2, source.Reads);

        Assert.Equal(MemoLayout.ChunkMarkerHex(2), second.MemoTypeHex);
        Assert.Equal(MemoLayout.Hex(VectorStream.Bytes("lazy", 5_000).AsSpan(2_000, 1_000)), second.MemoDataHex);
        Assert.Null(second.MemoFormatHex);
        Assert.Equal(MemoLayout.ChunkMarkerHex(4), memos.MarkerHexAt(2));
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void The_last_element_of_a_file_one_byte_past_a_block_is_that_byte()
    {
        byte[] file = VectorStream.Bytes("lazy", 1_024_001);
        ChunkLanding landing = new(FileTree.ChunkCount(file.Length, MemoLayout.ChunkBytes));
        LazyMemoList memos = new(new MemoryPublicationSource(file), landing.Missing());

        Assert.Equal(1_025, memos.Count);
        Assert.Equal(1_024, memos.ChunkAt(1_024));
        Assert.Equal(MemoLayout.Hex(file.AsSpan(1_024_000)), memos[1_024].MemoDataHex);
    }

    [Fact]
    public void Enumerating_reads_in_order_and_one_at_a_time()
    {
        CountingPublicationSource source = new(new VectorPublicationSource("lazy", 3_000));
        LazyMemoList memos = new(source, IndexRanges.Of([(0, 3)]));
        int seen = 0;

        foreach (MemoSubmission memo in memos)
        {
            Assert.Equal(MemoLayout.ChunkMarkerHex(seen), memo.MemoTypeHex);
            Assert.Equal(++seen, source.Reads);
        }
    }

    [Fact]
    public void Ranges_map_positions_to_chunks_and_back()
    {
        IndexRanges ranges = IndexRanges.Of([(3, 2), (10, 3)]);

        Assert.Equal([3L, 4L, 10L, 11L, 12L], ranges.ToArray());
        Assert.Equal(3, ranges.IndexOf(11));
        Assert.Equal(-1, ranges.IndexOf(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ranges[5]);
    }

    [Fact]
    public void Missing_chunks_come_back_as_ranges()
    {
        ChunkLanding landing = new(200);
        for (long chunk = 0; chunk < 200; chunk++)
        {
            if (chunk is not (5 or 6 or 7 or 64 or 199)) landing.Land(chunk, 10);
        }

        IndexRanges missing = landing.Missing();

        Assert.Equal([5L, 6L, 7L, 64L, 199L], missing.ToArray());
    }
}
