using Xrpl.Wallet;
using XrplFile.Keys;
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

/// <summary>Spec S25: a resume asks for its approval after reconciliation, and above the threshold it asks the person.</summary>
public sealed class ResumeConfirmationTests : VaultHarness
{
    private static readonly BatchStrategyOptions Fast = new()
    {
        ChainCheck = TimeSpan.FromMilliseconds(5),
        ProgressSweep = TimeSpan.FromMilliseconds(5),
        HistoryLagLimit = TimeSpan.FromSeconds(2),
        FeeWait = TimeSpan.FromMilliseconds(20),

        // The fake closes a ledger on every read, so its deadlines pass in milliseconds.
        TransportRetryStep = TimeSpan.FromMilliseconds(1),
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-resume-confirm-").FullName;
    private readonly FakeConnectionFactory _connections = new();

    private RunPaths Paths => new(_directory);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A writer and a payer of 100 XRP each in a vault, and a Batch request for <paramref name="size"/> bytes recorded at <c>file.bin</c>.</summary>
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
        PublishRequest request = Requests.Fresh(
            VectorStream.Bytes("app-batch", size), "application/octet-stream", "file.bin", writerRef, writer.ClassicAddress, "testnet",
            payerRef, payer.ClassicAddress, sourcePath: Path.Combine(_directory, "file.bin"));

        return (run, request, history, vault);
    }

    /// <summary>
    /// A Batch run of 400 chunks at an open-ledger fee of 190, cancelled in its first transmission. The payer's worst
    /// case for the whole file is about 1.7 million drops; the resume quotes what remains, measured at 2,888 fee
    /// units x 570 = 1,646,160 drops after the two transmissions the cancel leaves (about 15 chunks landed). That is
    /// 646,160 past the 1,000,000 line, room for some 150 landed chunks, so the confirmation appears whatever the
    /// timing of the fake's rounds.
    /// </summary>
    private async Task<(PublicationRun Run, PublishRequest Request, string StatePath, KeyVault Vault)> InterruptedExpensiveAsync()
    {
        (PublicationRun run, PublishRequest request, _, KeyVault vault) = await ReadyAsync(400_000);
        _connections.Ledger.OpenLedgerFee = new OpenLedgerFee(190, 10);
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) run.Cancel();
        };

        Assert.True(run.Start(request));
        await run.Completion;
        _connections.BatchSink.Transmitting = null;
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);

        return (run, request, run.Snapshot.StatePath!, vault);
    }

    private static PublishRequest Resuming(PublishRequest started, string path) =>
        new(started.Size, started.Type, started.Name, started.KeyReference, started.Account, started.Network,
            started.PayerReference, started.PayerAccount, path) { SourceRecorded = true };

    [Fact]
    public async Task A_resume_above_the_threshold_waits_for_the_person_and_goes_on_when_confirmed()
    {
        (PublicationRun run, PublishRequest request, string statePath, _) = await InterruptedExpensiveAsync();
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 400_000), Token);

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is not null);

        CostSummary asked = run.Snapshot.Confirmation!;
        Assert.True(asked.NeedsConfirmation);
        Assert.True(run.AnswerConfirmation(true));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.Null(run.Snapshot.Confirmation);
    }

    [Fact]
    public async Task A_declined_resume_sends_nothing_and_stays_resumable()
    {
        (PublicationRun run, PublishRequest request, string statePath, _) = await InterruptedExpensiveAsync();
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 400_000), Token);
        byte[] before = await File.ReadAllBytesAsync(statePath, Token);
        int sent = _connections.BatchSink!.Transmissions.Count;

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is not null);
        Assert.True(run.AnswerConfirmation(false));
        await run.Completion;

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.Equal(sent, _connections.BatchSink.Transmissions.Count);
        Assert.Equal(before, await File.ReadAllBytesAsync(statePath, Token));
    }

    [Fact]
    public async Task A_resume_below_the_threshold_asks_nobody()
    {
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(20_000);
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) run.Cancel();
        };
        Assert.True(run.Start(request));
        await run.Completion;
        _connections.BatchSink.Transmitting = null;
        string statePath = run.Snapshot.StatePath!;
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 20_000), Token);
        bool asked = false;

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        while (!run.Completion.IsCompleted)
        {
            asked |= run.Snapshot.Confirmation is not null;
            await Task.Delay(5, Token);
        }

        Assert.False(asked);
        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
    }
}
