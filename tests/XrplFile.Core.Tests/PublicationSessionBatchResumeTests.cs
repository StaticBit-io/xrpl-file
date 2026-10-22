using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class PublicationSessionBatchResumeTests : IDisposable
{
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-batch-resume-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _stateDirectory, FencePoll = TimeSpan.FromMilliseconds(1) };

    private static BatchTestNode Funded()
    {
        BatchTestNode node = new();
        node.Ledger.Fees = new NetworkFees(10, 1_000_000, 200_000);
        node.Ledger.Balances[node.Writer.Account] = 100_000_000;
        node.Ledger.Balances[node.Payer.Account] = 100_000_000;
        return node;
    }

    private static BatchStrategyFactory Factory(BatchTestNode node, ITransactionSigner payer, List<BatchBudget>? budgets = null) =>
        (budget, fence) =>
        {
            budgets?.Add(budget);
            return new BatchStrategy(node.Sink, node.Ledger, node.Writer, payer, budget, fence, BatchTestNode.FastOptions);
        };

    /// <summary>A Batch run stopped after a few transmissions, as closing the window stops it.</summary>
    private async Task<(PublicationSession Session, byte[] File)> InterruptedAsync(BatchTestNode node, int afterTransmissions = 2)
    {
        byte[] file = VectorStream.Bytes("resume", 40_000);
        PublicationSession session = await PublicationSession.StartAsync(
            Factory(node, node.Payer), node.Ledger, file, "text/plain", null,
            node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options, Token);

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        node.Sink.Transmitting = transmission =>
        {
            if (transmission.Ordinal + 1 >= afterTransmissions) stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(null, stop.Token));
        node.Sink.Transmitting = null;

        return (session, file);
    }

    [Fact]
    public async Task A_resume_reads_nothing_until_a_snapshot_is_past_the_fence_and_two_ledgers()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        long fence = PublicationState.Load(first.StatePath).Fence!.Value;
        Assert.True(fence > 0);
        int callsBefore = node.Ledger.AccountTransactionsCalls;
        List<PublishProgress> waited = [];

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options,
            new SynchronousProgress<PublishProgress>(waited.Add), Token);

        AccountTransactionsCall firstRead = node.Ledger.Calls[callsBefore];
        Assert.True(firstRead.ToLedger > fence + 2, $"read at {firstRead.ToLedger} with the fence at {fence}");
        Assert.NotEmpty(waited);
        Assert.All(waited, progress => Assert.Equal("fence", progress.Stage));

        PublicationReport report = await resumed.RunAsync(null, Token);
        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
        Assert.All((await node.LandedByMarkerAsync()).Where(pair => MemoLayout.ChunkIndexOf(pair.Key) is not null), pair => Assert.Single(pair.Value));
    }

    [Fact]
    public async Task A_new_payer_is_written_to_the_state_only_after_the_fence_and_pays_for_everything_after()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        ITransactionSigner newPayer = new WalletSigner(XrplWallet.Generate());
        node.Ledger.Balances[newPayer.Account] = 100_000_000;
        node.Sink.SetSequence(newPayer.Account, 900);
        string oldPayer = node.Payer.Account;
        List<string?> payerDuringFence = [];
        int transmittedBefore = node.Sink.Transmissions.Count;

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node, newPayer), node.Ledger, file, first.StatePath, newPayer.Account, "key-new", Options,
            new SynchronousProgress<PublishProgress>(_ => payerDuringFence.Add(PublicationState.Load(first.StatePath).Payer)), Token);

        Assert.All(payerDuringFence, payer => Assert.Equal(oldPayer, payer));
        PublicationState changed = PublicationState.Load(first.StatePath);
        Assert.Equal(newPayer.Account, changed.Payer);
        Assert.Equal("key-new", changed.PayerKeyReference);

        await resumed.RunAsync(null, Token);

        Assert.All(node.Sink.Transmissions.Skip(transmittedBefore).Where(t => t.TransactionType == "Batch"), t => Assert.Equal(newPayer.Account, t.Account));
        Assert.All((await node.LandedByMarkerAsync()).Where(pair => MemoLayout.ChunkIndexOf(pair.Key) is not null), pair => Assert.Single(pair.Value));
    }

    [Fact]
    public async Task A_resume_prices_only_what_is_still_missing()
    {
        BatchTestNode node = Funded();
        List<BatchBudget> budgets = [];
        byte[] file = VectorStream.Bytes("resume", 40_000);
        BatchPublicationEstimate whole = await PublicationSession.EstimateAsync(node.Ledger, node.Writer.Account, node.Payer.Account, file.Length, Options, Token);
        (PublicationSession first, _) = await InterruptedAsync(node, afterTransmissions: 3);

        await PublicationSession.ResumeAsync(
            Factory(node, node.Payer, budgets), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);

        Assert.True(Assert.Single(budgets).PayerBudgetDrops < whole.PayerFeeDropsWorstCase);
    }

    [Fact]
    public async Task A_run_that_never_transmitted_has_no_fence_to_wait_for()
    {
        BatchTestNode node = Funded();
        byte[] file = VectorStream.Bytes("resume", 9_000);
        PublicationSession first = await PublicationSession.StartAsync(
            Factory(node, node.Payer), node.Ledger, file, "text/plain", null,
            node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options, Token);
        List<PublishProgress> waited = [];

        await PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options,
            new SynchronousProgress<PublishProgress>(waited.Add), Token);

        Assert.Empty(waited);
    }

    [Fact]
    public async Task A_fence_wait_on_a_node_that_stopped_fails_instead_of_waiting_forever()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        node.Sink.AutoClose = false;
        SessionOptions stuck = new()
        {
            StateDirectory = _stateDirectory,
            FencePoll = TimeSpan.FromMilliseconds(1),
            NodeOutageLimit = TimeSpan.FromMilliseconds(200),
        };

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", stuck, null, Token));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }

    [Fact]
    public async Task Closing_during_the_fence_wait_leaves_the_state_as_it_was_and_the_run_resumable()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        byte[] before = await File.ReadAllBytesAsync(first.StatePath, Token);
        using CancellationTokenSource close = CancellationTokenSource.CreateLinkedTokenSource(Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options,
            new SynchronousProgress<PublishProgress>(_ => close.Cancel()), close.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(first.StatePath, Token));

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);
        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
    }

    [Fact]
    public async Task A_one_account_run_is_not_resumed_as_a_Batch_run()
    {
        BatchTestNode node = Funded();
        byte[] file = VectorStream.Bytes("v2", 2500);
        await using SequenceStrategy strategy = new(new FakeLedgerSink(node.Ledger), node.Writer, new SequenceStrategyOptions { FeeDrops = 10 });
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", "testnet", Options, Token);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
    }
}
