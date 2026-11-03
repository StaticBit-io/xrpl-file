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
    private async Task<(PublicationRun Run, PublishRequest Request, PublicationHistory History, KeyVault Vault)> ReadyAsync(int size = 20_000, TimeProvider? time = null)
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
        PublicationRun run = new(_connections, vault, Paths, history, time: time, batchOptions: Fast);
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
    /// timing of the fake's rounds. With a <paramref name="time"/>, that clock moves on by
    /// <paramref name="transmitting"/> during the first transmission, so the interrupted session has worked for
    /// a known time.
    /// </summary>
    private async Task<(PublicationRun Run, PublishRequest Request, string StatePath, KeyVault Vault)> InterruptedExpensiveAsync(
        ManualTimeProvider? time = null, TimeSpan transmitting = default)
    {
        (PublicationRun run, PublishRequest request, _, KeyVault vault) = await ReadyAsync(400_000, time);
        _connections.Ledger.OpenLedgerFee = new OpenLedgerFee(190, 10);
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal != 1) return;

            time?.Advance(transmitting);
            run.Cancel();
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

        // The quote is the work that remains, not the file's whole price.
        BatchPublicationEstimate whole = await PublicationEstimator.EstimateBatchAsync(
            _connections.Ledger, request.Account, request.PayerAccount!, 400_000, Token);
        Assert.True(asked.Transactions < whole.Memos + whole.Batches);

        // And its time is the remaining work alone: the hash pass is over by the time the person is asked.
        BatchPublicationEstimate remaining = await RemainingEstimateAsync(request, asked.Transactions);
        Assert.Equal(PublicationDuration.ForBatchRemaining(remaining), asked.Duration);

        Assert.True(run.AnswerConfirmation(true));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is null);
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.Null(run.Snapshot.Confirmation);
    }

    /// <summary>
    /// The estimate of the chunks a resume still has to write, found from the transactions its quote names. The
    /// run does not hand its estimate out, and a file of this size is one block with no lists, so the public
    /// estimator prices <c>n</c> chunks exactly as the resume priced them.
    /// </summary>
    private async Task<BatchPublicationEstimate> RemainingEstimateAsync(PublishRequest request, long transactions)
    {
        for (long chunks = 1; chunks <= FileTree.ChunkCount(request.Size, MemoLayout.ChunkBytes); chunks++)
        {
            BatchPublicationEstimate candidate = await PublicationEstimator.EstimateBatchAsync(
                _connections.Ledger, request.Account, request.PayerAccount!, chunks * MemoLayout.ChunkBytes, Token);

            if (candidate.Memos + candidate.Batches == transactions) return candidate;
        }

        throw new InvalidOperationException($"no number of chunks is priced at {transactions} transactions");
    }

    [Fact]
    public async Task While_a_confirmation_waits_the_screen_offers_one_way_to_decline_and_no_progress()
    {
        (PublicationRun run, PublishRequest request, string statePath, _) = await InterruptedExpensiveAsync();
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 400_000), Token);
        RunModel model = new(run);

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is not null);
        RunView view = model.Capture();

        Assert.False(view.ShowsProgress);
        Assert.False(view.CanCancel);
        Assert.NotNull(view.Confirmation);
        Assert.Equal(RunModel.AwaitingConfirmationText, view.StageText);

        Assert.True(run.AnswerConfirmation(false));
        await run.Completion;
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
    public async Task Lock_while_a_resume_waits_for_the_person_stops_it_and_keeps_it_resumable()
    {
        (PublicationRun run, PublishRequest request, string statePath, KeyVault vault) = await InterruptedExpensiveAsync();
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 400_000), Token);
        byte[] before = await File.ReadAllBytesAsync(statePath, Token);

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is not null);

        UnlockModel unlock = new(vault, vaultPathIsDefault: true, run);
        await unlock.LockAsync();

        Assert.True(run.Completion.IsCompleted);
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.Null(run.Snapshot.Confirmation);
        Assert.False(vault.IsUnlocked);
        Assert.Equal(before, await File.ReadAllBytesAsync(statePath, Token));
    }

    [Fact]
    public async Task Cancelling_while_a_resume_waits_for_the_person_sends_nothing_and_stays_resumable()
    {
        (PublicationRun run, PublishRequest request, string statePath, _) = await InterruptedExpensiveAsync();
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 400_000), Token);
        byte[] before = await File.ReadAllBytesAsync(statePath, Token);
        int sent = _connections.BatchSink!.Transmissions.Count;

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is not null);

        run.Cancel();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.Null(run.Snapshot.Confirmation);
        Assert.False(run.AnswerConfirmation(true));
        Assert.Equal(sent, _connections.BatchSink.Transmissions.Count);
        Assert.Equal(before, await File.ReadAllBytesAsync(statePath, Token));

        // Still on the unfinished list, which is where a stopped run is resumed from.
        UnfinishedModel unfinished = new(Paths, run, new FakeFilePicker());
        unfinished.Refresh();
        Assert.Equal(statePath, Assert.Single(unfinished.Rows).Path);
    }

    [Fact]
    public async Task Reset_while_a_resume_waits_changes_nothing()
    {
        (PublicationRun run, PublishRequest request, string statePath, _) = await InterruptedExpensiveAsync();
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 400_000), Token);

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is not null);
        CostSummary asked = run.Snapshot.Confirmation!;

        run.Reset();

        Assert.Equal(PublicationRunState.Running, run.Snapshot.State);
        Assert.Same(asked, run.Snapshot.Confirmation);
        Assert.Equal(statePath, run.Snapshot.StatePath);

        // The question is still the person's to answer.
        Assert.True(run.AnswerConfirmation(false));
        await run.Completion;
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
    }

    [Fact]
    public async Task A_declined_resume_lets_the_computer_sleep_again_from_the_thread_that_held_it()
    {
        (_, PublishRequest request, string statePath, KeyVault vault) = await InterruptedExpensiveAsync();
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 400_000), Token);
        RecordingExecutionState state = new();
        PublicationRun run = new(
            _connections, vault, Paths, new PublicationHistory(Paths.HistoryPath), batchOptions: Fast, keepAwake: new ThreadKeepAwake(state));

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is not null);

        // Held while the question waits: the hash pass and the reconciliation are behind it, the wait is the person's.
        await BatchTestWait.UntilAsync(() => state.Calls.Count == 1);
        Assert.Equal(["require"], state.Calls.Select(call => call.Call));

        Assert.True(run.AnswerConfirmation(false));
        await run.Completion;

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
        Assert.Equal(state.Calls[0].Thread, state.Calls[1].Thread);
    }

    [Fact]
    public async Task Lock_cancels_the_run_before_it_locks_the_vault()
    {
        (PublicationRun run, PublishRequest request, _, KeyVault vault) = await ReadyAsync();
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? unlockedWhenCancelled = null;
        _connections.BeforeConnect = async (_, token) =>
        {
            reached.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                unlockedWhenCancelled = vault.IsUnlocked;
                throw;
            }
        };

        Assert.True(run.Start(request));
        await reached.Task.WaitAsync(Token);

        UnlockModel unlock = new(vault, vaultPathIsDefault: true, run);
        await unlock.LockAsync();

        Assert.True(unlockedWhenCancelled, "the vault was already locked when the run learned it was cancelled");
        Assert.True(run.Completion.IsCompleted);
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task Lock_locks_the_vault_even_when_the_run_does_not_stop_within_its_bound()
    {
        ManualTimeProvider time = new();
        (PublicationRun run, PublishRequest request, _, KeyVault vault) = await ReadyAsync(time: time);
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // A node that does not answer and does not look at the token: the run cannot stop until the gate opens.
        _connections.BeforeConnect = async (_, _) =>
        {
            reached.TrySetResult();
            await gate.Task;
        };

        Assert.True(run.Start(request));
        await reached.Task.WaitAsync(Token);

        UnlockModel unlock = new(vault, vaultPathIsDefault: true, run);
        Task locking = unlock.LockAsync();

        Assert.False(locking.IsCompleted);
        Assert.True(vault.IsUnlocked);

        time.Advance(UnlockModel.RunStopTimeout);

        // Well under the real 10 s: the bound is on the run's clock, which the test moved.
        await locking.WaitAsync(TimeSpan.FromSeconds(3), Token);

        Assert.False(vault.IsUnlocked);
        Assert.False(run.Completion.IsCompleted);

        gate.SetResult();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
    }

    [Fact]
    public async Task An_unanswered_confirmation_past_the_vault_idle_window_is_a_decline_and_the_vault_is_left_to_lock()
    {
        (_, PublishRequest request, string statePath, KeyVault vault) = await InterruptedExpensiveAsync();
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 400_000), Token);
        byte[] before = await File.ReadAllBytesAsync(statePath, Token);
        int sent = _connections.BatchSink!.Transmissions.Count;

        ManualTimeProvider time = new();
        TimeSpan idle = TimeSpan.FromMinutes(15);
        await using VaultAutoLock autoLock = new(vault, idle, time);
        PublicationRun run = new(_connections, vault, Paths, new PublicationHistory(Paths.HistoryPath), autoLock, time, Fast);

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is not null);

        // The lease touches every minute, so without the bound the question would hold the vault open for good.
        int minutes = 0;

        while (!run.Completion.IsCompleted && minutes < 20)
        {
            Assert.True(vault.IsUnlocked);
            time.Advance(TimeSpan.FromMinutes(1));
            minutes++;
            await Task.Delay(5, Token);
        }

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.InRange(minutes, 15, 17);
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.Null(run.Snapshot.Confirmation);
        Assert.False(run.AnswerConfirmation(true));
        Assert.Equal(sent, _connections.BatchSink.Transmissions.Count);
        Assert.Equal(before, await File.ReadAllBytesAsync(statePath, Token));

        // Nothing touches the vault after the window: one more idle window locks it.
        Assert.True(vault.IsUnlocked);
        time.Advance(idle);
        await BatchTestWait.UntilAsync(() => !vault.IsUnlocked);
    }

    [Fact]
    public async Task A_resumed_publication_counts_both_sessions_and_not_the_wait_for_the_person()
    {
        ManualTimeProvider time = new();
        (PublicationRun run, PublishRequest request, string statePath, _) =
            await InterruptedExpensiveAsync(time, TimeSpan.FromMinutes(10));
        Assert.Equal(600, PublicationState.Load(statePath).WorkedSeconds);
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 400_000), Token);

        bool advanced = false;
        _connections.BatchSink!.Transmitting = _ =>
        {
            if (advanced) return;

            advanced = true;
            time.Advance(TimeSpan.FromMinutes(5));
        };

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is not null);
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.True(run.AnswerConfirmation(true));
        await run.Completion;

        RunSnapshot snapshot = run.Snapshot;
        Assert.Equal(PublicationRunState.Succeeded, snapshot.State);
        Assert.Equal(TimeSpan.FromSeconds(600), snapshot.WorkedBefore);
        Assert.Equal(TimeSpan.FromSeconds(180), snapshot.WaitedForPerson);
        Assert.Equal("Finished in 15:00", TestText.En(RunModel.StageTextOf(snapshot, time.GetUtcNow(), rate: null)));
    }

    [Fact]
    public async Task A_new_run_instance_resuming_a_state_counts_its_recorded_working_time()
    {
        ManualTimeProvider time = new();
        (_, PublishRequest request, string statePath, KeyVault vault) =
            await InterruptedExpensiveAsync(time, TimeSpan.FromMinutes(10));
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 400_000), Token);

        PublicationRun fresh = new(_connections, vault, Paths, new PublicationHistory(Paths.HistoryPath), time: time, batchOptions: Fast);

        Assert.True(fresh.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => fresh.Snapshot.Confirmation is not null);
        Assert.True(fresh.AnswerConfirmation(true));
        await fresh.Completion;

        Assert.Equal(PublicationRunState.Succeeded, fresh.Snapshot.State);
        Assert.Equal(TimeSpan.FromSeconds(600), fresh.Snapshot.WorkedBefore);
    }

    [Fact]
    public async Task A_stopped_run_has_its_working_time_in_the_state_before_it_reports_stopped()
    {
        ManualTimeProvider time = new();
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(400_000, time);
        _connections.Ledger.OpenLedgerFee = new OpenLedgerFee(190, 10);
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal != 1) return;

            time.Advance(TimeSpan.FromMinutes(10));
            run.Cancel();
        };

        Assert.True(run.Start(request));

        // A screen that offers Resume the moment it sees the run stop must find the figure already written.
        // Polled without a delay so the window between publishing and writing, if it existed, would be seen.
        DateTimeOffset until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (run.Snapshot.State == PublicationRunState.Running)
        {
            Assert.True(DateTimeOffset.UtcNow < until, "the run did not stop within 10 s");
            await Task.Yield();
        }

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.Equal(600, PublicationState.Load(run.Snapshot.StatePath!).WorkedSeconds);
    }

    [Fact]
    public async Task A_working_time_that_cannot_be_written_never_keeps_the_run_from_stopping()
    {
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(400_000);
        _connections.Ledger.OpenLedgerFee = new OpenLedgerFee(190, 10);
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) run.Cancel();
        };
        run.WorkedWriting = _ => throw new InvalidOperationException("not an IO failure");

        Assert.True(run.Start(request));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.Null(PublicationState.Load(run.Snapshot.StatePath!).WorkedSeconds);
    }

    [Fact]
    public async Task Lock_with_no_run_going_just_locks()
    {
        (PublicationRun run, _, _, KeyVault vault) = await ReadyAsync();
        UnlockModel unlock = new(vault, vaultPathIsDefault: true, run);

        await unlock.LockAsync();

        Assert.False(vault.IsUnlocked);
        Assert.Equal(PublicationRunState.Idle, run.Snapshot.State);
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
