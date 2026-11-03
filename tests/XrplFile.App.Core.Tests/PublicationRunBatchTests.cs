using Xrpl.Wallet;
using XrplFile.Keys;
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class PublicationRunBatchTests : VaultHarness
{
    private static readonly BatchStrategyOptions Fast = new()
    {
        ChainCheck = TimeSpan.FromMilliseconds(5),
        ProgressSweep = TimeSpan.FromMilliseconds(5),
        HistoryLagLimit = TimeSpan.FromSeconds(2),
        FeeWait = TimeSpan.FromMilliseconds(20),

        // The fake closes a ledger on every read, so its deadlines pass in milliseconds: a 400 ms
        // retry step would meet a resolved candidate instead of its resend.
        TransportRetryStep = TimeSpan.FromMilliseconds(1),
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-run-batch-").FullName;
    private readonly FakeConnectionFactory _connections = new();

    private RunPaths Paths => new(_directory);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(PublicationRun Run, PublishRequest Request, PublicationHistory History, KeyVault Vault)> ReadyAsync(int size = 20_000, IKeepAwake? keepAwake = null, Action<string>? log = null)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), Token);

        XrplWallet writer = XrplWallet.Generate();
        XrplWallet payer = XrplWallet.Generate();
        string writerRef = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "writer", "testnet", writer.ClassicAddress, writer.Seed), Token);
        string payerRef = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "payer", "testnet", payer.ClassicAddress, payer.Seed), Token);

        FakeBatchLedgerSink sink = _connections.EnableBatch();
        sink.SetSequence(writer.ClassicAddress, 100);
        sink.SetSequence(payer.ClassicAddress, 500);
        _connections.Ledger.Balances[writer.ClassicAddress] = 100_000_000;
        _connections.Ledger.Balances[payer.ClassicAddress] = 100_000_000;

        PublicationHistory history = new(Paths.HistoryPath);
        PublicationRun run = new(_connections, vault, Paths, history, batchOptions: Fast, keepAwake: keepAwake, log: log);

        // On disk because a resume opens the recorded path itself.
        string path = Path.Combine(_directory, "file.bin");
        byte[] bytes = VectorStream.Bytes("app-batch", size);
        await File.WriteAllBytesAsync(path, bytes, Token);
        PublishRequest request = Requests.Fresh(
            bytes, "application/octet-stream", "file.bin", writerRef, writer.ClassicAddress, "testnet",
            payerRef, payer.ClassicAddress, path);

        return (run, request, history, vault);
    }

    private static void AssertHeldAndClearedOnOneThread(RecordingExecutionState state)
    {
        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
        Assert.Equal(state.Calls[0].Thread, state.Calls[1].Thread);
    }

    [Fact]
    public async Task A_run_that_succeeds_holds_the_computer_awake_from_start_to_end_on_one_thread()
    {
        RecordingExecutionState state = new();
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(keepAwake: new ThreadKeepAwake(state));

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        AssertHeldAndClearedOnOneThread(state);
    }

    [Fact]
    public async Task A_run_that_fails_lets_the_computer_sleep_again()
    {
        RecordingExecutionState state = new();
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(keepAwake: new ThreadKeepAwake(state));
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "fake: no node");

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        AssertHeldAndClearedOnOneThread(state);
    }

    [Fact]
    public async Task A_cancelled_run_lets_the_computer_sleep_again()
    {
        RecordingExecutionState state = new();
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(keepAwake: new ThreadKeepAwake(state));
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) run.Cancel();
        };

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        AssertHeldAndClearedOnOneThread(state);
    }

    [Fact]
    public async Task A_keep_awake_that_throws_on_release_does_not_change_the_runs_outcome()
    {
        ThrowingReleaseKeepAwake keepAwake = new();
        List<string> log = [];
        (PublicationRun run, PublishRequest request, PublicationHistory history, _) = await ReadyAsync(
            keepAwake: keepAwake, log: line => { lock (log) log.Add(line); });

        Assert.True(run.Start(request));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.NotNull(run.Snapshot.Report);
        Assert.Single(history.Read());
        Assert.Equal(1, keepAwake.Acquired);
        Assert.Equal(1, keepAwake.Released);

        // Swallowed so the outcome stays the run's, but not hidden: what failed is on record.
        string line = Assert.Single(log);
        Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal);
        Assert.Contains("the platform refused to let the computer sleep again", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_batch_publication_succeeds_and_History_records_its_payer_from_the_state()
    {
        (PublicationRun run, PublishRequest request, PublicationHistory history, _) = await ReadyAsync();

        Assert.True(run.Start(request));
        await run.Completion;

        RunSnapshot snapshot = run.Snapshot;
        Assert.Equal(PublicationRunState.Succeeded, snapshot.State);
        Assert.True(snapshot.UsesBatch);
        Assert.NotNull(snapshot.StatePath);
        Assert.Equal(request.Size, snapshot.FileBytes);

        PublicationRecord record = Assert.Single(history.Read());
        Assert.Equal(request.PayerAccount, record.Payer);
        Assert.Equal(snapshot.Report!.Link.ToString(), record.Link);
        Assert.False(File.Exists(snapshot.StatePath), "the finished run's state file outlived its History record");
    }

    [Fact]
    public async Task A_run_whose_fee_unit_rose_past_the_approved_ceiling_is_refused_before_anything_is_sent()
    {
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync();
        BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(
            _connections.Ledger, request.Account, request.PayerAccount!, request.Size, new SessionOptions { StateDirectory = Paths.StateDirectory }, Token);
        PublishRequest approved = request with { Approval = SpendingApproval.For(estimate, request.Account, request.PayerAccount!) };

        // The Estimate screen approved a unit of 15 and a ceiling of 30; the node now asks 45.
        _connections.Ledger.OpenLedgerFee = new OpenLedgerFee(30, 10);

        Assert.True(run.Start(approved));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Contains("Estimate it again.", TestText.En(run.Snapshot.Failure), StringComparison.Ordinal);
        Assert.Empty(_connections.BatchSink!.Transmissions);
    }

    [Fact]
    public async Task A_run_whose_fee_unit_fell_below_the_approved_ceiling_starts_and_finishes()
    {
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync();
        SessionOptions options = new() { StateDirectory = Paths.StateDirectory };

        // Approved while the node asked more, then the fee fell: the approved ceiling is above the current one.
        _connections.Ledger.OpenLedgerFee = new OpenLedgerFee(30, 10);
        BatchPublicationEstimate high = await PublicationSession.EstimateAsync(
            _connections.Ledger, request.Account, request.PayerAccount!, request.Size, options, Token);
        PublishRequest approved = request with { Approval = SpendingApproval.For(high, request.Account, request.PayerAccount!) };

        _connections.Ledger.OpenLedgerFee = new OpenLedgerFee(12, 10);
        BatchPublicationEstimate low = await PublicationSession.EstimateAsync(
            _connections.Ledger, request.Account, request.PayerAccount!, request.Size, options, Token);
        Assert.True(low.FeeCeilingDrops < approved.Approval!.FeeUnitCeilingDrops);

        Assert.True(run.Start(approved));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.Null(run.Snapshot.Failure);
    }

    [Fact]
    public async Task A_batch_run_writes_its_round_trace_to_the_log_under_its_state_file_name()
    {
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync();

        Assert.True(run.Start(request));
        await run.Completion;

        string log = await File.ReadAllTextAsync(Paths.TraceLogPath, Token);
        Assert.Contains(Path.GetFileName(run.Snapshot.StatePath!), log, StringComparison.Ordinal);
        Assert.Contains("round:", log, StringComparison.Ordinal);
        Assert.Contains("transmit:0:tesSUCCESS", log, StringComparison.Ordinal);
        Assert.Contains("settled:", log, StringComparison.Ordinal);
        Assert.Contains("end Succeeded", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// The 2026-11-03 mainnet failure was logged as "end Failed NodeUnreachable" and nothing more:
    /// which read failed, and what the node said, was lost with the exception.
    /// </summary>
    [Fact]
    public async Task A_failed_batch_run_logs_what_it_failed_with()
    {
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(40_000);
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) throw new XrplFileException(XrplFileError.SubmitRefused, "fake: refused\r\nat the door");
        };

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Contains("end Failed SubmitRefused: fake: refused at the door ", await File.ReadAllTextAsync(Paths.TraceLogPath, Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_batch_run_logs_how_it_ended()
    {
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(40_000);
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) run.Cancel();
        };

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Contains("end Cancelled", await File.ReadAllTextAsync(Paths.TraceLogPath, Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_batch_run_resumes_from_its_state_with_its_payer()
    {
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(40_000);
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) run.Cancel();
        };

        Assert.True(run.Start(request));
        await run.Completion;
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        string statePath = run.Snapshot.StatePath!;
        _connections.BatchSink.Transmitting = null;

        Assert.True(run.Resume(statePath, request with { Source = null }));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.False(File.Exists(statePath));
    }

    [Fact]
    public async Task A_one_account_run_resumes_on_the_ticket_strategy_it_recorded_even_when_small()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), Token);
        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "w", "testnet", wallet.ClassicAddress, wallet.Seed), Token);
        _connections.Ledger.Balances[wallet.ClassicAddress] = 100_000_000;
        byte[] file = new byte[4096];
        string filePath = Path.Combine(_directory, "f.bin");
        await File.WriteAllBytesAsync(filePath, file, Token);

        Directory.CreateDirectory(Paths.StateDirectory);
        string statePath = Path.Combine(Paths.StateDirectory, "1-run.json");
        new PublicationState(
            PublicationState.CurrentVersion, "testnet", wallet.ClassicAddress, reference, "application/octet-stream", "f.bin",
            file.Length, MemoLayout.ChunkBytes, PublicationSession.RootOf(file), 1, "chunks",
            Strategy: PublicationState.TicketStrategyName).Save(statePath);

        PublicationRun run = new(_connections, vault, Paths, new PublicationHistory(Paths.HistoryPath));
        Assert.True(run.Resume(statePath, new PublishRequest(file.Length, "application/octet-stream", "f.bin", reference, wallet.ClassicAddress, "testnet", SourcePath: filePath)));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.Contains("mint", _connections.Sink.Events);
    }

    private static Dictionary<string, object> Transaction(VaultSigner signer) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = signer.Account,
        ["Fee"] = "12",
        ["Sequence"] = 7u,
        ["LastLedgerSequence"] = 100u,
        ["SigningPubKey"] = signer.PublicKey,
    };

    /// <summary>
    /// A cached wallet outlives a removed vault entry (see <see cref="VaultSigner"/>), so a signer whose
    /// cache was dropped is the one that then fails to find the entry it signs with.
    /// </summary>
    private static async Task AssertCachesDroppedAsync(KeyVault vault, PublishRequest request, List<VaultSigner> signers, int expected)
    {
        Assert.Equal(expected, signers.Count);

        await vault.DeleteAsync(request.KeyReference, Token);
        if (request.PayerReference is not null) await vault.DeleteAsync(request.PayerReference, Token);

        foreach (VaultSigner signer in signers)
        {
            VaultException refused = await Assert.ThrowsAsync<VaultException>(() => signer.SignAsync(Transaction(signer), Token));
            Assert.Equal(VaultError.NoSuchEntry, refused.Error);
        }
    }

    private static void Warm(VaultSigner signer) => signer.SignAsync(Transaction(signer), Token).GetAwaiter().GetResult();

    [Fact]
    public async Task The_cache_probe_tells_a_kept_wallet_from_a_dropped_one()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), Token);
        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "w", "testnet", wallet.ClassicAddress, wallet.Seed), Token);
        VaultSigner signer = await VaultSigner.CreateAsync(vault, reference, Token, cacheWallet: true);

        await signer.SignAsync(Transaction(signer), Token);
        await vault.DeleteAsync(reference, Token);
        await signer.SignAsync(Transaction(signer), Token);

        signer.ClearCache();

        VaultException refused = await Assert.ThrowsAsync<VaultException>(() => signer.SignAsync(Transaction(signer), Token));
        Assert.Equal(VaultError.NoSuchEntry, refused.Error);
    }

    [Fact]
    public async Task Both_signers_are_cleared_when_a_batch_run_succeeds()
    {
        (PublicationRun run, PublishRequest request, _, KeyVault vault) = await ReadyAsync();
        List<VaultSigner> signers = [];
        run.BatchSignerCreated = signers.Add;

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        await AssertCachesDroppedAsync(vault, request, signers, 2);
    }

    [Fact]
    public async Task Both_signers_are_cleared_when_a_batch_run_is_cancelled()
    {
        (PublicationRun run, PublishRequest request, _, KeyVault vault) = await ReadyAsync(40_000);
        List<VaultSigner> signers = [];
        run.BatchSignerCreated = signers.Add;
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) run.Cancel();
        };

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        await AssertCachesDroppedAsync(vault, request, signers, 2);
    }

    [Fact]
    public async Task Both_signers_are_cleared_when_a_batch_run_fails_after_signing()
    {
        (PublicationRun run, PublishRequest request, _, KeyVault vault) = await ReadyAsync(40_000);
        List<VaultSigner> signers = [];
        run.BatchSignerCreated = signers.Add;
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) throw new InvalidOperationException("the node broke");
        };

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.True(_connections.BatchSink.Transmissions.Count >= 1, "the run failed before it signed anything");
        await AssertCachesDroppedAsync(vault, request, signers, 2);
    }

    [Fact]
    public async Task Both_signers_are_cleared_when_a_start_is_refused()
    {
        (PublicationRun run, PublishRequest request, _, KeyVault vault) = await ReadyAsync();
        List<VaultSigner> signers = [];
        run.BatchSignerCreated = signer =>
        {
            signers.Add(signer);
            Warm(signer);
        };
        _connections.Ledger.Balances[request.PayerAccount!] = 0;

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Empty(_connections.BatchSink!.Transmissions);
        await AssertCachesDroppedAsync(vault, request, signers, 2);
    }

    [Fact]
    public async Task Both_signers_are_cleared_when_a_resume_is_refused()
    {
        (PublicationRun run, PublishRequest request, _, KeyVault vault) = await ReadyAsync(40_000);
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) run.Cancel();
        };
        Assert.True(run.Start(request));
        await run.Completion;
        string statePath = run.Snapshot.StatePath!;
        _connections.BatchSink.Transmitting = null;

        List<VaultSigner> signers = [];
        run.BatchSignerCreated = signer =>
        {
            signers.Add(signer);
            Warm(signer);
        };
        _connections.Ledger.Balances[request.PayerAccount!] = 0;

        Assert.True(run.Resume(statePath, request with { Source = null }));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        await AssertCachesDroppedAsync(vault, request, signers, 2);
    }

    [Fact]
    public async Task The_writer_signer_is_cleared_when_the_payer_signer_cannot_be_made()
    {
        (PublicationRun run, PublishRequest request, _, KeyVault vault) = await ReadyAsync();
        List<VaultSigner> signers = [];
        run.BatchSignerCreated = signer =>
        {
            signers.Add(signer);
            Warm(signer);
        };

        Assert.True(run.Start(request with { PayerReference = "no-such-entry" }));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        await AssertCachesDroppedAsync(vault, request, signers, 1);
    }
}
