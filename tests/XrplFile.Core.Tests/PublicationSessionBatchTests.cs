using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class PublicationSessionBatchTests : IDisposable
{
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-batch-session-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options(long? budget = null) =>
        new() { StateDirectory = _stateDirectory, BudgetDrops = budget, SourcePath = @"C:\files\s.txt", FencePoll = TimeSpan.FromMilliseconds(1) };

    private static BatchTestNode Funded(long writer = 100_000_000, long payer = 100_000_000)
    {
        BatchTestNode node = new();
        node.Ledger.Fees = new NetworkFees(10, 1_000_000, 200_000);
        node.Ledger.Balances[node.Writer.Account] = writer;
        node.Ledger.Balances[node.Payer.Account] = payer;
        return node;
    }

    private static BatchStrategyFactory Factory(BatchTestNode node, BatchStrategyOptions? options = null) =>
        (budget, fence) => new BatchStrategy(node.Sink, node.Ledger, node.Writer, node.Payer, budget, fence, options ?? BatchTestNode.FastOptions);

    private Task<PublicationSession> StartAsync(BatchTestNode node, byte[] file, SessionOptions? options = null) =>
        PublicationSession.StartAsync(
            Factory(node), node.Ledger, file, "text/plain", "s.txt",
            node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", options ?? Options(), Token);

    [Fact]
    public async Task A_network_without_BatchV1_1_is_refused_first()
    {
        BatchTestNode node = Funded(writer: 0, payer: 0);
        node.Ledger.EnabledAmendments.Clear();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => StartAsync(node, VectorStream.Bytes("b", 9000)));

        Assert.Equal(XrplFileError.BatchNotEnabled, error.Error);
    }

    [Fact]
    public async Task An_open_ledger_fee_above_200_drops_is_refused_at_estimate_time()
    {
        BatchTestNode node = Funded(writer: 0, payer: 0);
        node.Ledger.OpenLedgerFee = new OpenLedgerFee(201, 10);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.EstimateAsync(
            node.Ledger, node.Writer.Account, node.Payer.Account, 9000, Options(), Token));

        Assert.Equal(XrplFileError.FeeTooHigh, error.Error);
    }

    [Theory]
    [InlineData(0L, 100_000_000L, 0L, "InsufficientBalance")]
    [InlineData(100_000_000L, 0L, 0L, "PayerInsufficientBalance")]
    [InlineData(100_000_000L, 100_000_000L, 1L, "BudgetExceeded")]
    public async Task Balances_then_the_budget_are_refused_in_the_specs_order_before_any_state_exists(
        long writer, long payer, long budget, string expected)
    {
        BatchTestNode node = Funded(writer, payer);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => StartAsync(node, VectorStream.Bytes("b", 9000), Options(budget == 0 ? null : budget)));

        Assert.Equal(Enum.Parse<XrplFileError>(expected), error.Error);
        Assert.Empty(Directory.EnumerateFiles(_stateDirectory));
    }

    [Fact]
    public async Task A_batch_run_writes_a_version_3_state_with_its_payer_source_and_a_zero_fence()
    {
        BatchTestNode node = Funded();

        PublicationSession session = await StartAsync(node, VectorStream.Bytes("b", 9000));

        PublicationState state = PublicationState.Load(session.StatePath);
        Assert.Equal(PublicationState.BatchVersion, state.Version);
        Assert.Equal(PublicationState.BatchStrategyName, state.Strategy);
        Assert.Equal(node.Payer.Account, state.Payer);
        Assert.Equal("key-p", state.PayerKeyReference);
        Assert.Equal(0, state.Fence);
        Assert.Equal(@"C:\files\s.txt", state.SourcePath);
    }

    [Fact]
    public async Task A_batch_run_publishes_a_file_that_reads_back_and_reports_what_the_payer_paid()
    {
        BatchTestNode node = Funded();
        byte[] file = VectorStream.Bytes("batch", 20_000);
        PublicationSession session = await StartAsync(node, file);

        PublicationReport report = await session.RunAsync(null, Token);

        ReadResult read = await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token);
        Assert.Equal(file, read.Bytes);
        Assert.Equal(node.Writer.Account, read.Writer);

        long paid = 0;
        await foreach (LedgerTransaction tx in node.Ledger.AccountTransactionsAsync(node.Payer.Account, 0, node.Ledger.CurrentLedger, true, Token))
        {
            paid += tx.Fee!.Value;
        }

        Assert.Equal(node.Payer.Account, report.Payer);
        Assert.Equal(paid, report.PayerFeeDrops);

        PublicationState done = PublicationState.Load(session.StatePath);
        Assert.Equal("done", done.Phase);
        Assert.Equal(report.ManifestHash, done.ManifestHash);
    }

    [Fact]
    public async Task The_state_file_holds_a_fence_past_every_deadline_before_that_deadline_is_sent()
    {
        BatchTestNode node = Funded();
        PublicationSession session = await StartAsync(node, VectorStream.Bytes("fence", 20_000));
        List<(long Fence, uint LastLedger)> seen = [];
        node.Sink.Transmitting = transmission =>
        {
            // The session's own view, set only after a save succeeded: opening the file here could make
            // the next fence save's move fail on Windows, and with it the run (spec B23).
            lock (seen) seen.Add((session.State.Fence ?? 0, transmission.LastLedger));
        };

        await session.RunAsync(null, Token);

        Assert.NotEmpty(seen);
        Assert.All(seen, pair => Assert.True(pair.Fence >= pair.LastLedger, $"fence {pair.Fence} behind {pair.LastLedger}"));
    }

    [Fact]
    public async Task A_fence_that_cannot_be_saved_fails_the_run_before_anything_is_sent()
    {
        BatchTestNode node = Funded();
        PublicationSession session = await StartAsync(node, VectorStream.Bytes("blocked", 9000));
        Directory.CreateDirectory(session.StatePath + ".writing");

        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => session.RunAsync(null, Token));

        Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        Assert.Empty(node.Sink.Transmissions);
    }

    [Fact]
    public async Task A_factory_that_builds_a_strategy_for_another_payer_is_refused()
    {
        BatchTestNode node = Funded();
        ITransactionSigner stranger = new WalletSigner(XrplWallet.Generate());

        await Assert.ThrowsAsync<ArgumentException>(() => PublicationSession.StartAsync(
            (budget, fence) => new BatchStrategy(node.Sink, node.Ledger, node.Writer, stranger, budget, fence),
            node.Ledger, VectorStream.Bytes("b", 9000), "text/plain", null,
            node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options(), Token));
    }

    [Fact]
    public async Task A_BatchStrategy_is_refused_by_the_one_account_start()
    {
        BatchTestNode node = Funded();
        BatchStrategy strategy = node.Strategy();

        await Assert.ThrowsAsync<ArgumentException>(() => PublicationSession.StartAsync(
            strategy, node.Ledger, VectorStream.Bytes("b", 9000), "text/plain", null,
            node.Writer.Account, "key-w", "testnet", Options(), Token));

        Assert.Empty(Directory.EnumerateFiles(_stateDirectory));
    }

    [Fact]
    public async Task A_BatchStrategy_is_refused_by_the_one_account_resume()
    {
        BatchTestNode node = Funded();
        PublicationSession session = await StartAsync(node, VectorStream.Bytes("b", 9000));

        await Assert.ThrowsAsync<ArgumentException>(() => PublicationSession.ResumeAsync(
            node.Strategy(), node.Ledger, VectorStream.Bytes("b", 9000), session.StatePath, Options(), Token));
    }

    [Fact]
    public async Task The_fence_recorder_never_lowers_a_stored_fence()
    {
        BatchTestNode node = Funded();
        PublicationSession session = await StartAsync(node, VectorStream.Bytes("b", 9000));
        IBatchFenceRecorder recorder = session;

        await recorder.RecordFenceAsync(100, Token);
        await recorder.RecordFenceAsync(50, Token);

        Assert.Equal(100, session.State.Fence);
        Assert.Equal(100, PublicationState.Load(session.StatePath).Fence);
    }

    [Fact]
    public async Task A_one_chunk_file_costs_the_payer_nothing_and_the_writer_two_solos()
    {
        BatchTestNode node = Funded();
        PublicationSession session = await StartAsync(node, VectorStream.Bytes("one", 500));

        PublicationReport report = await session.RunAsync(null, Token);

        Assert.Equal(0L, report.PayerFeeDrops);
        Assert.DoesNotContain(node.Sink.Transmissions, transmission => transmission.TransactionType == "Batch");
        Assert.Equal(2, node.Sink.Transmissions.Count(t => t.TransactionType == "AccountSet" && t.Account == node.Writer.Account));
        Assert.Equal(2 * 15L, report.FeeDropsEstimated);
    }
}
