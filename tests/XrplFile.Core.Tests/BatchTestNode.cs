using Xrpl.Wallet;

namespace XrplFile.Tests;

/// <summary>A writer, a payer, a fake ledger and a fast strategy over them, shared by the Batch strategy tests.</summary>
internal sealed class BatchTestNode
{
    public const uint WriterStart = 100;
    public const uint PayerStart = 500;

    public BatchTestNode(Action<FakeBatchLedgerSink>? script = null)
    {
        Ledger = new FakeLedgerSource();
        Sink = new FakeBatchLedgerSink(Ledger);
        Sink.SetSequence(Writer.Account, WriterStart);
        Sink.SetSequence(Payer.Account, PayerStart);
        script?.Invoke(Sink);
    }

    public ITransactionSigner Writer { get; } = new WalletSigner(XrplWallet.Generate());

    public ITransactionSigner Payer { get; } = new WalletSigner(XrplWallet.Generate());

    public FakeLedgerSource Ledger { get; }

    public FakeBatchLedgerSink Sink { get; }

    public RecordingFence Fence { get; } = new();

    public static BatchStrategyOptions FastOptions { get; } = new()
    {
        ChainCheck = TimeSpan.FromMilliseconds(5),

        // The fake closes a ledger on every read, and a send has 20 ledgers before its deadline. Chain
        // checks (5 ms) already close up to 200 a second; a progress sweep every 5 ms doubled that,
        // so a send held up by a loaded host for ~50 ms met an expired deadline. At 250 ms the
        // sweep adds 4 a second. Tests that assert on progress use ProgressOptions.
        ProgressSweep = TimeSpan.FromMilliseconds(250),
        HistoryLagLimit = TimeSpan.FromSeconds(2),
        FeeWait = TimeSpan.FromMilliseconds(20),

        // The fake closes a ledger on every read, so its deadlines pass in milliseconds: a 400 ms
        // retry step would meet a resolved candidate instead of its resend.
        TransportRetryStep = TimeSpan.FromMilliseconds(1),

        // Ten times the default: a send held up by a loaded host must not outlive its deadline just
        // because the chain check closed 20 fake ledgers meanwhile. The test of the default says so itself.
        DeadlineLedgers = 200,
    };

    /// <summary>FastOptions with a progress sweep as frequent as the chain check, for tests that assert on progress.</summary>
    public static BatchStrategyOptions ProgressOptions { get; } = FastOptions with { ProgressSweep = TimeSpan.FromMilliseconds(5) };

    public static BatchBudget Budget { get; } = new(15, 30, 10_000_000, 10_000_000);

    public BatchStrategy Strategy(
        BatchStrategyOptions? options = null, BatchBudget? budget = null, ITransactionSigner? writer = null,
        ITransactionSigner? payer = null, ILedgerSink? sink = null) =>
        new(sink ?? Sink, Ledger, writer ?? Writer, payer ?? Payer, budget ?? Budget, Fence, options ?? FastOptions);

    public static IReadOnlyList<MemoSubmission> Memos(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex([(byte)i, 7]), null))];

    /// <summary>The writer's successful records carrying each memo marker, in ledger order.</summary>
    public async Task<IReadOnlyDictionary<string, List<LedgerTransaction>>> LandedByMarkerAsync()
    {
        Dictionary<string, List<LedgerTransaction>> byMarker = new(StringComparer.OrdinalIgnoreCase);

        await foreach (LedgerTransaction tx in Ledger.AccountTransactionsAsync(Writer.Account, 0, Ledger.CurrentLedger, true, CancellationToken.None))
        {
            if (!tx.Succeeded || tx.MemoTypeHex is null) continue;
            if (!byMarker.TryGetValue(tx.MemoTypeHex, out List<LedgerTransaction>? list)) byMarker[tx.MemoTypeHex] = list = [];
            list.Add(tx);
        }

        return byMarker;
    }

    public static async Task EventuallyAsync(Func<bool> condition)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

        while (!condition())
        {
            if (DateTimeOffset.UtcNow > until) throw new TimeoutException("the condition did not become true in 20 s");
            await Task.Delay(5);
        }
    }
}
