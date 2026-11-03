using System.Runtime.CompilerServices;

using Xrpl.Wallet;

using XrplFile.Keys;
using XrplFile.Tests;

using Xunit;

using static XrplFile.App.Core.Tests.GatedWait;

namespace XrplFile.App.Core.Tests;

public sealed class PublicationRunTests : VaultHarness
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-run-").FullName;
    private readonly FakeConnectionFactory _connections = new();

    private RunPaths Paths => new(_directory);

    private PublicationHistory History => new(Paths.HistoryPath);

    /// <param name="connections">
    /// The factory the run is built with. Defaults to the class's own <see cref="_connections"/>;
    /// a test that needs to decorate the connection (to gate a submission, say) passes its own
    /// wrapper here rather than constructing the wallet and vault entry by hand a second time.
    /// </param>
    /// <param name="history">
    /// The history the run is built with. Defaults to <see cref="History"/> (a fresh one over this
    /// instance's own file); a test that needs the append itself to misbehave passes its own.
    /// </param>
    private async Task<(PublicationRun Run, PublishRequest Request)> ReadyAsync(
        CancellationToken token, int size = 4096, IConnectionFactory? connections = null, IPublicationHistory? history = null)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);

        // A real seed, so VaultSigner can derive the account the request names and sign with it.
        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key", "standalone", wallet.ClassicAddress, wallet.Seed),
            token);

        _connections.Ledger.Balances[wallet.ClassicAddress] = 100_000_000;

        PublicationRun run = new(connections ?? _connections, vault, Paths, history ?? History);

        // On disk because a resume opens the recorded path itself.
        string path = Path.Combine(_directory, "file.bin");
        byte[] bytes = new byte[size];
        await File.WriteAllBytesAsync(path, bytes, token);
        PublishRequest request = Requests.Fresh(
            bytes, "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1", sourcePath: path);

        return (run, request);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task A_run_starts_idle()
    {
        (PublicationRun run, _) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PublicationRunState.Idle, run.Snapshot.State);
    }

    [Fact]
    public async Task A_publication_succeeds_reports_progress_and_produces_a_link()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(request));
        await run.Completion;

        RunSnapshot snapshot = run.Snapshot;
        Assert.Equal(PublicationRunState.Succeeded, snapshot.State);
        Assert.NotNull(snapshot.Report);
        Assert.NotNull(snapshot.Progress);
        Assert.Equal("file.bin", snapshot.Name);
        Assert.Null(snapshot.HistoryNote);
        Assert.Equal(_connections.Connections, _connections.Disposals);
    }

    [Fact]
    public async Task A_publication_that_succeeded_is_recorded_in_the_history()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);

        run.Start(request);
        await run.Completion;

        PublicationRecord recorded = Assert.Single(History.Read());
        Assert.Equal(run.Snapshot.Report!.Link.ToString(), recorded.Link);
        Assert.Equal("file.bin", recorded.Name);
        Assert.Equal("1", recorded.Network);
    }

    [Fact]
    public async Task A_publication_that_succeeded_is_reported_so_even_when_the_history_cannot_be_written()
    {
        // A plain file occupying the path the history's own directory would need to be, so
        // PublicationHistory.Save's Directory.CreateDirectory throws IOException every time it is
        // asked to write -- the exact shape of the defect this test guards against: that used to
        // escape PublicationHistory.Append (CreateDirectory sat outside its try), come back here as
        // an IOException, and get caught by RunAsync's IOException handler, reporting a publication
        // that had already spent its money and already landed on the ledger as Failed with no
        // report at all.
        string blockedParent = Path.Combine(_directory, "blocked-history-parent");
        File.WriteAllBytes(blockedParent, [1]);
        PublicationHistory brokenHistory = new(Path.Combine(blockedParent, "publications.json"));

        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, history: brokenHistory);

        run.Start(request);
        await run.Completion;

        RunSnapshot snapshot = run.Snapshot;

        // The publication itself: still Succeeded, still carrying its report and link, even though
        // the history could not be written at all.
        Assert.Equal(PublicationRunState.Succeeded, snapshot.State);
        Assert.NotNull(snapshot.Report);

        // And the person is told the history specifically did not get updated, rather than the
        // failure being silently absorbed where nothing on the run's own screen would ever show it.
        Assert.NotNull(snapshot.HistoryNote);
    }

    [Fact]
    public async Task An_exception_from_appending_to_the_history_keeps_its_own_message()
    {
        // PublicationHistory.Save only degrades on IOException/UnauthorizedAccessException; anything
        // else escapes Append uncaught, which is what RunAsync's own last-resort catch around the
        // append call exists for. An empty path is what provokes that cross-platform: Path.GetDirectoryName
        // returns null for one on every platform (there is no parent directory of an empty path --
        // this is shared BCL logic, not a P/Invoke that could differ by OS, unlike a genuine drive
        // root such as "C:\", whose own null result is Windows-only, since "\" is not a separator on
        // Unix), and Save passes that null straight into Directory.CreateDirectory, which throws
        // ArgumentNullException -- a type neither Save nor Append catches, and nothing here ever
        // actually touches a real path.
        PublicationHistory badHistory = new(string.Empty);

        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, history: badHistory);

        run.Start(request);
        await run.Completion;

        RunSnapshot snapshot = run.Snapshot;
        Assert.Equal(PublicationRunState.Succeeded, snapshot.State);
        Assert.NotNull(snapshot.Report);

        // The exception's own message survives into the sentence, rather than being thrown away:
        // this runs after money has already been spent, and losing every trace of what went wrong
        // afterwards is not something to also do here.
        Assert.NotNull(snapshot.HistoryNote);
        Assert.Contains("Value cannot be null", snapshot.HistoryNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <see cref="IPublicationHistory"/> that returns a malformed result: <c>Recorded: false</c>
    /// with no <c>Note</c> to say why. <see cref="HistoryAppendResult"/>'s own documentation says a
    /// failure to record always carries a reason, but nothing enforces that on
    /// <see cref="IPublicationHistory"/> itself -- the interface admits any implementation, and this
    /// is the one this one real implementation never produces but a future one could.
    /// </summary>
    private sealed class NotRecordedWithNoNoteHistory : IPublicationHistory
    {
        public HistoryAppendResult Append(PublicationRecord record) => new(Recorded: false, Note: null);
    }

    [Fact]
    public async Task A_not_recorded_result_with_no_note_still_surfaces_something()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(
            TestContext.Current.CancellationToken, history: new NotRecordedWithNoNoteHistory());

        run.Start(request);
        await run.Completion;

        RunSnapshot snapshot = run.Snapshot;
        Assert.Equal(PublicationRunState.Succeeded, snapshot.State);
        Assert.NotNull(snapshot.Report);

        // The decision has to be Recorded, not "was there a Note to show": a check that only looked
        // at Note would let "not recorded" pass through in total silence the moment an
        // implementation forgot to explain why, which is exactly the failure mode this test is for.
        Assert.NotNull(snapshot.HistoryNote);
    }

    /// <summary>
    /// An <see cref="IPublicationHistory"/> whose first <see cref="Append"/> call holds until
    /// <see cref="Release"/> is called, then returns a manufactured, unmistakable result instead of
    /// touching the real history behind it at all -- exactly the <see cref="GatedSubmitSink"/>
    /// pattern, over the seam <see cref="IPublicationHistory"/> exists to provide. <see cref="Reached"/>
    /// resolves the instant that first call begins, before it waits, so a test can start a second run
    /// on the same <see cref="PublicationRun"/> while the first one's own append is still held open --
    /// the actual window the generation pin in <c>PublicationRun.RunAsync</c> exists to close.
    /// </summary>
    private sealed class GatedHistory(IPublicationHistory inner) : IPublicationHistory
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _first = 1;

        /// <summary>Resolves the instant the gated (first) append begins, before it waits to be released.</summary>
        public Task Reached => _reached.Task;

        /// <summary>Lets the gated append proceed.</summary>
        public void Release() => _release.TrySetResult();

        public HistoryAppendResult Append(PublicationRecord record)
        {
            if (Interlocked.Exchange(ref _first, 0) == 1)
            {
                _reached.TrySetResult();

                // Append is synchronous, and so is this double's own contract with it: block the
                // calling thread (one of RunAsync's own background ones, never the test's) until
                // the test calls Release(). Deterministic, no wall clock.
                _release.Task.GetAwaiter().GetResult();

                // A manufactured, obviously-distinctive outcome for the gated call, so the test can
                // tell at a glance whether it ever reached a later run's snapshot -- the real
                // history behind this double is never even touched for this call, let alone
                // produces this string on its own.
                return new HistoryAppendResult(
                    Recorded: true, Note: "SENTINEL: this note belongs only to the first, gated run");
            }

            return inner.Append(record);
        }
    }

    [Fact]
    public async Task A_history_note_from_an_earlier_run_does_not_land_on_a_newer_ones_snapshot()
    {
        GatedHistory gate = new(History);

        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), TestContext.Current.CancellationToken);

        XrplWallet wallet1 = XrplWallet.Generate();
        string reference1 = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key 1", "standalone", wallet1.ClassicAddress, wallet1.Seed),
            TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[wallet1.ClassicAddress] = 100_000_000;

        XrplWallet wallet2 = XrplWallet.Generate();
        string reference2 = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key 2", "standalone", wallet2.ClassicAddress, wallet2.Seed),
            TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[wallet2.ClassicAddress] = 100_000_000;

        PublicationRun run = new(_connections, vault, Paths, gate);

        run.Start(Requests.Fresh(
            new byte[4096], "application/octet-stream", "run1.bin", reference1, wallet1.ClassicAddress, "1"));

        // Run 1 has already published Succeeded (Append only runs after that -- see
        // PublicationRun.RunAsync's own remarks) and is now held inside its own append call. This is
        // exactly the window that lets Begin accept the second Start below at all: _state is no
        // longer Running.
        Task reachedOrFinished = await Task.WhenAny(gate.Reached, run.Completion);
        if (!ReferenceEquals(reachedOrFinished, gate.Reached))
        {
            throw new InvalidOperationException($"run 1 finished ({run.Snapshot.State}) before reaching its own append");
        }

        Task run1Completion = run.Completion;

        // Wrapped in try/finally: run 1's background thread is parked inside its own gated Append
        // call from this point on, and stays that way until gate.Release() runs. Without the finally,
        // an assertion failing anywhere in here would leave that thread blocked forever rather than
        // just failing the test -- the test still fails and reports correctly either way, but a
        // parked thread should not be left behind by a test that is done.
        try
        {
            Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);

            // The second run, on the very same PublicationRun instance and the very same shared
            // history -- accepted only because _state is Succeeded, not Running.
            Assert.True(run.Start(Requests.Fresh(
                new byte[4096], "application/octet-stream", "run2.bin", reference2, wallet2.ClassicAddress, "1")));
            await run.Completion;

            Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
            Assert.Equal("run2.bin", run.Snapshot.Name);
        }
        finally
        {
            // Lets run 1's own append finally proceed and try to write its (manufactured,
            // unmistakable) note.
            gate.Release();
        }

        await run1Completion;

        // The actual test: did run 1's note land here, on run 2's now-current snapshot?
        Assert.Equal("run2.bin", run.Snapshot.Name);
        Assert.Null(run.Snapshot.HistoryNote);
    }

    [Fact]
    public async Task A_history_note_from_a_run_does_not_land_after_the_screen_has_reset_it()
    {
        GatedHistory gate = new(History);
        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, history: gate);

        run.Start(request);

        // Held inside its own append call, after it already published Succeeded.
        Task reachedOrFinished = await Task.WhenAny(gate.Reached, run.Completion);
        if (!ReferenceEquals(reachedOrFinished, gate.Reached))
        {
            throw new InvalidOperationException($"the run finished ({run.Snapshot.State}) before reaching its own append");
        }

        Task runCompletion = run.Completion;

        // Wrapped in try/finally for the same reason the sibling test above is: the run's background
        // thread is parked inside its own gated Append call until gate.Release() runs, and an
        // assertion failing in between must not leave it parked forever.
        try
        {
            Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);

            // No second run this time -- just the screen putting the finished run's outcome away,
            // which Reset accepts for the same reason Begin would: _state is Succeeded, not Running.
            // This is the scenario Reset's own generation bump exists for: without it, the run's
            // late-arriving note would still match the generation Reset just cleared and land on
            // this now-Idle snapshot.
            run.Reset();
            Assert.Equal(PublicationRunState.Idle, run.Snapshot.State);
        }
        finally
        {
            // Lets the run's own append finally proceed and try to write its (manufactured,
            // unmistakable) note.
            gate.Release();
        }

        await runCompletion;

        // The actual test: did the note land here, after the reset?
        Assert.Equal(PublicationRunState.Idle, run.Snapshot.State);
        Assert.Null(run.Snapshot.HistoryNote);
    }

    /// <summary>
    /// A connection whose <see cref="DisposeAsync"/> throws after actually disposing the real one
    /// underneath, standing in for a socket or a strategy's own cleanup that itself reports an error
    /// on close. Used to prove that a throwing disposal cannot turn an already-decided outcome into
    /// a different one (see <c>PublicationRun.RunAsync</c>'s <c>DisposeQuietlyAsync</c> and its own
    /// remarks on why that dependency is closed rather than left implicit).
    /// </summary>
    private sealed class ThrowingDisposalConnectionFactory(FakeConnectionFactory inner) : IConnectionFactory
    {
        public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
        {
            ILedgerConnection connection = await inner.ConnectAsync(network, cancellationToken).ConfigureAwait(false);
            return new Connection(connection);
        }

        private sealed class Connection(ILedgerConnection inner) : ILedgerConnection
        {
            public ILedgerSource Source => inner.Source;

            public ILedgerSink Sink => inner.Sink;

            public string Url => inner.Url;

            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                throw new IOException("simulated: closing the connection itself failed");
            }
        }
    }

    [Fact]
    public async Task A_disposal_that_throws_does_not_turn_a_completed_publication_into_a_reported_failure()
    {
        ThrowingDisposalConnectionFactory connections = new(_connections);
        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, connections: connections);

        run.Start(request);
        await run.Completion;

        RunSnapshot snapshot = run.Snapshot;

        // The publication itself succeeded, and closing the connection afterwards throwing must not
        // be able to take that back: without DisposeQuietlyAsync's own guard, this run's
        // finalState = Succeeded (already decided before the connection's disposal even started)
        // would be overwritten by the generic catch-all, reporting Failed with no report -- the same
        // shape of loss fix 5 (across both review rounds) exists to prevent, just from a different
        // cause.
        Assert.Equal(PublicationRunState.Succeeded, snapshot.State);
        Assert.NotNull(snapshot.Report);
    }

    [Fact]
    public async Task A_second_run_while_one_is_going_is_refused()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(request));
        Assert.False(run.Start(request));

        await run.Completion;

        // The refusal is only half the requirement: the run already going must not have been
        // corrupted by the second Start that was turned away.
        RunSnapshot snapshot = run.Snapshot;
        Assert.Equal(PublicationRunState.Succeeded, snapshot.State);
        Assert.NotNull(snapshot.Report);
        Assert.Equal("file.bin", snapshot.Name);
    }

    [Fact]
    public async Task A_cancelled_run_reports_cancelled_and_leaves_a_state_file()
    {
        GatedSubmitSink gate = new(_connections.Sink, gateAt: 1);
        GatedConnectionFactory connections = new(_connections, gate);
        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, connections: connections);

        run.Start(request);

        // Cancelling the instant Start() returns races PublicationSession.StartAsync itself: on a
        // fake ledger everything up to and including VaultSigner.CreateAsync's vault reveal
        // resolves synchronously fast, and CreateAsync's very first await
        // (KeyVault.RevealAsync -> the gate's SemaphoreSlim.WaitAsync(cancellationToken)) throws
        // the moment the token is already cancelled -- before PublicationSession.StartAsync ever
        // runs and writes the state file. Measured: cancelling right after Start() reproduced that
        // every single time, not occasionally -- the run reported Cancelled correctly, but
        // PublicationSession.Unfinished came back empty because no state file was ever written.
        //
        // Waiting for the gate fixes this deterministically and without polling: it resolves the
        // instant the run's first submission begins, which is necessarily after
        // PublicationSession.StartAsync has already saved the state file (RunAsync -- and every
        // submission inside it -- only starts once StartAsync has returned). Unlike polling
        // Snapshot.Progress, this is not a lagging indicator: Progress is reported through
        // System.Progress<int>, which posts to the thread pool, so by the time a poll observes it
        // the run may already be further along than the read implies -- on a contended machine, far
        // enough along to have finished. The gate holds the run at exactly this point until
        // Release() is called, so there is nothing left to race. WaitForGateAsync (not a bare
        // await on gate.Reached) also races the gate against the run itself, so a regression that
        // makes the run fail before ever reaching the gate fails this test loudly instead of
        // hanging it.
        await WaitForGateAsync(gate, run);
        run.Cancel();
        gate.Release();

        await run.Completion;

        // The contrast this test is actually for: without the wait above (cancelling immediately),
        // the run ends Cancelled with no state file, which the assertion below alone would not
        // catch since it only checks Unfinished, not the state -- so both are asserted together.
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.NotEmpty(PublicationSession.Unfinished(Paths.StateDirectory));

        // Cancellation is exactly the path a leak would happen on: the connection this run opened
        // must be closed, not left dangling because the run ended some way other than success.
        Assert.Equal(_connections.Connections, _connections.Disposals);
    }

    [Fact]
    public async Task A_cancelled_run_records_nothing_in_the_history()
    {
        GatedSubmitSink gate = new(_connections.Sink, gateAt: 1);
        GatedConnectionFactory connections = new(_connections, gate);
        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, connections: connections);

        run.Start(request);

        // Cancelling immediately, as the brief's own version does, kills the run inside
        // VaultSigner.CreateAsync before it ever connects (see the sibling test's own remarks on
        // WaitForGateAsync) -- which can never reach history.Append under any implementation, correct
        // or broken, so it proves nothing about the thing this test names. Waiting for the gate
        // cancels a run that is actually publishing.
        await WaitForGateAsync(gate, run);
        run.Cancel();
        gate.Release();

        await run.Completion;

        Assert.Empty(History.Read());

        // The connection this run opened is closed, not leaked, even on the path that never reaches
        // the history at all.
        Assert.Equal(_connections.Connections, _connections.Disposals);
    }

    [Fact]
    public async Task An_exception_outside_the_known_types_still_ends_the_run_rather_than_wedging_it()
    {
        // An empty file: PublicationEstimator.EstimateAsync (called from
        // PublicationSession.EstimateAsync, before a signer or a strategy exist at all) throws a
        // plain ArgumentOutOfRangeException for one ("an empty file has no chunks and no root"),
        // which is none of the four types RunAsync's catch clauses name. Before the catch-all this
        // reached, that fault RunAsync's Completion, left _state stuck at Running forever, and took
        // Reset, every future Start, and StopAsync's "waits then reports success" with it --
        // recoverable only by restarting the application.
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken, size: 0);

        Assert.True(run.Start(request));
        await run.Completion;

        RunSnapshot snapshot = run.Snapshot;
        Assert.Equal(PublicationRunState.Failed, snapshot.State);
        Assert.NotNull(snapshot.Failure);

        // A connection was opened before the estimate failed, so it must not leak -- but no strategy
        // exists yet at this point (see the comment above), so this does not exercise the strategy's
        // own disposal finally; A_failure_after_the_strategy_is_built_still_disposes_everything below
        // is what does.
        Assert.Equal(_connections.Connections, _connections.Disposals);

        // The service is not wedged: Reset succeeds and a further Start is accepted, neither of
        // which an unhandled fault would have allowed.
        run.Reset();
        Assert.Equal(PublicationRunState.Idle, run.Snapshot.State);
        Assert.True(run.Start(request));
        await run.Completion;
    }

    [Fact]
    public async Task A_failing_node_leaves_the_run_failed_with_its_own_sentence()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");

        run.Start(request);
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal(TestText.En(PublishErrorMessages.For(XrplFileError.NodeUnreachable)), run.Snapshot.Failure);

        // No connection was ever handed back to leak: ConnectAsync fails before FakeConnectionFactory
        // counts one, so this holds at 0 == 0 -- still the same invariant a run that failed after
        // connecting would have to uphold at a higher count.
        Assert.Equal(_connections.Connections, _connections.Disposals);
    }

    [Fact]
    public async Task A_node_that_stops_answering_mid_run_is_not_reported_as_having_cost_nothing()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);
        _connections.Sink.Throws[1] = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");

        run.Start(request);
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal(
            TestText.En(PublishErrorMessages.ForRun(
                new XrplFileException(XrplFileError.NodeUnreachable, "no answer"),
                RunFailurePoint.WhileSubmitting, usesTickets: false, ownerReserveDrops: 0)),
            run.Snapshot.Failure);
        Assert.NotEmpty(PublicationSession.Unfinished(Paths.StateDirectory));
    }

    [Fact]
    public async Task A_ticketed_run_that_loses_its_node_mid_run_names_its_tickets_reserve()
    {
        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, size: 100_000);
        _connections.Sink.Throws[1] = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");

        run.Start(request);
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal(
            TestText.En(PublishErrorMessages.ForRun(
                new XrplFileException(XrplFileError.NodeUnreachable, "no answer"),
                RunFailurePoint.WhileSubmitting, usesTickets: true, ownerReserveDrops: 200_000)),
            run.Snapshot.Failure);
    }

    [Fact]
    public async Task A_resume_whose_node_does_not_answer_says_this_attempt_spent_nothing()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);
        _connections.Sink.Throws[1] = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");
        run.Start(request);
        await run.Completion;
        UnfinishedRun interrupted = Assert.Single(PublicationSession.Unfinished(Paths.StateDirectory));

        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");
        run.Resume(interrupted.Path, request with { Source = null });
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal(
            TestText.En(PublishErrorMessages.ForRun(
                new XrplFileException(XrplFileError.NodeUnreachable, "no answer"),
                RunFailurePoint.BeforeResuming, usesTickets: false, ownerReserveDrops: 0)),
            run.Snapshot.Failure);
    }

    [Fact]
    public async Task A_one_account_resume_is_not_a_Batch_run_even_when_its_request_names_a_payer()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);
        _connections.Sink.Throws[1] = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");
        run.Start(request);
        await run.Completion;
        UnfinishedRun interrupted = Assert.Single(PublicationSession.Unfinished(Paths.StateDirectory));
        _connections.Sink.Throws.Clear();

        // The recorded strategy decides, not the request: this state file is a one-account run's.
        run.Resume(interrupted.Path, request with { Source = null, PayerAccount = XrplWallet.Generate().ClassicAddress, PayerReference = "unused" });
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.False(run.Snapshot.UsesBatch);
    }

    [Fact]
    public async Task A_failure_after_connecting_still_disposes_the_connection()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);

        // A reference the vault never stored: VaultSigner.CreateAsync's RevealAsync throws
        // VaultError.NoSuchEntry -- after ConnectAsync has already handed back a real connection,
        // unlike the sibling test above where the node itself refuses. Still no strategy at this
        // point either (PublishPolicy.Create runs after VaultSigner.CreateAsync), so this is the
        // connection-only case; A_failure_after_the_strategy_is_built_still_disposes_everything below
        // is where a strategy exists too.
        PublishRequest broken = request with { KeyReference = "not-a-real-reference" };

        run.Start(broken);
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal(TestText.En(VaultErrorMessages.For(VaultError.NoSuchEntry)), run.Snapshot.Failure);
        Assert.Equal(1, _connections.Connections);
        Assert.Equal(_connections.Connections, _connections.Disposals);
    }

    [Fact]
    public async Task A_price_the_application_cannot_use_is_refused_before_a_strategy_is_built()
    {
        // Past int.MaxValue -- see EstimateModelTests's own sibling test for why both strategy
        // option types refuse it. PublishPolicy.Create's own remarks say every caller checks this
        // first and that reaching it otherwise is a defect, naming EstimateModel as the caller --
        // but EstimateModel only gates the *first* look at a request, and RunAsync re-estimates on
        // its own before ever calling PublishPolicy.Create (see its own remarks on why: the node's
        // price can move between the estimate and the run). UnfinishedModel.ResumeAsync reaches
        // PublicationRun.Resume, and so PublishPolicy.Create, without ever going through
        // EstimateModel at all -- so the check has to live here, in RunAsync itself, to actually
        // cover every caller rather than only the one that happens to ask first. Exercised through
        // Start rather than Resume: both call the same RunAsync, and this is the simpler of the two
        // to set up.
        _connections.Ledger.Fees = new NetworkFees((long)int.MaxValue + 1, 1_000_000, 200_000);

        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);

        run.Start(request);
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal(TestText.En(PublishErrorMessages.FeeTooLargeForApplication), run.Snapshot.Failure);

        // No strategy, and therefore no vault reveal, was ever reached for a run refused this
        // early -- but a connection was already opened for the (re-)estimate, and it must not leak.
        Assert.Equal(1, _connections.Connections);
        Assert.Equal(_connections.Connections, _connections.Disposals);
    }

    [Fact]
    public async Task A_failure_after_the_strategy_is_built_still_disposes_everything()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);

        // The node refuses the very first submission -- well after PublishPolicy.Create has already
        // built the strategy, which nothing else in this file reaches: the sibling failure tests
        // above each stop earlier (NodeUnreachable before any connection exists at all; NoSuchEntry
        // and the price-too-large test before any strategy does). SequenceStrategy.SubmitAsync turns
        // a refusal engine result into XrplFileException(SubmitRefused, ...), so this is also the
        // first test to exercise that specific mapping. This is the one test that actually exercises
        // the strategy's own disposal finally (RunAsync's inner try/finally around ISubmissionStrategy), not only
        // the connection's outer one -- there is no separate counter for strategy disposal to assert
        // on directly, but this run cannot reach the connection's own disposal (and this assertion)
        // without the strategy's inner finally having already run first.
        _connections.Sink.Refusals[0] = "tefBAD_AUTH";

        run.Start(request);
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal(TestText.En(PublishErrorMessages.For(XrplFileError.SubmitRefused)), run.Snapshot.Failure);
        Assert.Equal(1, _connections.Connections);
        Assert.Equal(_connections.Connections, _connections.Disposals);
    }

    [Fact]
    public async Task Stopping_returns_only_once_the_run_has_actually_stopped()
    {
        GatedSubmitSink gate = new(_connections.Sink, gateAt: 1);
        GatedConnectionFactory connections = new(_connections, gate);
        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, connections: connections);

        run.Start(request);

        // Gated exactly as the cancellation tests above, and for the same reason: calling StopAsync
        // the instant Start() returns races VaultSigner.CreateAsync's own reveal, and a run that
        // finishes on its own also ends with stopped == true and State != Running -- so an ungated
        // version of this test would pass whether or not StopAsync interrupted anything.
        //
        // StopAsync itself is started before the gate is released, not awaited immediately: it
        // calls Cancel() as its very first act, and only then waits, so starting it while the run
        // is still held at the gate and releasing right after captures the same "interrupted, not
        // outlasted" scenario without any real waiting either.
        await WaitForGateAsync(gate, run);
        Task<bool> stopping = run.StopAsync(TimeSpan.FromMinutes(1));
        gate.Release();
        bool stopped = await stopping;

        Assert.True(stopped);
        Assert.True(run.Completion.IsCompleted);

        // Cancelled, not merely "not Running": proof that StopAsync actually interrupted a run
        // still in flight, which "not Running" alone does not distinguish from a run StopAsync
        // simply waited out.
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.Equal(_connections.Connections, _connections.Disposals);
    }

    [Fact]
    public async Task Stopping_an_idle_run_is_immediate_and_true()
    {
        (PublicationRun run, _) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(await run.StopAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Stopping_returns_false_when_the_timeout_expires_with_the_run_still_going()
    {
        GatedSubmitSink gate = new(_connections.Sink, gateAt: 1);
        GatedConnectionFactory connections = new(_connections, gate);
        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, connections: connections);

        // The run is held at its very first submission, not looking at cancellation until released:
        // StopAsync's short timeout has to expire while it is genuinely stuck there, which is the
        // one branch of StopAsync nothing exercised before -- the branch that keeps a closing window
        // from hanging forever on a run that will not yield. No real delay is needed for this any
        // more: the gate itself is what holds the run, for as long as the test wants.
        run.Start(request);
        await WaitForGateAsync(gate, run);

        bool stopped = await run.StopAsync(TimeSpan.FromMilliseconds(50));

        // Wrapped in try/finally: the run is parked inside its own gated submission from this point
        // on, until gate.Release() runs. Without the finally, either assertion failing here would
        // leave that background thread blocked forever rather than just failing the test.
        try
        {
            Assert.False(stopped);
            Assert.Equal(PublicationRunState.Running, run.Snapshot.State);
        }
        finally
        {
            // Let the run actually finish, so nothing from this test is still running when the next
            // one starts: StopAsync already called Cancel(), so releasing the gate lets that take
            // effect.
            gate.Release();
        }

        await run.Completion;
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
    }

    /// <summary>
    /// A local <see cref="ILedgerSource"/> decorator that advances a <see cref="ManualTimeProvider"/>
    /// clock once at the start of every <see cref="AccountTransactionsAsync"/> scan — the call the
    /// read-back stage makes (through <c>XrplFileReader.BlockListAsync</c> and
    /// <c>XrplFileReader.BlockAsync</c>) and nothing else does. Deliberately not hung off
    /// <see cref="ILedgerSink.SubmitAsync"/>: every submission already signs, and
    /// <see cref="VaultSigner"/> already touches the idle lock on every signature (see its own
    /// remarks), so a clock tied to submissions would be touched by that alone and could never tell
    /// <see cref="PublicationRun"/>'s own touch apart from it. The read-back stage signs nothing, so
    /// it is the one stretch of a run where <c>PublicationRun.Observe</c>'s touch is the only
    /// thing standing between the idle window and a lock.
    /// </summary>
    private sealed class ClockAdvancingSource(ILedgerSource inner, ManualTimeProvider time, TimeSpan step) : ILedgerSource
    {
        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) =>
            inner.TransactionAsync(hash, cancellationToken);

        public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
            string account, long fromLedger, long toLedger, bool forward,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Once per call, before anything is yielded: what matters is how many times read-back
            // asked to scan, not how many transactions each scan happened to return.
            time.Advance(step);

            await foreach (LedgerTransaction transaction in inner
                .AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return transaction;
            }
        }

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) =>
            inner.EarliestLedgerAsync(cancellationToken);

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => inner.FeesAsync(cancellationToken);

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) =>
            inner.BalanceDropsAsync(account, cancellationToken);

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) =>
            inner.AccountStateAsync(account, cancellationToken);

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) =>
            inner.LiveTicketsAsync(account, cancellationToken);

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) =>
            inner.CurrentLedgerAsync(cancellationToken);

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) =>
            inner.DestinationAsync(account, cancellationToken);
    }

    /// <summary>
    /// A connection factory that hands out <see cref="FakeConnectionFactory"/>'s own ledger and sink,
    /// wrapped so every read-back scan also advances the manual clock. The honest way to put a
    /// decorator in the connection's path without giving <see cref="FakeConnectionFactory"/> a knob
    /// only one test needs.
    /// </summary>
    private sealed class ClockAdvancingConnectionFactory(FakeConnectionFactory inner, ManualTimeProvider time, TimeSpan step)
        : IConnectionFactory
    {
        public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
        {
            ILedgerConnection connection = await inner.ConnectAsync(network, cancellationToken).ConfigureAwait(false);
            return new Connection(connection, time, step);
        }

        private sealed class Connection(ILedgerConnection inner, ManualTimeProvider time, TimeSpan step) : ILedgerConnection
        {
            public ILedgerSource Source { get; } = new ClockAdvancingSource(inner.Source, time, step);

            public ILedgerSink Sink => inner.Sink;

            public string Url => inner.Url;

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    /// <summary>
    /// Discriminates on the touch, unlike the brief's own version of this test (which only asserted
    /// the run succeeded — something that would pass just as well with the touch deleted). What it
    /// discriminates is "touches" from "never touches" — no finer than that:
    /// <c>PublicationRun.Observe</c>'s own touch call is unconditional on every progress report it
    /// receives, so there is no distinct implementation between "touches" and "never touches" for a
    /// test to catch, and read-back reports progress once per block
    /// (<c>XrplFileWriter.PublishAsync</c>'s own "read-back" report, one per block), not once per
    /// scan or per submission.
    ///
    /// The idle window is five minutes, and every <see cref="ILedgerSource.AccountTransactionsAsync"/>
    /// scan the read-back stage makes moves the clock two minutes: under the window on its own, so a
    /// callback that touches after the scans that precede each "read-back" report — resetting the
    /// window before the next one starts — keeps every single step inside it. A two-block file
    /// (1,024,001 bytes) makes read-back scan three times: once for the block list (no report of its
    /// own) and once per block (one report each), so the window has to survive two such resets, not
    /// one, before the manifest's own signature. A callback that never touches at all lets the clock
    /// cross the five-minute mark on the third scan, before read-back's second "read-back" report
    /// ever fires, and the manifest's signature then finds a locked vault.
    ///
    /// Verified by temporarily removing the <c>autoLock?.Touch()</c> call in
    /// <c>PublicationRun.Observe</c>: with it gone this test's run ends
    /// <see cref="PublicationRunState.Failed"/> with a <see cref="VaultError.Locked"/> sentence
    /// instead of <see cref="PublicationRunState.Succeeded"/>, and with the call restored — as it is
    /// in this file — the run succeeds. That contrast is what makes the assertion below worth having.
    /// </summary>
    [Fact]
    public async Task Progress_touches_the_idle_lock()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), TestContext.Current.CancellationToken);
        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key", "standalone", wallet.ClassicAddress, wallet.Seed),
            TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[wallet.ClassicAddress] = 500_000_000;

        ManualTimeProvider time = new();
        await using VaultAutoLock autoLock = new(vault, TimeSpan.FromMinutes(5), time);

        ClockAdvancingConnectionFactory connections = new(_connections, time, TimeSpan.FromMinutes(2));

        PublicationRun run = new(connections, vault, Paths, History, autoLock);

        // 1,024,001 bytes: 1,025 chunks over two blocks (FileTree.BlockChunks is 1024), so the
        // read-back stage this test depends on has more than one block to scan and more than one
        // "read-back" report to touch on.
        run.Start(Requests.Fresh(
            new byte[1_024_001], "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1"));
        await run.Completion;

        // The run touched the lock during the phase that signs nothing, not only on the signatures
        // that came before it, so the window never actually elapsed even though that phase moved
        // the clock most of the way there three separate times.
        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.True(await vault.ListAsync(TestContext.Current.CancellationToken) is { Count: 1 });
    }

    [Fact]
    public async Task The_lease_keeps_the_vault_open_through_a_long_await_that_reports_nothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);
        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key", "standalone", wallet.ClassicAddress, wallet.Seed), token);
        _connections.Ledger.Balances[wallet.ClassicAddress] = 100_000_000;

        ManualTimeProvider time = new();
        await using VaultAutoLock autoLock = new(vault, TimeSpan.FromMinutes(5), time);
        GatedConnectFactory gated = new(_connections, gateAt: 1);
        PublicationRun run = new(gated, vault, Paths, History, autoLock, time);

        Assert.True(run.Start(Requests.Fresh(new byte[4096], "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1")));
        await gated.Reached;

        // Ten minutes inside one await, twice the idle window, with no progress report at all.
        for (int minute = 0; minute < 10; minute++) time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(vault.IsUnlocked);

        gated.Release();
        await run.Completion;
        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);

        // The lease ended with the run: the idle window applies again.
        time.Advance(TimeSpan.FromMinutes(6));
        await BatchTestWait.UntilAsync(() => !vault.IsUnlocked);
    }

    [Fact]
    public async Task A_running_ticketed_publication_says_so_on_its_snapshot()
    {
        GatedSubmitSink gate = new(_connections.Sink, gateAt: 1);
        GatedConnectionFactory connections = new(_connections, gate);
        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, size: 100_000, connections: connections);

        run.Start(request);
        await WaitForGateAsync(gate, run);

        RunSnapshot snapshot = run.Snapshot;
        run.Cancel();
        gate.Release();
        await run.Completion;

        Assert.True(snapshot.UsesTickets);
        Assert.Equal(200_000, snapshot.OwnerReserveDrops);
    }

    [Fact]
    public async Task A_running_publication_without_tickets_says_so_on_its_snapshot()
    {
        GatedSubmitSink gate = new(_connections.Sink, gateAt: 1);
        GatedConnectionFactory connections = new(_connections, gate);
        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, connections: connections);

        run.Start(request);
        await WaitForGateAsync(gate, run);

        RunSnapshot snapshot = run.Snapshot;
        run.Cancel();
        gate.Release();
        await run.Completion;

        Assert.False(snapshot.UsesTickets);
    }

    [Fact]
    public async Task Resetting_a_ticketed_run_clears_its_ticket_facts()
    {
        GatedSubmitSink gate = new(_connections.Sink, gateAt: 1);
        GatedConnectionFactory connections = new(_connections, gate);
        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, size: 100_000, connections: connections);

        run.Start(request);
        await WaitForGateAsync(gate, run);
        run.Cancel();
        gate.Release();
        await run.Completion;

        Assert.True(run.Snapshot.UsesTickets);

        run.Reset();

        RunSnapshot snapshot = run.Snapshot;
        Assert.Equal(PublicationRunState.Idle, snapshot.State);
        Assert.False(snapshot.UsesTickets);
        Assert.Equal(0, snapshot.OwnerReserveDrops);
    }

    /// <summary>
    /// Holds the <paramref name="gateAt"/>-th <see cref="ConnectAsync"/> until <see cref="Release"/>,
    /// so a test can look at a run that <see cref="PublicationRun.Start"/> has begun but whose
    /// estimate has not been taken yet: everything on its snapshot then came from Begin alone.
    /// </summary>
    private sealed class GatedConnectFactory(IConnectionFactory inner, int gateAt) : IConnectionFactory
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task Reached => _reached.Task;

        public void Release() => _release.TrySetResult();

        public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == gateAt)
            {
                _reached.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await inner.ConnectAsync(network, cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task A_run_started_after_a_ticketed_one_does_not_inherit_its_ticket_facts()
    {
        GatedSubmitSink submitGate = new(_connections.Sink, gateAt: 1);
        GatedConnectFactory connectGate = new(new GatedConnectionFactory(_connections, submitGate), gateAt: 2);
        (PublicationRun run, PublishRequest ticketed) =
            await ReadyAsync(TestContext.Current.CancellationToken, size: 100_000, connections: connectGate);

        run.Start(ticketed);
        await WaitForGateAsync(submitGate, run);
        run.Cancel();
        submitGate.Release();
        await run.Completion;
        Assert.True(run.Snapshot.UsesTickets);

        Assert.True(run.Start(ticketed with { Size = 4096, Source = new MemoryPublicationSource(new byte[4096]) }));

        Task reachedOrFinished = await Task.WhenAny(connectGate.Reached, run.Completion);
        if (!ReferenceEquals(reachedOrFinished, connectGate.Reached))
        {
            throw new InvalidOperationException($"the second run finished ({run.Snapshot.State}) before connecting");
        }

        RunSnapshot beforeEstimate;
        try
        {
            beforeEstimate = run.Snapshot;
        }
        finally
        {
            connectGate.Release();
        }

        await run.Completion;

        Assert.Equal(PublicationRunState.Running, beforeEstimate.State);
        Assert.False(beforeEstimate.UsesTickets);
        Assert.Equal(0, beforeEstimate.OwnerReserveDrops);
        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.False(run.Snapshot.UsesTickets);
    }
}
