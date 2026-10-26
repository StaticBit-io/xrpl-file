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
}
