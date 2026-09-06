using Xunit;

namespace XrplFile.Tests;

public class XrplFileReaderTests
{
    private static FakeLedgerSource SourceFor(long size) => new FakeLedgerSource().AddRange(Vectors.Ledger(Vectors.File(size)));

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(1024001)]
    [InlineData(2500500)]
    public async Task A_vector_file_reads_back_byte_for_byte(long size)
    {
        VectorFile vector = Vectors.File(size);
        XrplFileReader reader = new(SourceFor(size));

        ReadResult result = await reader.ReadAsync(Vectors.Link, TestContext.Current.CancellationToken);

        Assert.Equal(VectorStream.Bytes(vector.Seed, vector.Size), result.Bytes);
        Assert.Equal(vector.Root, result.Manifest.Root);
        Assert.Equal(Vectors.Writer, result.Writer);
        Assert.Equal(vector.Blocks, result.Blocks.Count);
        Assert.Equal("application/octet-stream", result.Manifest.Type);
    }

    [Fact]
    public async Task A_single_block_file_needs_no_list_read()
    {
        FakeLedgerSource source = SourceFor(1001);
        XrplFileReader reader = new(source);

        ManifestRecord manifest = await reader.ManifestAsync(Vectors.Link, TestContext.Current.CancellationToken);
        IReadOnlyList<BlockEntry> blocks = await reader.BlockListAsync(manifest, TestContext.Current.CancellationToken);

        BlockEntry only = Assert.Single(blocks);
        Assert.Equal(new BlockEntry(0, manifest.Manifest.Root, manifest.Manifest.FirstLedger), only);
        Assert.Equal(0, source.AccountTransactionsCalls);
    }

    [Fact]
    public async Task A_duplicate_index_keeps_the_earlier_copy()
    {
        VectorFile vector = Vectors.File(1001);
        List<LedgerTransaction> ledger = Vectors.Ledger(vector);
        LedgerTransaction first = ledger.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 0);
        // Same ledger, added after the real one: later in canonical order, so it must lose.
        LedgerTransaction later = first with { Hash = Vectors.Hash("retry"), MemoDataHex = new string('0', 2000) };
        FakeLedgerSource source = new FakeLedgerSource().AddRange(ledger).Add(later);

        ReadResult result = await new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken);

        Assert.Equal(VectorStream.Bytes(vector.Seed, vector.Size), result.Bytes);
    }

    [Fact]
    public async Task A_corrupt_chunk_is_reported_with_its_block()
    {
        FakeLedgerSource source = SourceFor(2500500)
            .Replace(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 1500, tx => tx with { MemoDataHex = new string('F', 2000) });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.BlockMismatch, error.Error);
        Assert.Equal(1, error.Block);
    }

    [Fact]
    public async Task A_missing_chunk_is_reported_with_its_block()
    {
        FakeLedgerSource source = SourceFor(2500500).Remove(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 2400);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ChunkMissing, error.Error);
        Assert.Equal(2, error.Block);
        Assert.Contains("2400", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_or_foreign_transaction_is_not_a_chunk()
    {
        VectorFile vector = Vectors.File(1001);
        FakeLedgerSource source = new FakeLedgerSource().AddRange(Vectors.Ledger(vector));
        LedgerTransaction real = Vectors.Ledger(vector).Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 1);
        // A failed copy of chunk 1 earlier in the ledger, a stranger's chunk 1, and an unmarked memo.
        source.Add(real with { Hash = Vectors.Hash("failed"), Succeeded = false, MemoDataHex = "00" });
        source.Add(real with { Hash = Vectors.Hash("stranger"), Account = "rSomebodyElse", MemoDataHex = "00" });
        source.Add(real with { Hash = Vectors.Hash("unmarked"), MemoTypeHex = null, MemoDataHex = "00" });

        ReadResult result = await new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken);

        Assert.Equal(VectorStream.Bytes(vector.Seed, vector.Size), result.Bytes);
    }

    [Fact]
    public async Task A_list_that_does_not_match_the_root_is_refused_before_any_chunk()
    {
        FakeLedgerSource source = SourceFor(2500500)
            .Replace(tx => MemoLayout.ListIndexOf(tx.MemoTypeHex) == 0,
                tx => tx with { MemoDataHex = MemoLayout.Utf8Hex(MemoLayout.FromHexUtf8(tx.MemoDataHex!).Replace("\"l\":1000", "\"l\":1001", StringComparison.Ordinal).Replace('A', 'B')) });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ListMismatch, error.Error);
    }

    [Fact]
    public async Task A_manifest_the_node_does_not_know_is_not_found_on_a_full_history_node()
    {
        FakeLedgerSource source = new() { EarliestLedger = Endpoints.MainnetFirstLedger };

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ManifestAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ManifestNotFound, error.Error);
    }

    [Fact]
    public async Task A_manifest_the_node_does_not_know_is_a_history_problem_on_a_partial_node()
    {
        FakeLedgerSource source = new() { EarliestLedger = 100_000_000 };

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ManifestAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeLacksHistory, error.Error);
        Assert.Contains("100000000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_node_whose_history_starts_after_the_first_chunk_cannot_read_the_file()
    {
        FakeLedgerSource source = SourceFor(1001);
        source.EarliestLedger = 1001;

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeLacksHistory, error.Error);
    }

    [Theory]
    [InlineData("{\"v\":1,\"type\":\"a/b\",\"size\":1001,\"chunk\":1000,\"root\":\"" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "\",\"ledgers\":[1000,1000],\"blocks\":2,\"lists\":1}")]
    [InlineData("not json")]
    public async Task A_manifest_whose_counts_disagree_is_corrupt(string json)
    {
        FakeLedgerSource source = SourceFor(1001)
            .Replace(tx => MemoLayout.IsManifest(tx.MemoTypeHex), tx => tx with { MemoDataHex = MemoLayout.Utf8Hex(json) });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ManifestAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.CorruptManifest, error.Error);
    }

    [Fact]
    public async Task A_transaction_that_is_not_a_manifest_is_not_found()
    {
        FakeLedgerSource source = SourceFor(1001)
            .Replace(tx => MemoLayout.IsManifest(tx.MemoTypeHex), tx => tx with { MemoTypeHex = MemoLayout.ChunkMarkerHex(5) });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ManifestAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ManifestNotFound, error.Error);
    }

    [Fact]
    public async Task A_file_above_the_ceiling_is_refused_before_any_chunk_is_read()
    {
        FakeLedgerSource source = SourceFor(2500500);
        XrplFileReader reader = new(source, new ReaderOptions { MaxBytes = 2_000_000 });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => reader.ReadAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.TooLarge, error.Error);
        Assert.Equal(0, source.AccountTransactionsCalls);
    }

    [Fact]
    public async Task A_block_is_read_from_its_own_ledger_not_the_files_first()
    {
        VectorFile vector = Vectors.File(2500500);
        FakeLedgerSource source = SourceFor(2500500);
        XrplFileReader reader = new(source);
        ManifestRecord manifest = await reader.ManifestAsync(Vectors.Link, TestContext.Current.CancellationToken);
        IReadOnlyList<BlockEntry> blocks = await reader.BlockListAsync(manifest, TestContext.Current.CancellationToken);
        // Break every chunk before block 2 — a reader that starts at the file's first ledger would trip on them.
        source.Replace(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is { } i && i < 2048 && tx.LedgerIndex < vector.Ledger.BlockLedgers[2], tx => tx with { Succeeded = false });

        byte[] block = await reader.BlockAsync(manifest, blocks[2], TestContext.Current.CancellationToken);

        Assert.Equal(VectorStream.Bytes(vector.Seed, vector.Size).Skip(2048 * 1000).ToArray(), block);
    }
}
