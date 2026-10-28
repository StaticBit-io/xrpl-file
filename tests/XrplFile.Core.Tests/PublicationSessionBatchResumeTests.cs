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

        (long _, long firstRead) = node.Ledger.Passes(node.Writer.Account, first.State.StartedInLedger, callsBefore)[0];
        Assert.True(firstRead > fence + 2, $"read to {firstRead} with the fence at {fence}");
        Assert.Contains(waited, progress => progress.Stage == "fence");

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
    public async Task A_Batch_resume_of_a_file_of_another_size_is_a_changed_file_refused_before_the_ledger_is_read()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        byte[] other = VectorStream.Bytes("resume", file.Length - 1);
        int reads = node.Ledger.AccountTransactionsCalls;
        int transmitted = node.Sink.Transmissions.Count;

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, other, first.StatePath, node.Payer.Account, "key-p", Options, null, Token));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
        Assert.True(error.SourceChanged);
        Assert.Equal(reads, node.Ledger.AccountTransactionsCalls);
        Assert.Equal(transmitted, node.Sink.Transmissions.Count);
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

        Assert.DoesNotContain(waited, progress => progress.Stage == "fence");
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
    public async Task A_resume_waits_for_history_that_lags_the_ledger_and_pays_for_no_landed_chunk_again()
    {
        BatchTestNode node = Funded();
        List<BatchBudget> budgets = [];
        byte[] file = VectorStream.Bytes("resume", 40_000);
        BatchPublicationEstimate whole = await PublicationSession.EstimateAsync(node.Ledger, node.Writer.Account, node.Payer.Account, file.Length, Options, Token);
        (PublicationSession first, _) = await InterruptedAsync(node, afterTransmissions: 3);

        // The node's account_tx has not caught up with the ledgers the fence wait closed: everything
        // the writer did is missing from the first reads, though its sequence has long moved.
        node.Ledger.HistoryHides = tx => string.Equals(tx.Account, node.Writer.Account, StringComparison.Ordinal);
        node.Ledger.HistoryHideReads = 3;
        node.Ledger.HistoryHideReadsFrom = first.State.StartedInLedger;
        int callsBefore = node.Ledger.AccountTransactionsCalls;

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node, node.Payer, budgets), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);

        // Three lagging reads, each discarded, then one that covers the writer's sequences (or, when a
        // later range of the fourth reached the node before its first, one more).
        Assert.InRange(node.Ledger.Passes(node.Writer.Account, first.State.StartedInLedger, callsBefore).Count, 4, 5);

        Assert.True(Assert.Single(budgets).PayerBudgetDrops < whole.PayerFeeDropsWorstCase);

        PublicationReport report = await resumed.RunAsync(null, Token);
        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
        Assert.All((await node.LandedByMarkerAsync()).Where(pair => MemoLayout.ChunkIndexOf(pair.Key) is not null), pair => Assert.Single(pair.Value));
    }

    [Fact]
    public async Task A_resume_on_a_node_whose_history_never_catches_up_fails_as_lacking_history_and_stays_resumable()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node, afterTransmissions: 3);
        node.Ledger.HistoryHides = tx => string.Equals(tx.Account, node.Writer.Account, StringComparison.Ordinal);
        node.Ledger.HistoryHideReads = int.MaxValue;
        SessionOptions lagging = new()
        {
            StateDirectory = _stateDirectory,
            FencePoll = TimeSpan.FromMilliseconds(1),
            HistoryLagLimit = TimeSpan.FromMilliseconds(200),
        };
        byte[] before = await File.ReadAllBytesAsync(first.StatePath, Token);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", lagging, null, Token));

        Assert.Equal(XrplFileError.NodeLacksHistory, error.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(first.StatePath, Token));

        node.Ledger.HistoryHideReads = 0;
        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);
        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
        Assert.All((await node.LandedByMarkerAsync()).Where(pair => MemoLayout.ChunkIndexOf(pair.Key) is not null), pair => Assert.Single(pair.Value));
    }

    [Fact]
    public async Task A_manifest_the_lagging_history_does_not_show_yet_is_still_found_and_not_sent_twice()
    {
        BatchTestNode node = Funded();
        byte[] file = VectorStream.Bytes("resume", 9_000);
        PublicationSession first = await PublicationSession.StartAsync(
            Factory(node, node.Payer), node.Ledger, file, "text/plain", null,
            node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options, Token);
        PublicationReport original = await first.RunAsync(null, Token);

        // A crash between the manifest validating and "done" being written.
        PublicationState state = PublicationState.Load(first.StatePath);
        (state with { Phase = "manifest", ManifestHash = null }).Save(first.StatePath);
        int transmitted = node.Sink.Transmissions.Count;

        node.Ledger.HistoryHides = tx => MemoLayout.IsManifest(tx.MemoTypeHex);
        node.Ledger.HistoryHideReads = 2;
        node.Ledger.HistoryHideReadsFrom = first.State.StartedInLedger;
        int callsBefore = node.Ledger.AccountTransactionsCalls;

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        // The reconciliation and the first manifest search both read before the manifest showed.
        Assert.True(node.Ledger.Passes(node.Writer.Account, first.State.StartedInLedger, callsBefore).Count >= 3);

        Assert.Equal(original.Link, report.Link);
        Assert.Equal(transmitted, node.Sink.Transmissions.Count);
        Assert.Equal(original.ManifestHash, PublicationState.Load(first.StatePath).ManifestHash);
    }

    [Fact]
    public async Task A_manifest_search_that_reads_a_short_history_reads_again_and_sends_no_second_manifest()
    {
        BatchTestNode node = Funded();
        byte[] file = VectorStream.Bytes("resume", 9_000);
        PublicationSession first = await PublicationSession.StartAsync(
            Factory(node, node.Payer), node.Ledger, file, "text/plain", null,
            node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options, Token);
        PublicationReport original = await first.RunAsync(null, Token);

        PublicationState state = PublicationState.Load(first.StatePath);
        (state with { Phase = "manifest", ManifestHash = null }).Save(first.StatePath);
        int transmitted = node.Sink.Transmissions.Count;

        // The reconciliation reads the whole history; the manifest search's first two reads are
        // answered by an index that has not caught up with the writer's last ledgers.
        int baseline = node.Ledger.AccountTransactionsCalls;
        node.Ledger.HistoryHides = tx => string.Equals(tx.Account, node.Writer.Account, StringComparison.Ordinal)
            && node.Ledger.AccountTransactionsCalls is var calls && (calls == baseline + 2 || calls == baseline + 3);
        node.Ledger.HistoryHideReads = int.MaxValue;

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.True(node.Ledger.AccountTransactionsCalls >= baseline + 4, "the short passes were not read again");
        Assert.Equal(original.Link, report.Link);
        Assert.Equal(transmitted, node.Sink.Transmissions.Count);
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
