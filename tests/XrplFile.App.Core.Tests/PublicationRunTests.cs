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

    private async Task<(PublicationRun Run, PublishRequest Request)> ReadyAsync(CancellationToken token, int size = 4096)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);

        // A real seed, so VaultSigner can derive the account the request names and sign with it.
        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key", "standalone", wallet.ClassicAddress, wallet.Seed),
            token);

        _connections.Ledger.Balances[wallet.ClassicAddress] = 100_000_000;

        PublicationRun run = new(_connections, vault, Paths, History);
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
    public async Task A_second_run_while_one_is_going_is_refused()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(request));
        Assert.False(run.Start(request));

        await run.Completion;
    }

    [Fact]
    public async Task A_cancelled_run_reports_cancelled_and_leaves_a_state_file()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken, size: 200_000);

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
        // Waiting for the first progress report fixes this deterministically: PublicationRun only
        // reports progress from inside PublicationSession.RunAsync, which starts strictly after
        // StartAsync has already saved the state file, and a 200,000-byte file batches into two
        // ticket batches of up to 100 memos each (PublishPolicy.TicketsPerBatch), so the run still
        // has a second batch of work left when the first batch's progress lands.
        await WaitForProgressAsync(run, TestContext.Current.CancellationToken);
        run.Cancel();

        await run.Completion;

        // The contrast this test is actually for: without the wait above (cancelling immediately),
        // the run ends Cancelled with no state file, which the assertion below alone would not
        // catch since it only checks Unfinished, not the state -- so both are asserted together.
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.NotEmpty(PublicationSession.Unfinished(Paths.StateDirectory));
    }

    /// <summary>
    /// Spins until <paramref name="run"/> has reported its first <see cref="PublishProgress"/>, or
    /// fails loudly rather than hanging forever if the run finishes (or dies) before ever reporting
    /// one -- which would otherwise turn a broken run into a test that never completes.
    /// </summary>
    private static async Task WaitForProgressAsync(PublicationRun run, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (run.Snapshot.Progress is null)
        {
            if (run.Completion.IsCompleted)
            {
                throw new InvalidOperationException(
                    $"the run finished ({run.Snapshot.State}) before reporting any progress, so there is no "
                    + "way left to cancel it mid-flight");
            }

            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    [Fact]
    public async Task A_cancelled_run_records_nothing_in_the_history()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken, size: 200_000);

        run.Start(request);
        run.Cancel();
        await run.Completion;

        Assert.Empty(History.Read());
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
    }

    [Fact]
    public async Task Stopping_returns_only_once_the_run_has_actually_stopped()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken, size: 200_000);

        run.Start(request);
        bool stopped = await run.StopAsync(TimeSpan.FromMinutes(1));

        Assert.True(stopped);
        Assert.True(run.Completion.IsCompleted);
        Assert.NotEqual(PublicationRunState.Running, run.Snapshot.State);
    }

    [Fact]
    public async Task Stopping_an_idle_run_is_immediate_and_true()
    {
        (PublicationRun run, _) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(await run.StopAsync(TimeSpan.FromSeconds(1)));
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
    /// the run succeeded — something that would pass just as well with the touch deleted).
    ///
    /// The idle window is five minutes, and every <see cref="ILedgerSource.AccountTransactionsAsync"/>
    /// scan the read-back stage makes moves the clock two minutes: under the window on its own, so a
    /// callback that touches on every "read-back" progress report — resetting the window right after
    /// each scan — keeps every single step inside it. A two-block file (1,024,001 bytes) makes
    /// read-back scan three times: once for the block list and once per block, so there are two
    /// touch opportunities the callback has to take, not one, before the manifest's own signature.
    /// A callback that touched only on the first one, or not at all, lets the clock cross the
    /// five-minute mark on the third scan — before that scan's own "read-back" report ever fires —
    /// and the manifest's signature then finds a locked vault.
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

        // The run touched the lock on every "read-back" progress report, not only on the signatures
        // that came before it, so the window never actually elapsed even though the phase that
        // signs nothing moved the clock most of the way there three separate times.
        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.True(await vault.ListAsync(TestContext.Current.CancellationToken) is { Count: 1 });
    }
}
