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
    public async Task A_Batch_resume_whose_history_stream_breaks_part_way_starts_it_again_and_pays_for_nothing_twice()
    {
        BatchTestNode node = new();
        node.Ledger.Fees = new NetworkFees(10, 1_000_000, 200_000);
        node.Ledger.Balances[node.Writer.Account] = 100_000_000;
        node.Ledger.Balances[node.Payer.Account] = 100_000_000;
        BatchStrategyFactory factory = (budget, fence) => new BatchStrategy(node.Sink, node.Ledger, node.Writer, node.Payer, budget, fence, BatchTestNode.FastOptions);
        byte[] file = VectorStream.Bytes("s20-batch", 40_000);
        PublicationSession first = await PublicationSession.StartAsync(
            factory, node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options, Token);
        using (CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            node.Sink.Transmitting = transmission =>
            {
                if (transmission.Ordinal >= 2) stop.Cancel();
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(null, stop.Token));
            node.Sink.Transmitting = null;
        }

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
        Assert.True(node.Ledger.Calls.Skip(callsBefore).Count(call => call.Account == node.Writer.Account && call.FromLedger == startedIn) >= 2);

        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
        Assert.All((await node.LandedByMarkerAsync()).Where(pair => MemoLayout.ChunkIndexOf(pair.Key) is not null), pair => Assert.Single(pair.Value));
    }
}
