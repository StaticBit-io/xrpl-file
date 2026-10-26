using Xrpl.Wallet;

using XrplFile.Keys;
using XrplFile.Tests;

using Xunit;

using static XrplFile.App.Core.Tests.GatedWait;

namespace XrplFile.App.Core.Tests;

public sealed class RunModelTests : VaultHarness
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-runmodel-").FullName;

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Every_stage_the_writer_reports_has_a_readable_name()
    {
        foreach (string stage in new[] { "chunks", "lists", "read-back", "manifest", "done" })
        {
            Assert.False(string.IsNullOrWhiteSpace(RunModel.StageNameOf(stage)));
            Assert.NotEqual(stage, RunModel.StageNameOf(stage));
        }
    }

    [Fact]
    public void The_read_back_stage_says_it_is_not_submitting_anything()
    {
        Assert.Contains("read", RunModel.StageNameOf("read-back"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("submit", RunModel.StageNameOf("read-back"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_stage_the_library_adds_later_is_shown_as_itself_rather_than_hidden()
    {
        Assert.Equal("something-new", RunModel.StageNameOf("something-new"));
    }

    [Fact]
    public void A_percentage_is_zero_before_anything_is_reported()
    {
        RunModel model = new(NewRun(), new ManualTimeProvider());

        Assert.Equal(0, model.Percent);
        Assert.False(model.IsFinished);
    }

    [Fact]
    public void A_percentage_comes_from_the_last_progress_report()
    {
        Assert.Equal(50, RunModel.PercentOf(new PublishProgress("chunks", 5, 10)));
        Assert.Equal(0, RunModel.PercentOf(new PublishProgress("chunks", 0, 0)));
        Assert.Equal(100, RunModel.PercentOf(new PublishProgress("manifest", 1, 1)));
    }

    [Fact]
    public void An_idle_run_cannot_be_cancelled()
    {
        RunModel model = new(NewRun(), new ManualTimeProvider());

        Assert.False(model.CanCancel);
    }

    [Fact]
    public void An_idle_run_has_no_link_failure_or_history_note()
    {
        RunModel model = new(NewRun(), new ManualTimeProvider());

        Assert.Null(model.Link);
        Assert.Null(model.Failure);
        Assert.Null(model.HistoryNote);
    }

    [Fact]
    public void The_stage_text_says_starting_before_any_progress_is_reported()
    {
        RunModel model = new(NewRun(), new ManualTimeProvider());

        Assert.Equal("Starting", model.StageText);
    }

    [Fact]
    public void An_idle_run_is_not_active_and_was_not_cancelled()
    {
        RunModel model = new(NewRun(), new ManualTimeProvider());

        Assert.False(model.IsActive);
        Assert.False(model.WasCancelled);
    }

    /// <summary>
    /// Publish.razor's own reason for <see cref="RunModel.IsActive"/> existing: it used to compare
    /// <c>PublicationRun.Snapshot.State != PublicationRunState.Idle</c> directly in markup. A run
    /// leaves Idle synchronously inside <see cref="PublicationRun.Start"/> itself (the state is set
    /// under the run's own lock before the background task is even scheduled), so this needs no
    /// gating to observe deterministically.
    /// </summary>
    [Fact]
    public async Task A_started_run_is_active_immediately()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);
        RunModel model = new(run, new ManualTimeProvider());

        Assert.True(run.Start(request));

        Assert.True(model.IsActive);

        // Let the run actually finish rather than leaving a background task dangling past the end of
        // the test.
        await run.Completion;
    }

    /// <summary>
    /// The gap <see cref="RunModel.WasCancelled"/> closes: <see cref="PublicationRun"/> leaves
    /// <see cref="RunSnapshot.Failure"/> null on a cancellation (cancelling is something a person
    /// did, not a fault), which used to leave <c>Run.razor</c> with neither Link nor Failure to show
    /// for a finished, cancelled run.
    /// </summary>
    [Fact]
    public async Task A_cancelled_run_reports_WasCancelled_with_no_failure_and_no_link()
    {
        FakeConnectionFactory connections = new();
        GatedSubmitSink gate = new(connections.Sink, gateAt: 1);
        GatedConnectionFactory gated = new(connections, gate);

        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, connections: gated, ledger: connections.Ledger);
        RunModel model = new(run, new ManualTimeProvider());

        Assert.True(run.Start(request));
        Assert.True(model.IsActive);

        await WaitForGateAsync(gate, run);
        run.Cancel();
        gate.Release();

        await run.Completion;

        Assert.Equal(PublicationRunState.Cancelled, model.Snapshot.State);
        Assert.True(model.WasCancelled);
        Assert.True(model.IsFinished);
        Assert.True(model.IsActive);
        Assert.Null(model.Failure);
        Assert.Null(model.Link);
    }

    /// <summary>
    /// Report defect 3: under "Cancelled", the elapsed time kept ticking and the rate came back,
    /// because the line was drawn from the screen's clock as if the run were still going.
    /// </summary>
    [Fact]
    public async Task A_cancelled_runs_line_stops_where_the_run_stopped()
    {
        FakeConnectionFactory connections = new();
        GatedSubmitSink gate = new(connections.Sink, gateAt: 1);
        GatedConnectionFactory gated = new(connections, gate);

        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, connections: gated, ledger: connections.Ledger);
        ManualTimeProvider time = new();
        RunModel model = new(run, time);

        Assert.True(run.Start(request));
        Assert.Null(model.Snapshot.FinishedAt);

        await WaitForGateAsync(gate, run);
        run.Cancel();
        gate.Release();
        await run.Completion;

        RunSnapshot stopped = model.Snapshot;
        Assert.NotNull(stopped.FinishedAt);
        Assert.True(stopped.FinishedAt >= stopped.StartedAt);

        string line = model.StageText;
        time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(line, model.StageText);
        Assert.StartsWith("Stopped while", line, StringComparison.Ordinal);
        Assert.DoesNotContain("/s", line, StringComparison.Ordinal);
        Assert.DoesNotContain("left", line, StringComparison.Ordinal);

        model.Dismiss();
        Assert.Null(model.Snapshot.FinishedAt);
    }

    /// <summary>
    /// <see cref="RunModel.Capture"/> has exactly one caller in the shipped application --
    /// Run.razor -- and every other test in this file exercises the per-property members
    /// (<see cref="RunModel.CanCancel"/>, <see cref="RunModel.StageText"/>, ...) directly, never
    /// <see cref="RunModel.Capture"/> itself. Without this test, <see cref="RunView"/>'s own
    /// derivations are a second, untested copy of the same logic: an edit to, say, how
    /// <see cref="RunModel.StageText"/> formats or which states count as
    /// <see cref="RunModel.IsFinished"/>, made to one copy and not the other, would leave every
    /// other test in this file green while changing what Run.razor actually draws. Pins the two
    /// copies together across the three states a render can actually observe -- idle, a run parked
    /// mid-submission by the gate, and a run that has finished -- so they cannot drift apart
    /// silently.
    /// </summary>
    [Fact]
    public async Task Capture_agrees_with_the_per_property_reads_across_idle_running_and_finished()
    {
        RunModel idleModel = new(NewRun(), new ManualTimeProvider());
        AssertViewMatchesProperties(idleModel);

        FakeConnectionFactory connections = new();
        GatedSubmitSink gate = new(connections.Sink, gateAt: 1);
        GatedConnectionFactory gated = new(connections, gate);

        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, connections: gated, ledger: connections.Ledger);
        RunModel model = new(run, new ManualTimeProvider());

        Assert.True(run.Start(request));

        await WaitForGateAsync(gate, run);
        AssertViewMatchesProperties(model);

        gate.Release();
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, model.Snapshot.State);
        AssertViewMatchesProperties(model);
    }

    [Fact]
    public void A_run_stopped_by_a_window_conflict_offers_no_Resume()
    {
        string path = Path.Combine(_directory, "conflict-run.json");
        new PublicationState(3, "testnet", "rWriter", "key", "text/plain", null, 2500, 1000, "AB", 1, "chunks",
            Strategy: "batch", Payer: XrplWallet.Generate().ClassicAddress, PayerKeyReference: "key-p", Fence: 0,
            Failure: PublicationState.WriterWindowConflictFailure).Save(path);
        RunSnapshot snapshot = new(PublicationRunState.Failed, null, null, "failed", "f", "testnet", null) { StatePath = path };

        Assert.False(RunModel.IsResumable(snapshot));
    }

    /// <summary>Every field <see cref="RunModel.Capture"/> derives, checked against its own
    /// per-property twin. Both are read back to back with nothing awaited in between, over a run
    /// that is either not yet started, parked at the gate, or already finished -- none of which can
    /// change state between the two reads, so any difference here is the two derivations actually
    /// disagreeing, not a race.</summary>
    private static void AssertViewMatchesProperties(RunModel model)
    {
        RunView view = model.Capture();

        Assert.Equal(model.CanCancel, view.CanCancel);
        Assert.Equal(model.IsFinished, view.IsFinished);
        Assert.Equal(model.WasCancelled, view.WasCancelled);
        Assert.Equal(model.Link, view.Link);
        Assert.Equal(model.Failure, view.Failure);
        Assert.Equal(model.HistoryNote, view.HistoryNote);
        Assert.Equal(model.TicketNote, view.TicketNote);
        Assert.Equal(model.TicketsLocked, view.TicketsLocked);
        Assert.Equal(model.StateNote, view.StateNote);
        Assert.Equal(model.StageText, view.StageText);
        Assert.Equal(model.Percent, view.Percent);
        Assert.Equal(RunModel.IsResumable(model.Snapshot), view.CanResume);
        Assert.Equal(model.Snapshot.StatePath, view.StatePath);
    }

    /// <summary>
    /// A run that is never actually started in the test that calls this -- safe to build over
    /// <see cref="NeverVault"/>. Shares this class's own <see cref="_directory"/> rather than
    /// creating a fresh temporary one per call: nothing here ever calls <see cref="RunPaths.EnsureCreated"/>
    /// (an idle run touches no file), so every caller sharing one directory is harmless, and
    /// <see cref="DisposeAsync"/> already owns cleaning it up -- a second, per-call temporary
    /// directory would need its own cleanup that nothing here ever did.
    /// </summary>
    private PublicationRun NewRun()
    {
        RunPaths paths = new(_directory);
        return new PublicationRun(
            new FakeConnectionFactory(), new NeverVault(), paths, new PublicationHistory(paths.HistoryPath));
    }

    /// <summary>
    /// A real, funded vault entry and a matching request — everything a run needs to actually leave
    /// Idle. Mirrors <c>PublicationRunTests.ReadyAsync</c>; not shared with it because the two
    /// already differ in what they take (this one threads through an optional
    /// <see cref="FakeLedgerSource"/> for the balance, which <c>PublicationRunTests</c> has no need
    /// of) and in what they return, so a shared helper would need as many parameters to configure
    /// away the differences as it would save in each call site.
    /// </summary>
    private async Task<(PublicationRun Run, PublishRequest Request)> ReadyAsync(
        CancellationToken token, IConnectionFactory? connections = null, FakeLedgerSource? ledger = null)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);

        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key", "standalone", wallet.ClassicAddress, wallet.Seed),
            token);

        FakeConnectionFactory fallback = new();
        (ledger ?? fallback.Ledger).Balances[wallet.ClassicAddress] = 100_000_000;

        RunPaths paths = new(_directory);
        PublicationRun run = new(connections ?? fallback, vault, paths, new PublicationHistory(paths.HistoryPath));
        PublishRequest request = Requests.Fresh(
            new byte[4096], "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1");

        return (run, request);
    }

    [Fact]
    public async Task A_dismissed_publication_lets_the_next_one_start_and_finish_with_its_own_link()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (PublicationRun run, PublishRequest request) = await ReadyAsync(token);
        RunModel model = new(run, new ManualTimeProvider());

        Assert.True(run.Start(request));
        await run.Completion;
        string first = model.Link!;

        model.Dismiss();

        Assert.False(model.IsActive);
        Assert.Null(model.Link);

        Assert.True(run.Start(request with { Size = 5000, Source = new MemoryPublicationSource(new byte[5000]), Name = "second.bin" }));
        await run.Completion;

        Assert.True(model.IsFinished);
        Assert.Equal("second.bin", model.Snapshot.Name);
        Assert.NotNull(model.Link);
        Assert.NotEqual(first, model.Link);
    }

    [Fact]
    public async Task A_finished_publication_can_be_followed_by_another_without_a_dismiss()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (PublicationRun run, PublishRequest request) = await ReadyAsync(token);
        RunModel model = new(run, new ManualTimeProvider());

        Assert.True(run.Start(request));
        await run.Completion;
        string first = model.Link!;

        Assert.True(run.Start(request with { Size = 5000, Source = new MemoryPublicationSource(new byte[5000]), Name = "second.bin" }));
        await run.Completion;

        Assert.Equal("second.bin", model.Snapshot.Name);
        Assert.NotEqual(first, model.Link);
    }

    [Fact]
    public async Task Only_a_running_publication_keeps_the_picker_away()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (PublicationRun run, PublishRequest request) = await ReadyAsync(token);
        RunModel model = new(run, new ManualTimeProvider());

        Assert.False(model.IsRunning);

        Assert.True(run.Start(request));
        Assert.True(model.IsRunning);
        await run.Completion;

        Assert.False(model.IsRunning);
        Assert.True(model.IsFinished);

        ShellModel shell = new();
        shell.Show(AppTab.History);
        shell.Show(AppTab.Publish);

        Assert.True(run.Start(request with { Size = 5000, Source = new MemoryPublicationSource(new byte[5000]), Name = "second.bin" }));
        await run.Completion;
        Assert.Equal("second.bin", model.Snapshot.Name);
    }

    [Fact]
    public void Closing_over_a_ticketed_run_says_the_tickets_stay_and_what_each_one_holds()
    {
        RunSnapshot snapshot = new(PublicationRunState.Running, null, null, null, "big.bin", "1", null)
        {
            UsesTickets = true,
            OwnerReserveDrops = 2_000_000,
        };

        Assert.Equal(
            "\"big.bin\" is still publishing. Closing now stops the run; it can be resumed later. The tickets it "
            + "reserved stay on this account, each holding 2 XRP of owner reserve, until a later "
            + "publication from this account spends them or the account is closed. Close anyway?",
            RunModel.CloseConfirmation(snapshot));
    }

    [Fact]
    public void Closing_over_a_run_without_tickets_reads_as_it_always_did()
    {
        RunSnapshot snapshot = new(PublicationRunState.Running, null, null, null, "small.txt", "1", null);

        Assert.Equal(
            "\"small.txt\" is still publishing. Closing now will stop the run; it can be resumed later. Close anyway?",
            RunModel.CloseConfirmation(snapshot));
    }

    [Fact]
    public void A_run_with_no_name_yet_is_called_the_file()
    {
        RunSnapshot snapshot = new(PublicationRunState.Running, null, null, null, null, null, null);

        Assert.StartsWith("\"the file\" is still publishing.", RunModel.CloseConfirmation(snapshot), StringComparison.Ordinal);
    }

    /// <summary>A report whose tidy-up ran and found nothing, unless the caller says otherwise.</summary>
    private static PublicationReport Report() =>
        new(
            new XrplFileLink("testnet", new string('A', 64)),
            new Manifest("application/octet-stream", 4096, 1024, "AB", 1, 2, 1, 0, "file.bin"),
            "rWriter", 6, 72, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, 0, new string('B', 64))
        {
            TicketReleaseFeeDrops = 0,
            TicketReleaseFeeDropsInDoubt = 0,
        };

    private static RunSnapshot Finished(PublicationReport report, long ownerReserveDrops = 2_000_000) =>
        new(PublicationRunState.Succeeded, null, report, null, "big.bin", "1", null)
        {
            UsesTickets = true,
            OwnerReserveDrops = ownerReserveDrops,
        };

    private static void AssertTicketNote(RunSnapshot snapshot, string? text, bool locked)
    {
        Assert.Equal(text, RunModel.TicketNoteOf(snapshot));
        Assert.Equal(locked, RunModel.TicketsLockedOf(snapshot));
    }

    [Fact]
    public void A_tidy_up_that_found_nothing_says_nothing()
    {
        AssertTicketNote(Finished(Report()), null, locked: false);
    }

    [Fact]
    public void A_run_that_did_not_succeed_says_nothing_about_tickets_or_its_state_file()
    {
        foreach (PublicationRunState state in Enum.GetValues<PublicationRunState>().Where(s => s != PublicationRunState.Succeeded))
        {
            RunSnapshot snapshot = new(state, null, null, state == PublicationRunState.Failed ? "it broke" : null, "big.bin", "1", null)
            {
                UsesTickets = true,
                OwnerReserveDrops = 2_000_000,
            };

            AssertTicketNote(snapshot, null, locked: false);
            Assert.Null(RunModel.StateNoteOf(snapshot));
        }
    }

    [Fact]
    public void Released_and_already_gone_tickets_read_as_released_with_their_reserve_free()
    {
        AssertTicketNote(
            Finished(Report() with { TicketsReleased = 3, TicketsAlreadyGone = 2 }),
            "5 leftover tickets were released, freeing their owner reserve.",
            locked: false);
    }

    [Fact]
    public void One_released_ticket_reads_in_the_singular()
    {
        AssertTicketNote(
            Finished(Report() with { TicketsAlreadyGone = 1 }),
            "1 leftover ticket was released, freeing its owner reserve.",
            locked: false);
    }

    [Fact]
    public void Tickets_not_released_say_they_stay_what_each_holds_and_what_frees_them()
    {
        AssertTicketNote(
            Finished(Report() with { TicketsNotReleased = 3 }),
            "3 leftover tickets could not be released. They stay on this account, each holding 2 XRP of "
            + "owner reserve, until a later publication from this account spends them or the account is closed.",
            locked: true);
    }

    [Fact]
    public void One_ticket_not_released_reads_in_the_singular()
    {
        AssertTicketNote(
            Finished(Report() with { TicketsNotReleased = 1 }, ownerReserveDrops: 200_000),
            "1 leftover ticket could not be released. It stays on this account, holding 0.2 XRP of "
            + "owner reserve, until a later publication from this account spends it or the account is closed.",
            locked: true);
    }

    [Fact]
    public void Some_released_and_some_not_names_both()
    {
        AssertTicketNote(
            Finished(Report() with { TicketsReleased = 4, TicketsAlreadyGone = 1, TicketsNotReleased = 3 }),
            "5 leftover tickets were released, but 3 could not be. They stay on this account, each holding "
            + "2 XRP of owner reserve, until a later publication from this account spends them or the "
            + "account is closed.",
            locked: true);

        AssertTicketNote(
            Finished(Report() with { TicketsReleased = 1, TicketsNotReleased = 1 }),
            "1 leftover ticket was released, but 1 could not be. It stays on this account, holding 2 XRP "
            + "of owner reserve, until a later publication from this account spends it or the account is closed.",
            locked: true);
    }

    private const string TidyUpDidNotFinish =
        "Releasing the leftover tickets on this account did not finish. Any it did not release stay on the account, "
        + "each holding 2 XRP of owner reserve, until a later publication from this account spends them "
        + "or the account is closed.";

    [Fact]
    public void A_failed_tidy_up_does_not_claim_to_know_how_many_tickets_stay()
    {
        AssertTicketNote(
            Finished(Report() with
            {
                TicketReleaseFailure = new TimeoutException("the tidy-up did not stop"),
                TicketReleaseFeeDrops = null,
                TicketReleaseFeeDropsInDoubt = null,
            }),
            TidyUpDidNotFinish,
            locked: true);
    }

    [Fact]
    public void A_tidy_up_whose_counts_were_lost_without_a_failure_reads_as_unfinished_too()
    {
        AssertTicketNote(
            Finished(Report() with { TicketReleaseFeeDrops = null, TicketReleaseFeeDropsInDoubt = null }),
            TidyUpDidNotFinish,
            locked: true);
    }

    [Fact]
    public void A_state_file_that_could_not_be_saved_says_to_delete_the_entry_rather_than_resume_it()
    {
        Assert.Null(RunModel.StateNoteOf(Finished(Report())));

        Assert.Equal(
            "This run's progress file could not be updated, so it may still be listed under unfinished runs. The "
            + "file is published; delete that entry rather than resume it, which would pay again for transactions "
            + "the file no longer needs.",
            RunModel.StateNoteOf(Finished(Report() with { StateSaveFailure = new IOException("disk full") })));
    }
}
