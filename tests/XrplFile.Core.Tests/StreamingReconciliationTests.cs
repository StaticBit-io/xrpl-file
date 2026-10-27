using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public sealed class StreamingReconciliationTests : IDisposable
{
    private const int Fee = 10;
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-s20-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100, FencePoll = TimeSpan.FromMilliseconds(1) };

    private static FakeLedgerSource Node()
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(Fee, 1_000_000, 200_000) };
        source.Balances[Wallet.ClassicAddress] = 500_000_000;
        return source;
    }

    private static void ForgetTheEnding(string statePath)
    {
        PublicationState state = PublicationState.Load(statePath);
        (state with { Phase = "manifest", ManifestHash = null }).Save(statePath);
    }

    private static void Plant(FakeLedgerSource ledger, string hash, string marker, string data) =>
        ledger.Add(new LedgerTransaction(hash, ledger.CurrentLedger, Wallet.ClassicAddress, true, true, "tesSUCCESS", marker, data, MemoLayout.JsonFormatHex)
        {
            TransactionType = "AccountSet", Sequence = 0, TicketSequence = 900_000, Fee = 12,
        });

    private static BatchTestNode FundedBatch()
    {
        BatchTestNode node = new();
        node.Ledger.Fees = new NetworkFees(10, 1_000_000, 200_000);
        node.Ledger.Balances[node.Writer.Account] = 100_000_000;
        node.Ledger.Balances[node.Payer.Account] = 100_000_000;
        return node;
    }

    private static BatchStrategyFactory Factory(BatchTestNode node) =>
        (budget, fence) => new BatchStrategy(node.Sink, node.Ledger, node.Writer, node.Payer, budget, fence, BatchTestNode.FastOptions);

    /// <summary>A Batch run stopped as it starts its transmission number <paramref name="stopAtOrdinal"/>.</summary>
    private async Task<(PublicationSession Session, byte[] File)> InterruptedBatchAsync(BatchTestNode node, BatchStrategyFactory factory, int stopAtOrdinal)
    {
        byte[] file = VectorStream.Bytes("s20-batch", 40_000);
        PublicationSession first = await PublicationSession.StartAsync(
            factory, node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options, Token);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        node.Sink.Transmitting = transmission =>
        {
            if (transmission.Ordinal >= stopAtOrdinal) stop.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(null, stop.Token));
        node.Sink.Transmitting = null;

        // The fake closes a ledger on each read; what the cancelled run had sent may still be open.
        for (int read = 0; read < 3; read++) await node.Ledger.CurrentLedgerAsync(Token);

        return (first, file);
    }

    /// <summary>
    /// What one streaming pass keeps. The result is not exposed on the session, and a list that is kept
    /// but never asked for leaves no trace in the report, so the pass is read directly.
    /// </summary>
    private static async Task<LandedMemos> ReconcileAsync(PublicationSession session, long to) =>
        (await session.StreamReconcileAsync(to, false, null, Token)).Landed;

    /// <summary>A finished two-block run whose ending is forgotten, as a crash before "done" leaves it.</summary>
    private async Task<(FakeLedgerSource Ledger, FakeLedgerSink Sink, SequenceStrategy Strategy, byte[] File, PublicationSession Run, PublicationReport Original)> ForgottenEndingAsync()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("s20", 1_100_000);
        SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee, ValidationPoll = TimeSpan.FromMilliseconds(1) });
        PublicationSession run = await PublicationSession.StartAsync(strategy, ledger, file, "application/octet-stream", null, Wallet.ClassicAddress, "key", "testnet", Options, Token);
        PublicationReport original = await run.RunAsync(null, Token);
        Assert.True(original.Manifest.Blocks >= 2, "the run needs a block list");
        ForgetTheEnding(run.StatePath);

        return (ledger, sink, strategy, file, run, original);
    }

    private static async Task<LedgerTransaction> WritersListAsync(FakeLedgerSource ledger, long index)
    {
        await foreach (LedgerTransaction tx in ledger.AccountTransactionsAsync(Wallet.ClassicAddress, 0, ledger.CurrentLedger, true, Token))
        {
            if (string.Equals(tx.MemoTypeHex, MemoLayout.ListMarkerHex(index), StringComparison.OrdinalIgnoreCase)) return tx;
        }

        throw new InvalidOperationException($"the run left no list memo {index}");
    }

    [Fact]
    public async Task A_finished_run_is_recognised_with_another_publications_lists_and_manifest_after_its_own()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("s20", 1_100_000);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee, ValidationPoll = TimeSpan.FromMilliseconds(1) });
        PublicationSession run = await PublicationSession.StartAsync(strategy, ledger, file, "application/octet-stream", null, Wallet.ClassicAddress, "key", "testnet", Options, Token);
        PublicationReport original = await run.RunAsync(null, Token);
        ForgetTheEnding(run.StatePath);

        // Another publication by the same writer, later in the same window: a list memo of index 0
        // with other blocks' hashes, and a manifest with the same root and other bytes.
        string foreignList = FileTree.EncodeEntries([new BlockEntry(0, new string('E', 64), 5_000), new BlockEntry(1, new string('F', 64), 5_001)])[0];
        Plant(ledger, new string('1', 64), MemoLayout.ListMarkerHex(0), MemoLayout.Utf8Hex(foreignList));
        Manifest foreign = original.Manifest with { FirstLedger = 1, LastLedger = 2 };
        Plant(ledger, new string('2', 64), MemoLayout.ManifestMarkerHex, MemoLayout.Utf8Hex(foreign.Encode()));
        int submitted = sink.Submitted.Count;
        int reads = ledger.AccountTransactionsCalls;
        List<PublishProgress> preparation = [];

        await using MemoryPublicationSource source = new(file);
        PublicationSession resumed = await PublicationSession.ResumeAsync(
            strategy, ledger, source, run.StatePath, Options, new SynchronousProgress<PublishProgress>(preparation.Add), Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(original.Link, report.Link);
        Assert.Equal(submitted, sink.Submitted.Count);
        Assert.Equal(reads + 2, ledger.AccountTransactionsCalls);
        Assert.Contains(preparation, progress => progress.Stage == "history");
    }

    [Fact]
    public async Task A_list_record_whose_index_is_past_this_files_list_count_is_not_kept()
    {
        (FakeLedgerSource ledger, _, SequenceStrategy strategy, byte[] file, PublicationSession run, PublicationReport original) = await ForgottenEndingAsync();
        await using SequenceStrategy owned = strategy;
        LedgerTransaction genuine = await WritersListAsync(ledger, 0);

        // This file's own entries, under an index its list cannot reach.
        int past = PublicationEstimator.BlockListMemoCount(original.Manifest.Blocks);
        Plant(ledger, new string('3', 64), MemoLayout.ListMarkerHex(past), genuine.MemoDataHex!);

        await using MemoryPublicationSource source = new(file);
        PublicationSession resumed = await PublicationSession.ResumeAsync(strategy, ledger, source, run.StatePath, Options, null, Token);
        LandedMemos landed = await ReconcileAsync(resumed, ledger.CurrentLedger);

        Assert.Equal(Enumerable.Range(0, original.ListCount), landed.Lists.Keys.Order());
    }

    [Fact]
    public async Task A_list_record_mixing_this_files_entries_with_another_files_is_not_kept()
    {
        (FakeLedgerSource ledger, FakeLedgerSink sink, SequenceStrategy strategy, byte[] file, PublicationSession run, PublicationReport original) = await ForgottenEndingAsync();
        await using SequenceStrategy owned = strategy;
        LedgerTransaction genuine = await WritersListAsync(ledger, 0);
        Assert.True(FileTree.TryDecodeFragment(MemoLayout.FromHexUtf8(genuine.MemoDataHex!), out IReadOnlyList<BlockEntry>? entries));

        // The same index, later in the window: kept, it would replace the genuine list and the manifest
        // built from the landing would no longer match, so a finished run would be sent again.
        string mixed = FileTree.EncodeEntries([entries[0], new BlockEntry(1, new string('F', 64), entries[1].FirstLedger)])[0];
        Plant(ledger, new string('4', 64), MemoLayout.ListMarkerHex(0), MemoLayout.Utf8Hex(mixed));
        int submitted = sink.Submitted.Count;

        await using MemoryPublicationSource source = new(file);
        PublicationSession resumed = await PublicationSession.ResumeAsync(strategy, ledger, source, run.StatePath, Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(original.Link, report.Link);
        Assert.Equal(submitted, sink.Submitted.Count);
        Assert.Equal(genuine.Hash, (await ReconcileAsync(resumed, ledger.CurrentLedger)).Lists[0].TxHash);
    }

    [Fact]
    public async Task A_finished_run_is_recognised_with_another_publications_manifest_before_its_own()
    {
        (FakeLedgerSource ledger, FakeLedgerSink sink, SequenceStrategy strategy, byte[] file, PublicationSession run, PublicationReport original) = await ForgottenEndingAsync();
        await using SequenceStrategy owned = strategy;

        // Another publication's manifest with the same root, ahead of the run's own in canonical order:
        // the manifest is told by its bytes, not by being the first one in the window.
        LedgerTransaction own = (await ledger.TransactionAsync(original.ManifestHash, Token))!;
        Manifest foreign = original.Manifest with { FirstLedger = 1, LastLedger = 2 };
        ledger.Remove(tx => string.Equals(tx.Hash, own.Hash, StringComparison.OrdinalIgnoreCase));

        // Plant puts the foreign manifest in the current ledger, so it sorts first only while the own one is in it too.
        Assert.Equal(own.LedgerIndex, ledger.CurrentLedger);
        Plant(ledger, new string('2', 64), MemoLayout.ManifestMarkerHex, MemoLayout.Utf8Hex(foreign.Encode()));
        ledger.Add(own);
        int submitted = sink.Submitted.Count;

        await using MemoryPublicationSource source = new(file);
        PublicationSession resumed = await PublicationSession.ResumeAsync(strategy, ledger, source, run.StatePath, Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(original.Link, report.Link);
        Assert.Equal(original.ManifestHash, PublicationState.Load(run.StatePath).ManifestHash);
        Assert.Equal(submitted, sink.Submitted.Count);
    }

    [Fact]
    public async Task A_record_of_another_account_with_the_writers_sequence_covers_nothing()
    {
        BatchTestNode node = FundedBatch();
        BatchStrategyFactory factory = Factory(node);
        (PublicationSession first, byte[] file) = await InterruptedBatchAsync(node, factory, stopAtOrdinal: 2);
        long startedIn = first.State.StartedInLedger;
        List<LedgerTransaction> records = [];
        await foreach (LedgerTransaction tx in node.Ledger.AccountTransactionsAsync(node.Writer.Account, startedIn, node.Ledger.CurrentLedger, true, Token))
        {
            if (string.Equals(tx.Account, node.Writer.Account, StringComparison.Ordinal) && tx.Sequence != 0) records.Add(tx);
        }

        Assert.NotEmpty(records);

        // The history index leaves one of the writer's records out for good, so a sequence of the run is uncovered.
        LedgerTransaction hidden = records[0];
        node.Ledger.HistoryHides = tx => string.Equals(tx.Hash, hidden.Hash, StringComparison.Ordinal);
        node.Ledger.HistoryHideReads = int.MaxValue;
        SessionOptions lagging = new()
        {
            StateDirectory = _stateDirectory,
            TicketsPerBatch = 100,
            FencePoll = TimeSpan.FromMilliseconds(1),
            HistoryLagLimit = TimeSpan.FromMilliseconds(200),
        };

        XrplFileException lacking = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            factory, node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", lagging, null, Token));
        Assert.Equal(XrplFileError.NodeLacksHistory, lacking.Error);

        // The payer sent a record that took the very sequences the hidden one took, and account_tx shows it to the writer too.
        node.Ledger.AddAffecting(
            new LedgerTransaction(new string('E', 64), hidden.LedgerIndex, node.Payer.Account, true, true, "tesSUCCESS", null, null, null)
            {
                TransactionType = hidden.TransactionType, Sequence = hidden.Sequence, TicketCount = hidden.TicketCount, Fee = 12,
            },
            node.Writer.Account);

        XrplFileException still = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            factory, node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", lagging, null, Token));
        Assert.Equal(XrplFileError.NodeLacksHistory, still.Error);
    }

    [Fact]
    public async Task A_Batch_resume_whose_history_stream_breaks_part_way_starts_it_again_and_pays_for_nothing_twice()
    {
        BatchTestNode node = FundedBatch();
        BatchStrategyFactory factory = Factory(node);
        (PublicationSession first, byte[] file) = await InterruptedBatchAsync(node, factory, stopAtOrdinal: 2);

        // The break fires only in a walk of more than five records: make sure the first one is.
        long startedIn = first.State.StartedInLedger;
        int writerRecords = 0;
        await foreach (LedgerTransaction _ in node.Ledger.AccountTransactionsAsync(node.Writer.Account, startedIn, node.Ledger.CurrentLedger, true, Token))
        {
            writerRecords++;
        }

        Assert.True(writerRecords > 5, $"the interrupted run left {writerRecords} writer records; the break needs more than five");
        int callsBefore = node.Ledger.Calls.Count;
        node.Ledger.HistoryFailsAfter = 5;

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            factory, node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);

        // Broken once, read again from the start: two walks of the writer from the run's first ledger.
        Assert.Equal(2, node.Ledger.Calls.Skip(callsBefore).Count(call => call.Account == node.Writer.Account && call.FromLedger == startedIn));

        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
        Assert.All((await node.LandedByMarkerAsync()).Where(pair => MemoLayout.ChunkIndexOf(pair.Key) is not null), pair => Assert.Single(pair.Value));
    }
}
