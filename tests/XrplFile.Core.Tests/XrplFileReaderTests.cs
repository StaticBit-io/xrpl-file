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
    public async Task A_duplicate_index_keeps_the_first_copy_in_canonical_order()
    {
        // 1024001 bytes: block 0 spans ledgers 1000..1010, wide enough that the reader must walk
        // through the middle of the block before it finishes, rather than completing on the first
        // handful of chunks the way the 1001-byte vector's 2-chunk file does.
        VectorFile vector = Vectors.File(1024001);
        List<LedgerTransaction> ledger = Vectors.Ledger(vector);
        LedgerTransaction real = ledger.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 0);
        // The real copy of chunk 0 is at ledger 1000, first in canonical order. The duplicate sits
        // at ledger 1005, strictly inside the block's range, so the reader must pass it mid-block
        // instead of never reaching it.
        LedgerTransaction duplicate = real with { Hash = Vectors.Hash("retry"), LedgerIndex = 1005, MemoDataHex = new string('F', real.MemoDataHex!.Length) };
        FakeLedgerSource source = new FakeLedgerSource().AddRange(ledger).Add(duplicate);

        ReadResult result = await new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken);

        Assert.Equal(VectorStream.Bytes(vector.Seed, vector.Size), result.Bytes);
    }

    [Fact]
    public async Task A_wrong_copy_earlier_in_canonical_order_than_the_real_one_wins_and_breaks_the_hash()
    {
        // Proves the rule really is "first wins", not "the right one wins": if the wrong copy sits
        // earlier in canonical order than the real one, the reader must keep the wrong bytes and
        // fail the block hash -- not silently prefer the correct copy because it happens to exist.
        VectorFile vector = Vectors.File(1024001);
        List<LedgerTransaction> ledger = Vectors.Ledger(vector);
        LedgerTransaction real = ledger.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 500);
        // The real copy of chunk 500 is at ledger 1005; the wrong copy is at ledger 1002, earlier
        // in canonical order, still strictly inside block 0's range (1000..1010).
        LedgerTransaction wrongButEarlier = real with { Hash = Vectors.Hash("wrong-but-earlier"), LedgerIndex = 1002, MemoDataHex = new string('F', real.MemoDataHex!.Length) };
        FakeLedgerSource source = new FakeLedgerSource().AddRange(ledger).Add(wrongButEarlier);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.BlockMismatch, error.Error);
        Assert.Equal(0, error.Block);
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
    public async Task An_oversized_chunk_is_skipped_so_a_later_real_copy_still_wins()
    {
        // manifest.Chunk is 1000 for this vector; a memo decoding to 2000 bytes is well over that
        // and must be treated like a non-hex memo -- skipped, not accepted into the slot.
        VectorFile vector = Vectors.File(1024001);
        List<LedgerTransaction> ledger = Vectors.Ledger(vector);
        LedgerTransaction real = ledger.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 0);
        List<LedgerTransaction> withOversizedFirstCopy = ledger
            .Select(tx => ReferenceEquals(tx, real) ? tx with { MemoDataHex = new string('A', 4000) } : tx)
            .ToList();
        FakeLedgerSource source = new FakeLedgerSource().AddRange(withOversizedFirstCopy)
            // The good copy, later in canonical order, still inside block 0's range (1000..1010).
            .Add(real with { Hash = Vectors.Hash("good-later"), LedgerIndex = 1005 });

        ReadResult result = await new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken);

        Assert.Equal(VectorStream.Bytes(vector.Seed, vector.Size), result.Bytes);
    }

    [Fact]
    public async Task An_oversized_chunk_with_no_other_copy_is_reported_as_missing()
    {
        FakeLedgerSource source = SourceFor(2500500)
            .Replace(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 0, tx => tx with { MemoDataHex = new string('A', 4000) });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ChunkMissing, error.Error);
        Assert.Equal(0, error.Block);
    }

    [Fact]
    public async Task A_failed_or_foreign_transaction_is_not_a_chunk()
    {
        // 1024001 bytes so block 0 spans ledgers 1000..1010 and the injected transactions, placed
        // at ledger 1005 (chunk 500's own ledger, strictly inside the block), are met mid-block
        // instead of after the reader has already finished on the file's first couple of chunks.
        VectorFile vector = Vectors.File(1024001);
        List<LedgerTransaction> ledger = Vectors.Ledger(vector);
        FakeLedgerSource source = new FakeLedgerSource().AddRange(ledger);
        LedgerTransaction real = ledger.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 500);
        // A failed copy of chunk 500, a stranger's chunk 500, and an unmarked memo, all at ledger 1005.
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
    public async Task A_stray_out_of_range_list_memo_is_a_list_mismatch_not_a_framework_exception()
    {
        // The manifest promises 1 list memo (xf/b/0). Re-marking it xf/b/7 leaves the account with
        // a stray out-of-range index and no xf/b/0 at all -- without the index-range filter this
        // used to slip past the count guard and blow up as KeyNotFoundException while decoding.
        FakeLedgerSource source = SourceFor(2500500)
            .Replace(tx => MemoLayout.ListIndexOf(tx.MemoTypeHex) == 0, tx => tx with { MemoTypeHex = MemoLayout.ListMarkerHex(7) });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ListMismatch, error.Error);
    }

    [Fact]
    public async Task Chunks_shorter_than_the_manifest_implies_are_a_block_mismatch_not_zero_padding()
    {
        // The chunk rule accepts a MemoData shorter than `chunk`, so a writer can cut a 2500-byte
        // file into five 500-byte chunks, hash them honestly, and claim size 5000: the block hash
        // matches, the root matches, every count agrees. Reading that used to return 5000 bytes of
        // which 2500 were zeros no hash covered -- the only path on which unverified bytes reached
        // a caller. The browser reader is held to the same rule.
        byte[] real = VectorStream.Bytes("short", 2500);
        IReadOnlyList<byte[]> chunks = FileTree.Split(real, 500);
        Manifest manifest = new(
            "application/octet-stream", 5000, 1000, Convert.ToHexString(FileTree.BlockHash(chunks)), 1000, 1000, 1, 0, null);
        Assert.Null(manifest.Inconsistency());

        FakeLedgerSource source = new();
        for (int i = 0; i < chunks.Count; i++)
        {
            source.Add(new LedgerTransaction(
                Vectors.Hash($"short:{i}"), 1000, Vectors.Writer, Succeeded: true, Validated: true, Result: "tesSUCCESS",
                MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunks[i]), MemoFormatHex: null));
        }

        source.Add(new LedgerTransaction(
            Vectors.ManifestHash, 1001, Vectors.Writer, Succeeded: true, Validated: true, Result: "tesSUCCESS",
            MemoLayout.ManifestMarkerHex, MemoLayout.Utf8Hex(manifest.Encode()), MemoLayout.JsonFormatHex));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.BlockMismatch, error.Error);
        Assert.Equal(0, error.Block);
        Assert.Contains("2500", error.Message, StringComparison.Ordinal);
        Assert.Contains("5000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_entry_in_a_list_memo_is_a_list_mismatch()
    {
        // "[null]" deserializes to an array holding a null, and reading a member off it used to
        // reach a caller as NullReferenceException instead of the browser reader's ListMismatch.
        FakeLedgerSource source = SourceFor(2500500)
            .Replace(tx => MemoLayout.ListIndexOf(tx.MemoTypeHex) == 0, tx => tx with { MemoDataHex = MemoLayout.Utf8Hex("[null]") });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ListMismatch, error.Error);
    }

    [Fact]
    public async Task An_entry_with_no_ledger_is_a_list_mismatch_not_a_block_read_from_ledger_zero()
    {
        // The list keeps its real indices and its real hashes, so the root still matches; only
        // block 1's "l" is gone. A non-nullable field read that as 0 and the reader went on to
        // page block 1 from ledger 0 -- on a real node, a scan of the writer's whole history.
        // The browser reader has always refused this list; now this one does too.
        FakeLedgerSource source = SourceFor(2500500)
            .Replace(tx => MemoLayout.ListIndexOf(tx.MemoTypeHex) == 0,
                tx => tx with { MemoDataHex = MemoLayout.Utf8Hex(MemoLayout.FromHexUtf8(tx.MemoDataHex!).Replace(",\"l\":1010", string.Empty, StringComparison.Ordinal)) });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ListMismatch, error.Error);
    }

    [Fact]
    public async Task A_duplicate_list_index_keeps_the_last_copy_in_canonical_order()
    {
        // The 2500500-byte vector's single list memo (xf/b/0) sits at ledger 1026, after every
        // chunk. A wrong-but-valid copy planted earlier in canonical order must lose to it: the
        // block-list rule keeps the LAST copy, the opposite of the chunk rule (section 5 vs 6).
        VectorFile vector = Vectors.File(2500500);
        List<LedgerTransaction> ledger = Vectors.Ledger(vector);
        LedgerTransaction real = ledger.Single(tx => MemoLayout.ListIndexOf(tx.MemoTypeHex) == 0);
        string wrongPayload = MemoLayout.FromHexUtf8(real.MemoDataHex!).Replace('A', 'B');
        // Ledger 1010 is inside block 1's range and strictly earlier than the real copy's 1026, so
        // this loses under "last wins" without the backward walk ever needing to reach it.
        LedgerTransaction wrongButEarlier = real with { Hash = Vectors.Hash("list-wrong-but-earlier"), LedgerIndex = 1010, MemoDataHex = MemoLayout.Utf8Hex(wrongPayload) };
        FakeLedgerSource source = new FakeLedgerSource().AddRange(ledger).Add(wrongButEarlier);

        ReadResult result = await new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken);

        Assert.Equal(VectorStream.Bytes(vector.Seed, vector.Size), result.Bytes);
    }

    [Fact]
    public async Task A_wrong_copy_later_in_canonical_order_than_the_real_list_memo_wins_and_breaks_the_root()
    {
        // Proves the rule really is "last wins", not "the right one wins": a wrong-but-valid copy
        // planted after the real one, at the same ledger, must be the one used -- producing
        // ListMismatch, not silently preferring the correct copy because it happens to exist.
        VectorFile vector = Vectors.File(2500500);
        List<LedgerTransaction> ledger = Vectors.Ledger(vector);
        LedgerTransaction real = ledger.Single(tx => MemoLayout.ListIndexOf(tx.MemoTypeHex) == 0);
        string wrongPayload = MemoLayout.FromHexUtf8(real.MemoDataHex!).Replace('A', 'B');
        LedgerTransaction wrongButLater = real with { Hash = Vectors.Hash("list-wrong-but-later"), MemoDataHex = MemoLayout.Utf8Hex(wrongPayload) };
        FakeLedgerSource source = new FakeLedgerSource().AddRange(ledger).Add(wrongButLater);

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

        byte[] block = await reader.BlockAsync(manifest, blocks[2], TestContext.Current.CancellationToken);

        Assert.Equal(VectorStream.Bytes(vector.Seed, vector.Size).Skip(2048 * 1000).ToArray(), block);
        // The discriminating check: BlockAsync must page from the block's own first ledger, not the
        // file's -- breaking chunks by index (as a prior version of this test did) proves nothing,
        // since BlockAsync filters chunks by index regardless of where the scan starts.
        AccountTransactionsCall blockCall = Assert.Single(source.Calls, call => call.Forward);
        Assert.Equal(blocks[2].FirstLedger, blockCall.FromLedger);
        Assert.True(blockCall.Forward);
    }

    // The 2,500,500-byte vector puts 100 chunks in a ledger, so block 1 starts in the ledger that
    // holds chunks 1000..1099, which block 0 ends in, and block 2 in the one holding 2000..2099.
    // A reader that walks each block from its own first ledger reads those two ledgers twice.
    private static async Task<(ManifestRecord Manifest, IReadOnlyList<BlockEntry> Blocks)> OpenAsync(XrplFileReader reader)
    {
        ManifestRecord manifest = await reader.ManifestAsync(Vectors.Link, TestContext.Current.CancellationToken);
        return (manifest, await reader.BlockListAsync(manifest, TestContext.Current.CancellationToken));
    }

    private static byte[] BlockBytes(VectorFile vector, int block) =>
        [.. VectorStream.Bytes(vector.Seed, vector.Size).Skip(block * FileTree.BlockChunks * vector.Chunk).Take(FileTree.BlockChunks * vector.Chunk)];

    [Fact]
    public async Task Blocks_sharing_a_ledger_take_each_record_from_the_node_once()
    {
        VectorFile vector = Vectors.File(2500500);
        FakeLedgerSource source = SourceFor(2500500);
        XrplFileReader reader = new(source);
        (ManifestRecord manifest, IReadOnlyList<BlockEntry> blocks) = await OpenAsync(reader);
        Assert.Equal(blocks[0].FirstLedger + 10, blocks[1].FirstLedger);
        int listCalls = source.AccountTransactionsCalls;
        long listRecords = source.RecordsYielded;

        for (int block = 0; block < blocks.Count; block++)
        {
            Assert.Equal(BlockBytes(vector, block), await reader.BlockAsync(manifest, blocks[block], TestContext.Current.CancellationToken));
        }

        Assert.Equal(manifest.Manifest.ChunkCount, source.RecordsYielded - listRecords);
        AccountTransactionsCall walk = Assert.Single(source.Calls.Skip(listCalls));
        Assert.Equal(blocks[0].FirstLedger, walk.FromLedger);
    }

    [Fact]
    public async Task A_shared_ledger_whose_next_block_comes_first_is_replayed_not_read_again()
    {
        // Canonical order inside a ledger is not index order: here block 1's chunks come before
        // block 0's last ones, so block 0 reads to the shared ledger's end and block 1's chunks
        // in it are already behind the walk when block 1 starts.
        VectorFile vector = Vectors.File(2500500);
        List<LedgerTransaction> ledger = Vectors.Ledger(vector);
        long shared = ledger.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 1000).LedgerIndex;
        List<LedgerTransaction> sharedChunks = [.. ledger.Where(tx => tx.LedgerIndex == shared)];
        ledger.RemoveAll(tx => tx.LedgerIndex == shared);
        ledger.AddRange(sharedChunks.Where(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) >= 1024));
        ledger.AddRange(sharedChunks.Where(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) < 1024));
        FakeLedgerSource source = new FakeLedgerSource().AddRange(ledger);
        XrplFileReader reader = new(source);
        (ManifestRecord manifest, IReadOnlyList<BlockEntry> blocks) = await OpenAsync(reader);
        int listCalls = source.AccountTransactionsCalls;
        long listRecords = source.RecordsYielded;

        Assert.Equal(BlockBytes(vector, 0), await reader.BlockAsync(manifest, blocks[0], TestContext.Current.CancellationToken));
        Assert.Equal(BlockBytes(vector, 1), await reader.BlockAsync(manifest, blocks[1], TestContext.Current.CancellationToken));

        Assert.Single(source.Calls.Skip(listCalls));
        // Block 0 reads 1,100 records, the whole shared ledger included; block 1 takes the 76 of
        // its own that ledger holds from what block 0 read and the other 948 from the node.
        Assert.Equal(1100 + 948, source.RecordsYielded - listRecords);
    }

    [Fact]
    public async Task A_wrong_copy_in_a_shared_ledger_still_wins_over_the_real_one_after_it()
    {
        // The first copy in canonical order is the chunk, whether the walk meets it from the node
        // or among what the block before it read. Here the wrong copy of chunk 1024 is the shared
        // ledger's first record, read by block 0's walk, which has no use for it.
        List<LedgerTransaction> ledger = Vectors.Ledger(Vectors.File(2500500));
        int at = ledger.FindIndex(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 1000);
        LedgerTransaction real = ledger.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 1024);
        ledger.Insert(at, real with { Hash = Vectors.Hash("wrong:1024"), MemoDataHex = new string('F', real.MemoDataHex!.Length) });
        FakeLedgerSource source = new FakeLedgerSource().AddRange(ledger);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.BlockMismatch, error.Error);
        Assert.Equal(1, error.Block);
    }

    [Fact]
    public async Task A_copy_before_a_blocks_first_ledger_is_not_its_chunk_even_when_an_earlier_block_read_it()
    {
        // Block 0's walk passes this copy of chunk 1024; block 1's range starts after it, so a
        // fresh walk for block 1 never meets it, and neither may what block 1 takes from block 0.
        VectorFile vector = Vectors.File(2500500);
        List<LedgerTransaction> ledger = Vectors.Ledger(vector);
        LedgerTransaction real = ledger.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 1024);
        LedgerTransaction middle = ledger.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 500);
        ledger.Insert(ledger.IndexOf(middle), real with { Hash = Vectors.Hash("early:1024"), LedgerIndex = middle.LedgerIndex, MemoDataHex = new string('F', real.MemoDataHex!.Length) });
        FakeLedgerSource source = new FakeLedgerSource().AddRange(ledger);

        ReadResult result = await new XrplFileReader(source).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken);

        Assert.Equal(VectorStream.Bytes(vector.Seed, vector.Size), result.Bytes);
    }

    [Fact]
    public async Task A_walk_the_node_stopped_answering_is_taken_again_from_the_blocks_own_ledger()
    {
        VectorFile vector = Vectors.File(2500500);
        FakeLedgerSource source = SourceFor(2500500);
        XrplFileReader reader = new(source);
        (ManifestRecord manifest, IReadOnlyList<BlockEntry> blocks) = await OpenAsync(reader);
        int listCalls = source.AccountTransactionsCalls;

        // Block 0 takes 1,024 records of the walk; carried on into block 1, it fails 500 later.
        source.HistoryFailsAfter = 1524;
        await reader.BlockAsync(manifest, blocks[0], TestContext.Current.CancellationToken);
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => reader.BlockAsync(manifest, blocks[1], TestContext.Current.CancellationToken));
        byte[] retried = await reader.BlockAsync(manifest, blocks[1], TestContext.Current.CancellationToken);

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Equal(BlockBytes(vector, 1), retried);
        Assert.Equal([blocks[0].FirstLedger, blocks[1].FirstLedger], source.Calls.Skip(listCalls).Select(call => call.FromLedger));
    }

    [Fact]
    public async Task A_walk_is_not_carried_on_under_another_callers_token()
    {
        // A carried-over walk keeps the token it was started with; going on with it for a caller
        // holding another one would leave that caller unable to cancel its own requests.
        VectorFile vector = Vectors.File(2500500);
        FakeLedgerSource source = SourceFor(2500500);
        XrplFileReader reader = new(source);
        (ManifestRecord manifest, IReadOnlyList<BlockEntry> blocks) = await OpenAsync(reader);
        using CancellationTokenSource first = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using CancellationTokenSource second = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int listCalls = source.AccountTransactionsCalls;

        await reader.BlockAsync(manifest, blocks[0], first.Token);
        await first.CancelAsync();
        byte[] next = await reader.BlockAsync(manifest, blocks[1], second.Token);

        Assert.Equal(BlockBytes(vector, 1), next);
        Assert.Equal([blocks[0].FirstLedger, blocks[1].FirstLedger], source.Calls.Skip(listCalls).Select(call => call.FromLedger));
    }

    [Fact]
    public async Task Blocks_read_at_the_same_time_each_come_out_whole()
    {
        VectorFile vector = Vectors.File(2500500);
        XrplFileReader reader = new(SourceFor(2500500));
        (ManifestRecord manifest, IReadOnlyList<BlockEntry> blocks) = await OpenAsync(reader);

        byte[][] read = await Task.WhenAll(blocks.Select(block => reader.BlockAsync(manifest, block, TestContext.Current.CancellationToken)));

        for (int block = 0; block < blocks.Count; block++)
        {
            Assert.Equal(BlockBytes(vector, block), read[block]);
        }
    }
}
