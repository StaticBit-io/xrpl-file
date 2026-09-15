using System.Runtime.CompilerServices;

using Xrpl.Wallet;

using XrplFile.Keys;
using XrplFile.Tests;

using Xunit;

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
        CancellationToken token, int size = 4096, IConnectionFactory? connections = null, PublicationHistory? history = null)
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
        PublishRequest request = new(
            new byte[size], "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1");

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
        Assert.Null(snapshot.HistoryFailure);
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
        Assert.NotNull(snapshot.HistoryFailure);
    }

    [Fact]
    public async Task An_exception_from_appending_to_the_history_keeps_its_own_message()
    {
        // PublicationHistory.Save only degrades on IOException/UnauthorizedAccessException; anything
        // else escapes Append uncaught, which is what RunAsync's own last-resort catch around the
        // append call exists for. "C:\" is a genuine drive root, not a made-up path: Path.GetDirectoryName
        // returns null for one (there is no parent directory of a root), and Save passes that null
        // straight into Directory.CreateDirectory, which throws ArgumentNullException -- a type
        // neither Save nor Append catches, and nothing here ever actually touches the real "C:\".
        PublicationHistory badHistory = new(@"C:\");

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
        Assert.NotNull(snapshot.HistoryFailure);
        Assert.Contains("Value cannot be null", snapshot.HistoryFailure, StringComparison.Ordinal);
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
        // Waiting for gate.Reached fixes this deterministically and without polling: it resolves
        // the instant the run's first submission begins, which is necessarily after
        // PublicationSession.StartAsync has already saved the state file (RunAsync -- and every
        // submission inside it -- only starts once StartAsync has returned). Unlike polling
        // Snapshot.Progress, this is not a lagging indicator: Progress is reported through
        // System.Progress<int>, which posts to the thread pool, so by the time a poll observes it
        // the run may already be further along than the read implies -- on a contended machine, far
        // enough along to have finished. The gate holds the run at exactly this point until
        // Release() is called, so there is nothing left to race.
        await gate.Reached;
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
        // gate.Reached) -- which can never reach history.Append under any implementation, correct
        // or broken, so it proves nothing about the thing this test names. Waiting for the gate
        // cancels a run that is actually publishing.
        await gate.Reached;
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
        // An empty file: XrplFileWriter.PublishAsync throws a plain ArgumentException for one
        // ("an empty file has no chunks and no root"), which is none of the four types RunAsync's
        // catch clauses name. Before the catch-all this reached, that fault RunAsync's Completion,
        // left _state stuck at Running forever, and took Reset, every future Start, and StopAsync's
        // "waits then reports success" with it -- recoverable only by restarting the application.
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken, size: 0);

        Assert.True(run.Start(request));
        await run.Completion;

        RunSnapshot snapshot = run.Snapshot;
        Assert.Equal(PublicationRunState.Failed, snapshot.State);
        Assert.NotNull(snapshot.Failure);

        // This is the one failure test where both a connection and a strategy genuinely exist when
        // the failure lands (the node-unreachable and no-such-entry tests below each stop short of
        // one or the other), which makes it the strongest place in this file to check that neither
        // leaked.
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
        Assert.Equal(PublishErrorMessages.For(XrplFileError.NodeUnreachable), run.Snapshot.Failure);

        // No connection was ever handed back to leak: ConnectAsync fails before FakeConnectionFactory
        // counts one, so this holds at 0 == 0 -- still the same invariant a run that failed after
        // connecting would have to uphold at a higher count.
        Assert.Equal(_connections.Connections, _connections.Disposals);
    }

    [Fact]
    public async Task A_failure_after_connecting_still_disposes_the_connection()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);

        // A reference the vault never stored: VaultSigner.CreateAsync's RevealAsync throws
        // VaultError.NoSuchEntry -- after ConnectAsync has already handed back a real connection,
        // unlike the sibling test above where the node itself refuses. This is the path fix 8 is
        // actually about: one where a connection genuinely opened before the run failed.
        PublishRequest broken = request with { KeyReference = "not-a-real-reference" };

        run.Start(broken);
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal(VaultErrorMessages.For(VaultError.NoSuchEntry), run.Snapshot.Failure);
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
        await gate.Reached;
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

    /// <summary>
    /// An <see cref="ILedgerSink"/> that holds its <paramref name="gateAt"/>-th
    /// <see cref="SubmitAsync"/> call open until <see cref="Release"/> is called, deliberately not
    /// looking at the transaction's own <see cref="CancellationToken"/> until then — standing in for
    /// a submission already in flight against a real socket, which <see cref="PublicationRun.Cancel"/>
    /// cannot recall until the socket actually answers. <see cref="Reached"/> resolves the instant
    /// that call begins, before it waits, so a test can know the run is held at exactly that point
    /// without polling anything or waiting on the wall clock.
    ///
    /// Replaces an earlier version of this double that instead slept a fixed real delay: that made
    /// the one test using it cost two real seconds and, more importantly, made every gated
    /// cancellation test in this file vulnerable to the same problem <see cref="Reached"/> exists to
    /// avoid — a poll of <see cref="RunSnapshot.Progress"/> is a lagging indicator (progress is
    /// reported through <see cref="System.Progress{T}"/>, which posts to the thread pool rather than
    /// calling inline), so on a contended machine the run can finish before a polling loop ever
    /// observes the report it was waiting for.
    /// </summary>
    private sealed class GatedSubmitSink(ILedgerSink inner, int gateAt) : ILedgerSink
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        /// <summary>Resolves the instant the gated submission begins, before it waits to be released.</summary>
        public Task Reached => _reached.Task;

        /// <summary>Lets the gated submission proceed.</summary>
        public void Release() => _release.TrySetResult();

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public async Task<SubmitOutcome> SubmitAsync(
            Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == gateAt)
            {
                _reached.TrySetResult();

                // Deliberately not awaited against cancellationToken: the test decides exactly when
                // this proceeds by calling Release(), so nothing here depends on real time or on
                // how fast the fake ledger happens to run.
                await _release.Task.ConfigureAwait(false);

                // Once released, honour cancellation exactly where a real submission's own checks
                // would, rather than silently carrying on with a transaction nobody wants sent any
                // more -- this is what lets a test call Cancel() while the run is held here and see
                // it actually take effect once Release() lets the run notice.
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await inner.SubmitAsync(transaction, signer, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A connection factory that hands out <see cref="FakeConnectionFactory"/>'s own ledger, wrapped
    /// so its sink's submissions go through a <see cref="GatedSubmitSink"/>. A test-local type for
    /// the same reason the clock-advancing pair below are: one test's need, not a knob on the shared
    /// double.
    /// </summary>
    private sealed class GatedConnectionFactory(FakeConnectionFactory inner, GatedSubmitSink sink) : IConnectionFactory
    {
        public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
        {
            ILedgerConnection connection = await inner.ConnectAsync(network, cancellationToken).ConfigureAwait(false);
            return new Connection(connection, sink);
        }

        private sealed class Connection(ILedgerConnection inner, ILedgerSink sink) : ILedgerConnection
        {
            public ILedgerSource Source => inner.Source;

            public ILedgerSink Sink => sink;

            public string Url => inner.Url;

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
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
        await gate.Reached;

        bool stopped = await run.StopAsync(TimeSpan.FromMilliseconds(50));

        Assert.False(stopped);
        Assert.Equal(PublicationRunState.Running, run.Snapshot.State);

        // Let the run actually finish, so nothing from this test is still running when the next one
        // starts: StopAsync already called Cancel(), so releasing the gate lets that take effect.
        gate.Release();
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

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) =>
            inner.CurrentLedgerAsync(cancellationToken);
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
        run.Start(new PublishRequest(
            new byte[1_024_001], "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1"));
        await run.Completion;

        // The run touched the lock during the phase that signs nothing, not only on the signatures
        // that came before it, so the window never actually elapsed even though that phase moved
        // the clock most of the way there three separate times.
        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.True(await vault.ListAsync(TestContext.Current.CancellationToken) is { Count: 1 });
    }
}
