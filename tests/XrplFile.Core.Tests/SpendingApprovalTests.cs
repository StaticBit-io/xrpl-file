using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public sealed class SpendingApprovalTests : IDisposable
{
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-approval-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options(SpendingApproval? approval = null, ResumeApproval? resume = null) =>
        new() { StateDirectory = _stateDirectory, FencePoll = TimeSpan.FromMilliseconds(1), TicketsPerBatch = 100, Approval = approval, ApproveResume = resume };

    private static BatchTestNode Funded()
    {
        BatchTestNode node = new();
        node.Ledger.Fees = new NetworkFees(10, 1_000_000, 200_000);
        node.Ledger.Balances[node.Writer.Account] = 100_000_000;
        node.Ledger.Balances[node.Payer.Account] = 100_000_000;
        return node;
    }

    private static BatchStrategyFactory Factory(BatchTestNode node, List<BatchBudget> budgets) =>
        (budget, fence) =>
        {
            budgets.Add(budget);
            return new BatchStrategy(node.Sink, node.Ledger, node.Writer, node.Payer, budget, fence, BatchTestNode.FastOptions);
        };

    [Fact]
    public async Task A_fresh_Batch_run_spends_on_the_approvals_ceilings()
    {
        BatchTestNode node = Funded();
        List<BatchBudget> budgets = [];
        byte[] file = VectorStream.Bytes("approval", 20_000);
        BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(node.Ledger, node.Writer.Account, node.Payer.Account, file.Length, Options(), Token);
        SpendingApproval approval = SpendingApproval.For(estimate, node.Writer.Account, node.Payer.Account) with { PayerCeilingDrops = estimate.PayerFeeDropsWorstCase + 7 };

        await PublicationSession.StartAsync(
            Factory(node, budgets), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet",
            Options(approval), Token);

        Assert.Equal(new BatchBudget(estimate.FeeUnitDrops, approval.FeeUnitCeilingDrops, approval.PayerCeilingDrops, approval.WriterCeilingDrops), Assert.Single(budgets));
    }

    [Fact]
    public async Task A_fee_unit_that_rises_under_the_approved_ceiling_still_starts()
    {
        BatchTestNode node = Funded();
        List<BatchBudget> budgets = [];
        byte[] file = VectorStream.Bytes("approval", 20_000);
        BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(node.Ledger, node.Writer.Account, node.Payer.Account, file.Length, Options(), Token);
        SpendingApproval approval = SpendingApproval.For(estimate, node.Writer.Account, node.Payer.Account);
        Assert.Equal((15L, 30L), (estimate.FeeUnitDrops, approval.FeeUnitCeilingDrops));

        // The open-ledger fee moves between the Estimate screen and the run, as testnet's does.
        node.Ledger.OpenLedgerFee = new OpenLedgerFee(12, 10);

        await PublicationSession.StartAsync(
            Factory(node, budgets), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet",
            Options(approval), Token);

        BatchBudget budget = Assert.Single(budgets);
        Assert.Equal((18L, 30L), (budget.FeeUnitDrops, budget.FeeCeilingDrops));
    }

    [Fact]
    public async Task A_fresh_Batch_run_whose_estimate_exceeds_its_approval_stops_before_anything_is_written()
    {
        BatchTestNode node = Funded();
        List<BatchBudget> budgets = [];
        byte[] file = VectorStream.Bytes("approval", 20_000);
        BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(node.Ledger, node.Writer.Account, node.Payer.Account, file.Length, Options(), Token);
        SpendingApproval stale = SpendingApproval.For(estimate, node.Writer.Account, node.Payer.Account);
        node.Ledger.OpenLedgerFee = new OpenLedgerFee(30, 10);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            Factory(node, budgets), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet",
            Options(stale), Token));

        Assert.Equal(XrplFileError.ApprovalExceeded, error.Error);
        Assert.Empty(budgets);
        Assert.Empty(Directory.GetFiles(_stateDirectory));
    }

    [Fact]
    public async Task A_run_larger_than_the_one_approved_is_refused_while_the_unit_is_still_under_the_ceiling()
    {
        BatchTestNode node = Funded();
        List<BatchBudget> budgets = [];
        byte[] approvedFile = VectorStream.Bytes("approval", 20_000);
        byte[] larger = VectorStream.Bytes("approval", 60_000);
        BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(node.Ledger, node.Writer.Account, node.Payer.Account, approvedFile.Length, Options(), Token);
        SpendingApproval approval = SpendingApproval.For(estimate, node.Writer.Account, node.Payer.Account);

        // Only the worst-case comparison at the approved ceiling can catch this: the unit has not moved.
        Assert.True(estimate.FeeUnitDrops <= approval.FeeUnitCeilingDrops);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            Factory(node, budgets), node.Ledger, larger, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet",
            Options(approval), Token));

        Assert.Equal(XrplFileError.ApprovalExceeded, error.Error);
        Assert.Empty(budgets);
        Assert.Empty(node.Sink.Transmissions);
        Assert.Empty(Directory.GetFiles(_stateDirectory));
    }

    [Fact]
    public async Task A_one_account_run_priced_above_its_approval_is_refused()
    {
        XrplWallet wallet = XrplWallet.Generate();
        FakeLedgerSource ledger = new() { Fees = new NetworkFees(10, 1_000_000, 200_000) };
        ledger.Balances[wallet.ClassicAddress] = 500_000_000;
        byte[] file = VectorStream.Bytes("approval", 2_500);
        PublicationEstimate estimate = await PublicationSession.EstimateAsync(ledger, wallet.ClassicAddress, file.Length, Options(), Token);
        SpendingApproval approval = SpendingApproval.For(estimate, wallet.ClassicAddress);
        ledger.Fees = new NetworkFees(12, 1_000_000, 200_000);
        await using SequenceStrategy strategy = new(new FakeLedgerSink(ledger), new WalletSigner(wallet), new SequenceStrategyOptions { FeeDrops = 12 });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, wallet.ClassicAddress, "key", "testnet", Options(approval), Token));

        Assert.Equal(XrplFileError.ApprovalExceeded, error.Error);
    }

    [Fact]
    public async Task An_approval_for_another_writer_is_a_callers_mistake()
    {
        BatchTestNode node = Funded();
        byte[] file = VectorStream.Bytes("approval", 20_000);
        BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(node.Ledger, node.Writer.Account, node.Payer.Account, file.Length, Options(), Token);
        SpendingApproval approval = SpendingApproval.For(estimate, "rSomebodyElse", node.Payer.Account);

        await Assert.ThrowsAsync<ArgumentException>(() => PublicationSession.StartAsync(
            Factory(node, []), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet",
            Options(approval), Token));
    }

    private async Task<(PublicationSession Session, byte[] File)> InterruptedAsync(BatchTestNode node)
    {
        byte[] file = VectorStream.Bytes("approval-resume", 40_000);
        PublicationSession session = await PublicationSession.StartAsync(
            Factory(node, []), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options(), Token);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        node.Sink.Transmitting = transmission =>
        {
            if (transmission.Ordinal >= 1) stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(null, stop.Token));
        node.Sink.Transmitting = null;
        return (session, file);
    }

    [Fact]
    public async Task A_resume_asks_for_its_approval_after_reconciliation_with_a_quote_of_the_remaining_work()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        List<ResumeQuote> quotes = [];
        List<BatchBudget> budgets = [];

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node, budgets), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p",
            Options(resume: (quote, _) =>
            {
                quotes.Add(quote);
                return Task.FromResult(SpendingApproval.For(quote.Batch!, quote.Writer, quote.Payer!));
            }), null, Token);

        ResumeQuote asked = Assert.Single(quotes);
        Assert.Equal(node.Writer.Account, asked.Writer);
        Assert.Equal(node.Payer.Account, asked.Payer);
        Assert.Equal(asked.MissingChunks, asked.Batch!.Chunks);
        Assert.Equal(asked.Batch.PayerFeeDropsWorstCase, Assert.Single(budgets).PayerBudgetDrops);

        PublicationReport report = await resumed.RunAsync(null, Token);
        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
    }

    [Fact]
    public async Task A_resume_whose_approval_is_declined_sends_nothing_and_leaves_the_state_as_it_was()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        byte[] before = await File.ReadAllBytesAsync(first.StatePath, Token);
        int sent = node.Sink.Transmissions.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PublicationSession.ResumeAsync(
            Factory(node, []), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p",
            Options(resume: (_, _) => throw new OperationCanceledException("declined")), null, Token));

        Assert.Equal(sent, node.Sink.Transmissions.Count);
        Assert.Equal(before, await File.ReadAllBytesAsync(first.StatePath, Token));
    }

    [Theory]
    [InlineData("payer")]
    [InlineData("writer")]
    [InlineData("unit")]
    public async Task A_Batch_resume_approved_below_its_quote_is_refused_before_the_strategy_is_built_or_anything_is_sent(string shortCeiling)
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        List<BatchBudget> budgets = [];
        int sent = node.Sink.Transmissions.Count;

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node, budgets), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p",
            Options(resume: (quote, _) =>
            {
                SpendingApproval quoted = SpendingApproval.For(quote.Batch!, quote.Writer, quote.Payer!);
                return Task.FromResult(shortCeiling switch
                {
                    "payer" => quoted with { PayerCeilingDrops = quoted.PayerCeilingDrops - 1 },
                    "writer" => quoted with { WriterCeilingDrops = quoted.WriterCeilingDrops - 1 },
                    _ => quoted with { FeeUnitCeilingDrops = quote.Batch!.FeeUnitDrops - 1 },
                });
            }), null, Token));

        Assert.Equal(XrplFileError.ApprovalExceeded, error.Error);
        Assert.Empty(budgets);
        Assert.Equal(sent, node.Sink.Transmissions.Count);
    }

    private sealed record InterruptedOneAccount(XrplWallet Wallet, FakeLedgerSource Ledger, FakeLedgerSink Sink, byte[] File, string StatePath);

    private const int OneAccountFeeDrops = 10;
    private const int OneAccountChunks = 5;

    /// <summary>A five-chunk one-account run that landed three chunks and was refused the fourth, so a resume has two left to send.</summary>
    private async Task<InterruptedOneAccount> InterruptedOneAccountAsync()
    {
        XrplWallet wallet = XrplWallet.Generate();
        FakeLedgerSource ledger = new() { Fees = new NetworkFees(OneAccountFeeDrops, 1_000_000, 200_000) };
        ledger.Balances[wallet.ClassicAddress] = 500_000_000;
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("approval-one-account", OneAccountChunks * MemoLayout.ChunkBytes);

        sink.Refusals[3] = "tefBAD_AUTH";
        await using SequenceStrategy dying = new(sink, new WalletSigner(wallet), new SequenceStrategyOptions { FeeDrops = OneAccountFeeDrops });
        PublicationSession first = await PublicationSession.StartAsync(
            dying, ledger, file, "text/plain", null, wallet.ClassicAddress, "key", "testnet", Options(), Token);

        await Assert.ThrowsAsync<XrplFileException>(() => first.RunAsync(null, Token));
        sink.Refusals.Clear();

        return new InterruptedOneAccount(wallet, ledger, sink, file, first.StatePath);
    }

    private static SequenceStrategy ResumingStrategy(InterruptedOneAccount run) =>
        new(run.Sink, new WalletSigner(run.Wallet), new SequenceStrategyOptions { FeeDrops = OneAccountFeeDrops });

    [Fact]
    public async Task A_one_account_resume_asks_after_reconciliation_with_the_whole_file_estimate_and_what_is_missing()
    {
        InterruptedOneAccount run = await InterruptedOneAccountAsync();
        PublicationEstimate whole = await PublicationSession.EstimateAsync(run.Ledger, run.Wallet.ClassicAddress, run.File.Length, Options(), Token);
        List<ResumeQuote> quotes = [];
        int sent = run.Sink.Submitted.Count;

        await using SequenceStrategy resuming = ResumingStrategy(run);
        PublicationSession resumed = await PublicationSession.ResumeAsync(
            resuming, run.Ledger, run.File, run.StatePath,
            Options(resume: (quote, _) =>
            {
                // Asked before anything is sent: reconciliation has already read the ledger.
                Assert.Equal(sent, run.Sink.Submitted.Count);
                quotes.Add(quote);
                return Task.FromResult(SpendingApproval.For(quote.OneAccount!, quote.Writer));
            }), Token);

        ResumeQuote asked = Assert.Single(quotes);
        Assert.Equal(run.Wallet.ClassicAddress, asked.Writer);
        Assert.Null(asked.Payer);
        Assert.Null(asked.Batch);

        // Spec question 6: the figures are the whole file's; the missing count is what is left to send.
        Assert.Equal(OneAccountChunks, asked.OneAccount!.Chunks);
        Assert.Equal(whole.Chunks, asked.OneAccount.Chunks);
        Assert.Equal(whole.FeeDropsWorstCase, asked.OneAccount.FeeDropsWorstCase);
        Assert.Equal(2, asked.MissingChunks);
        Assert.True(asked.MissingChunks < asked.OneAccount.Chunks);

        PublicationReport report = await resumed.RunAsync(null, Token);
        Assert.Equal(run.File, (await new XrplFileReader(run.Ledger).ReadAsync(report.Link, Token)).Bytes);
    }

    [Fact]
    public async Task A_declined_one_account_resume_sends_nothing_and_leaves_the_state_as_it_was()
    {
        InterruptedOneAccount run = await InterruptedOneAccountAsync();
        byte[] before = await File.ReadAllBytesAsync(run.StatePath, Token);
        int sent = run.Sink.Submitted.Count;

        await using SequenceStrategy resuming = ResumingStrategy(run);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PublicationSession.ResumeAsync(
            resuming, run.Ledger, run.File, run.StatePath,
            Options(resume: (_, _) => throw new OperationCanceledException("declined")), Token));

        Assert.Equal(sent, run.Sink.Submitted.Count);
        Assert.Equal(before, await File.ReadAllBytesAsync(run.StatePath, Token));
    }

    [Theory]
    [InlineData("writer")]
    [InlineData("unit")]
    public async Task A_one_account_resume_approved_below_its_quote_is_refused_before_it_returns_or_sends(string shortCeiling)
    {
        InterruptedOneAccount run = await InterruptedOneAccountAsync();
        int sent = run.Sink.Submitted.Count;

        await using SequenceStrategy resuming = ResumingStrategy(run);
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            resuming, run.Ledger, run.File, run.StatePath,
            Options(resume: (quote, _) =>
            {
                SpendingApproval quoted = SpendingApproval.For(quote.OneAccount!, quote.Writer);
                return Task.FromResult(shortCeiling == "writer"
                    ? quoted with { WriterCeilingDrops = quoted.WriterCeilingDrops - 1 }
                    : quoted with { FeeUnitCeilingDrops = quote.OneAccount!.FeeDropsPerTransaction - 1 });
            }), Token));

        Assert.Equal(XrplFileError.ApprovalExceeded, error.Error);
        Assert.Equal(sent, run.Sink.Submitted.Count);
    }
}
