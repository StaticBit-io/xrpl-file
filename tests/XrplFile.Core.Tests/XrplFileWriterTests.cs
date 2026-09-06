using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class XrplFileWriterTests
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();

    [Fact]
    public async Task A_two_block_file_is_published_and_reads_back()
    {
        VectorFile vector = Vectors.File(1024001);
        byte[] file = VectorStream.Bytes(vector.Seed, vector.Size);
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        List<PublishProgress> progress = [];

        PublishResult result = await new XrplFileWriter(sink, ledger, new WriterOptions { BatchSize = 200 }).PublishAsync(
            file, "application/octet-stream", "vector.bin", Wallet, "testnet",
            new Progress<PublishProgress>(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(1025, result.ChunkHashes.Count);
        Assert.Single(result.ListHashes);
        Assert.Equal("testnet", result.Link.Network);
        Assert.Equal(result.ManifestHash, result.Link.TxHash);
        Assert.Equal(vector.Root, result.Manifest.Root);
        Assert.Equal(2, result.Manifest.Blocks);
        Assert.Equal(1, result.Manifest.Lists);
        // Chunks fill ledgers 5000..5015; the default BatchSize (200) makes the fake close a ledger
        // five times inside the 1025-chunk loop (after chunks 200, 400, 600, 800, 1000) plus once
        // more at the stage end, so the list lands in 5016 and the manifest in 5017.
        Assert.Equal(5000, result.Manifest.FirstLedger);
        Assert.Equal(5016, result.Manifest.LastLedger);

        ReadResult read = await new XrplFileReader(ledger).ReadAsync(result.Link, TestContext.Current.CancellationToken);
        Assert.Equal(file, read.Bytes);
        Assert.Equal(5015, read.Blocks[1].FirstLedger);
    }

    [Fact]
    public async Task A_batch_of_submissions_waits_for_validation_before_the_next_batch()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        WriterOptions options = new() { BatchSize = 100 };
        byte[] file = VectorStream.Bytes("batch", 2_500_000);

        PublishResult result = await new XrplFileWriter(sink, ledger, options).PublishAsync(
            file, "application/octet-stream", "batch.bin", Wallet, "testnet", null, TestContext.Current.CancellationToken);

        Assert.Equal(2500, result.ChunkHashes.Count);

        int lastChunkPosition = sink.Events.LastIndexOf("chunk");
        int queriesBeforeLastChunk = sink.Events.Take(lastChunkPosition).Count(kind => kind == "query");

        // 2 500 chunks in batches of 100 make 25 validation waits over the course of the chunk
        // loop; since 2 500 is itself an exact multiple of the batch size, the 25th wait falls
        // immediately after the last chunk rather than before it, so exactly 24 land strictly
        // earlier. The remaining queries (the chunk stage's own end-of-stage wait, plus the list
        // and manifest stages that follow) push the whole-run total well past 25, so that count
        // is only bounded, not pinned to an exact value.
        Assert.Equal(24, queriesBeforeLastChunk);
        Assert.True(sink.Events.Count(kind => kind == "query") >= 25, "expected at least 25 validation queries over the whole run");
    }

    [Fact]
    public async Task The_default_batch_size_does_not_interrupt_a_small_file()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        await new XrplFileWriter(sink, ledger).PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet, "testnet", null, TestContext.Current.CancellationToken);

        int firstChunk = sink.Events.IndexOf("chunk");
        int lastChunk = sink.Events.LastIndexOf("chunk");

        // Three chunks, well under the default BatchSize of 200: no validated-sequence query is
        // interleaved between them, only the stage wait that follows the whole batch.
        Assert.DoesNotContain("query", sink.Events.GetRange(firstChunk, lastChunk - firstChunk + 1));
    }

    [Fact]
    public async Task Every_transaction_is_a_bare_account_set_with_one_memo()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        await new XrplFileWriter(sink, ledger).PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet, "testnet", null, TestContext.Current.CancellationToken);

        Assert.Equal(4, sink.Submitted.Count);   // three chunks and the manifest; one block, no list
        Assert.All(sink.Submitted, tx =>
        {
            Assert.Equal("AccountSet", tx["TransactionType"]);
            Assert.Equal(Wallet.ClassicAddress, tx["Account"]);
            Assert.Equal("12", tx["Fee"]);
            Assert.Equal(Wallet.PublicKey, tx["SigningPubKey"]);
            Assert.Equal(new[] { "Account", "Fee", "Memos", "Sequence", "SigningPubKey", "TransactionType" }, tx.Keys.Order().ToArray());
        });
        Assert.Equal(new uint[] { 10, 11, 12, 13 }, sink.Submitted.Select(tx => Convert.ToUInt32(tx["Sequence"])).ToArray());
    }

    [Fact]
    public async Task Chunks_carry_their_index_and_the_manifest_carries_its_marker_and_format()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        PublishResult result = await new XrplFileWriter(sink, ledger).PublishAsync(
            VectorStream.Bytes("s", 1500), "text/plain", "s.txt", Wallet, "mainnet", null, TestContext.Current.CancellationToken);

        LedgerTransaction chunk1 = (await ledger.TransactionAsync(result.ChunkHashes[1], TestContext.Current.CancellationToken))!;
        Assert.Equal(1, MemoLayout.ChunkIndexOf(chunk1.MemoTypeHex));
        Assert.Null(chunk1.MemoFormatHex);
        Assert.Equal(500 * 2, chunk1.MemoDataHex!.Length);

        LedgerTransaction manifest = (await ledger.TransactionAsync(result.ManifestHash, TestContext.Current.CancellationToken))!;
        Assert.True(MemoLayout.IsManifest(manifest.MemoTypeHex));
        Assert.Equal(MemoLayout.JsonFormatHex, manifest.MemoFormatHex);
        Assert.Equal(result.Manifest, Manifest.Decode(MemoLayout.FromHexUtf8(manifest.MemoDataHex!)));
        Assert.Equal("s.txt", result.Manifest.Name);
    }

    [Fact]
    public async Task A_refused_submission_stops_the_run_before_the_manifest()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.Refusals[1] = "tecINSUFFICIENT_RESERVE";

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => new XrplFileWriter(sink, ledger).PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet, "testnet", null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Contains("tecINSUFFICIENT_RESERVE", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, sink.Submitted.Count);
        Assert.DoesNotContain(sink.Submitted, tx => MemoLayout.IsManifest(FirstMemoType(tx)));
    }

    [Fact]
    public async Task A_read_back_that_does_not_match_stops_the_run_before_the_manifest()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.StoredAs[2] = _ => new string('0', 1000);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => new XrplFileWriter(sink, ledger).PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet, "testnet", null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.BlockMismatch, error.Error);
        Assert.Equal(3, sink.Submitted.Count);
    }

    [Fact]
    public async Task A_single_validation_timeout_does_not_stop_the_publish()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger) { ValidatedQueryTimeouts = 1 };

        PublishResult result = await new XrplFileWriter(sink, ledger).PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet, "testnet", null, TestContext.Current.CancellationToken);

        Assert.Equal(result.ManifestHash, result.Link.TxHash);
        Assert.Equal(0, sink.ValidatedQueryTimeouts);
    }

    [Fact]
    public async Task A_read_back_longer_than_the_file_is_a_block_mismatch()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Three chunks of 1000, 1000, 500 bytes; corrupt the last (position 2) to decode to a
        // full 1000-byte chunk instead of its real 500 -- a length-changing corruption, unlike
        // the same-length one above, which the existing test cannot produce.
        sink.StoredAs[2] = _ => new string('a', 2000);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => new XrplFileWriter(sink, ledger).PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet, "testnet", null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.BlockMismatch, error.Error);
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        FakeLedgerSource ledger = new();

        await Assert.ThrowsAsync<ArgumentException>(() => new XrplFileWriter(new FakeLedgerSink(ledger), ledger).PublishAsync(
            ReadOnlyMemory<byte>.Empty, "text/plain", null, Wallet, "testnet", null, TestContext.Current.CancellationToken));
    }

    private static string? FirstMemoType(Dictionary<string, object> tx)
    {
        System.Text.Json.JsonElement memo = System.Text.Json.JsonSerializer.SerializeToElement(tx["Memos"])[0].GetProperty("Memo");
        return memo.TryGetProperty("MemoType", out System.Text.Json.JsonElement type) ? type.GetString() : null;
    }
}
