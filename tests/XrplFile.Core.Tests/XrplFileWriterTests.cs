using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class XrplFileWriterTests
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);

    [Fact]
    public async Task A_two_block_file_is_published_and_reads_back()
    {
        VectorFile vector = Vectors.File(1024001);
        byte[] file = VectorStream.Bytes(vector.Seed, vector.Size);
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        List<PublishProgress> progress = [];

        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { BatchSize = 200 });
        XrplFileWriter writer = new(strategy, ledger);

        PublishResult result = await writer.PublishAsync(
            file, "application/octet-stream", "vector.bin", Wallet.ClassicAddress, "testnet",
            null, new Progress<PublishProgress>(progress.Add), TestContext.Current.CancellationToken);

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

    /// <summary>
    /// The writer adapts a strategy's per-memo count into its own stage report, and that adapter
    /// must run on the strategy's thread rather than posting to the thread pool. A caller passes
    /// its own <see cref="IProgress{T}"/> precisely to choose where reports land; a
    /// <see cref="Progress{T}"/> in the middle of the chain takes that choice away, and the
    /// desktop application's idle lock — touched on every report — then sees signs of life arrive
    /// arbitrarily late under load and locks a vault out from under a running publication.
    /// </summary>
    /// <remarks>
    /// The probe sits where nothing else can: a strategy decorator sees the adapter the writer
    /// passes down, so it can report through it and then ask, before its own <c>Report</c> call has
    /// returned, whether the caller heard. A posted report answers no. Reintroducing
    /// <see cref="Progress{T}"/> here would fail this almost every time rather than every time —
    /// the pool could in principle win the race — which is the strongest a test can be about
    /// something that is only ever late, never absent.
    /// </remarks>
    [Fact]
    public async Task A_strategys_progress_reaches_the_caller_before_the_report_returns()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        int heard = 0;

        await using SequenceStrategy inner = new(sink, Signer, new SequenceStrategyOptions { BatchSize = 200 });
        ProbingStrategy strategy = new(inner, () => Volatile.Read(ref heard));
        XrplFileWriter writer = new(strategy, ledger);

        await writer.PublishAsync(
            VectorStream.Bytes("inline", 4096), "application/octet-stream", "inline.bin",
            Wallet.ClassicAddress, "testnet", null,
            new InlineCounter(() => Interlocked.Increment(ref heard)), TestContext.Current.CancellationToken);

        Assert.Equal(1, strategy.HeardDuringReport);
    }

    /// <summary>
    /// Wraps a real strategy and, once, reports through the adapter the writer handed down, then
    /// records how many reports the caller had heard by the time that call returned.
    /// </summary>
    private sealed class ProbingStrategy(ISubmissionStrategy inner, Func<int> heard) : ISubmissionStrategy
    {
        /// <summary>Reports the caller heard while the probing <c>Report</c> was on the stack, or -1 if it never ran.</summary>
        public int HeardDuringReport { get; private set; } = -1;

        public long FeeDropsPerTransaction => inner.FeeDropsPerTransaction;

        public string Account => inner.Account;

        public async Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
            IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
        {
            IReadOnlyList<SubmittedMemo> landed =
                await inner.SubmitAllAsync(memos, submitted, cancellationToken).ConfigureAwait(false);

            if (submitted is not null && HeardDuringReport < 0)
            {
                int before = heard();
                submitted.Report(landed.Count);
                HeardDuringReport = heard() - before;
            }

            return landed;
        }

        public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken) =>
            inner.ReleaseLeftoverTicketsAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>A caller's progress that counts reports on whatever thread they arrive on.</summary>
    private sealed class InlineCounter(Action heard) : IProgress<PublishProgress>
    {
        public void Report(PublishProgress value) => heard();
    }

    [Fact]
    public async Task A_batch_of_submissions_waits_for_validation_before_the_next_batch()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        SequenceStrategyOptions options = new() { BatchSize = 100 };
        byte[] file = VectorStream.Bytes("batch", 2_500_000);

        await using SequenceStrategy strategy = new(sink, Signer, options);
        XrplFileWriter writer = new(strategy, ledger);

        PublishResult result = await writer.PublishAsync(
            file, "application/octet-stream", "batch.bin", Wallet.ClassicAddress, "testnet", null, null, TestContext.Current.CancellationToken);

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

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        await writer.PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, TestContext.Current.CancellationToken);

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

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        await writer.PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, TestContext.Current.CancellationToken);

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

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        PublishResult result = await writer.PublishAsync(
            VectorStream.Bytes("s", 1500), "text/plain", "s.txt", Wallet.ClassicAddress, "mainnet", null, null, TestContext.Current.CancellationToken);

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

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => writer.PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, TestContext.Current.CancellationToken));

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

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => writer.PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.BlockMismatch, error.Error);
        Assert.Equal(3, sink.Submitted.Count);
    }

    [Fact]
    public async Task A_single_validation_timeout_does_not_stop_the_publish()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger) { ValidatedQueryTimeouts = 1 };

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        PublishResult result = await writer.PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, TestContext.Current.CancellationToken);

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

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => writer.PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.BlockMismatch, error.Error);
    }

    [Fact]
    public async Task An_unknown_network_is_refused_before_anything_is_submitted()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        // The link is built from the network at the very end of a publication. Refusing it there
        // meant a thousand paid transactions and a manifest already in the ledger, and the
        // exception carried no manifest hash -- the file was published and its link lost.
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => writer.PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "main-net", null, null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.InvalidLink, error.Error);
        Assert.Empty(sink.Submitted);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task A_network_named_in_another_case_is_published_under_its_canonical_name()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        PublishResult result = await writer.PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "TestNet", null, null, TestContext.Current.CancellationToken);

        Assert.Equal("testnet", result.Link.Network);
    }

    [Fact]
    public async Task A_node_that_answers_with_its_own_error_is_a_refusal_naming_it_not_an_unreachable_node()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // What an unfunded wallet gets: the node answered, and precisely.
        sink.Throws[0] = new Xrpl.Client.Exceptions.RippledException(
            "Account not found.", new Xrpl.Models.Subscriptions.ErrorResponse { Error = "actNotFound" });

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => writer.PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Contains("actNotFound", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("did not answer", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("not connected")]
    [InlineData("connection")]
    [InlineData("timeout")]
    public async Task A_node_that_does_not_answer_is_still_an_unreachable_node(string failure)
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.Throws[0] = failure switch
        {
            "disconnected" => new Xrpl.Client.Exceptions.DisconnectedException("the socket went away"),
            "not connected" => new Xrpl.Client.Exceptions.NotConnectedException("never connected"),
            "connection" => new Xrpl.Client.Exceptions.ConnectionException("the handshake failed"),
            _ => new Xrpl.Client.Exceptions.TimeoutException("the node took too long"),
        };

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => writer.PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }

    [Fact]
    public async Task An_sdk_failure_that_is_not_the_node_speaking_is_an_unreachable_node()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // The SDK raises the bare base type on its connect path - "Websocket connection never
        // cleaned up", "Connect: created null websocket". The node never spoke, so this is the
        // same answer the reading side gives, not a refusal.
        sink.Throws[0] = new Xrpl.Client.Exceptions.XrplException("Connect: created null websocket");

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => writer.PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Contains("null websocket", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transaction_validated_with_a_failure_stops_the_run_as_a_refusal()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Accepted on submission and validated as failed: the tec* a transaction only reaches once
        // the ledger closes, which no engine result announced. The writer meets it where it asks
        // the first chunk which ledger it landed in.
        sink.FailsAs.Add(0);

        await using SequenceStrategy strategy = new(sink, Signer);
        XrplFileWriter writer = new(strategy, ledger);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => writer.PublishAsync(
            VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Contains("did not succeed", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sink.Submitted, tx => MemoLayout.IsManifest(FirstMemoType(tx)));
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        FakeLedgerSource ledger = new();

        await using SequenceStrategy strategy = new(new FakeLedgerSink(ledger), Signer);
        XrplFileWriter writer = new(strategy, ledger);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.PublishAsync(
            ReadOnlyMemory<byte>.Empty, "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, TestContext.Current.CancellationToken));
    }

    private static string? FirstMemoType(Dictionary<string, object> tx)
    {
        System.Text.Json.JsonElement memo = System.Text.Json.JsonSerializer.SerializeToElement(tx["Memos"])[0].GetProperty("Memo");
        return memo.TryGetProperty("MemoType", out System.Text.Json.JsonElement type) ? type.GetString() : null;
    }
}
