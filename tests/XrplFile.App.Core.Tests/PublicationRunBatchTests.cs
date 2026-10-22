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

    private async Task<(PublicationRun Run, PublishRequest Request, PublicationHistory History, KeyVault Vault)> ReadyAsync(int size = 20_000)
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
        PublicationRun run = new(_connections, vault, Paths, history, batchOptions: Fast);
        PublishRequest request = new(
            VectorStream.Bytes("app-batch", size), "application/octet-stream", "file.bin", writerRef, writer.ClassicAddress, "testnet",
            payerRef, payer.ClassicAddress, Path.Combine(_directory, "file.bin"));

        return (run, request, history, vault);
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
        Assert.Equal(request.File.Length, snapshot.FileBytes);

        PublicationRecord record = Assert.Single(history.Read());
        Assert.Equal(request.PayerAccount, record.Payer);
        Assert.Equal(snapshot.Report!.Link.ToString(), record.Link);
        Assert.False(File.Exists(snapshot.StatePath), "the finished run's state file outlived its History record");
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

        Assert.True(run.Resume(statePath, request));
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

        Directory.CreateDirectory(Paths.StateDirectory);
        string statePath = Path.Combine(Paths.StateDirectory, "1-run.json");
        new PublicationState(
            PublicationState.CurrentVersion, "testnet", wallet.ClassicAddress, reference, "application/octet-stream", "f.bin",
            file.Length, MemoLayout.ChunkBytes, PublicationSession.RootOf(file), 1, "chunks", new Dictionary<int, string>(),
            Strategy: PublicationState.TicketStrategyName).Save(statePath);

        PublicationRun run = new(_connections, vault, Paths, new PublicationHistory(Paths.HistoryPath));
        Assert.True(run.Resume(statePath, new PublishRequest(file, "application/octet-stream", "f.bin", reference, wallet.ClassicAddress, "testnet")));
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

        Assert.True(run.Resume(statePath, request));
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
