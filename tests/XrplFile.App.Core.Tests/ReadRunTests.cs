using System.Runtime.CompilerServices;

using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class ReadRunTests
{
    private readonly FakeConnectionFactory _connections = new();
    private readonly FakeFilePicker _picker = new();

    private async Task<(ReadRun Run, XrplFileLink Link, ManifestRecord Manifest)> ReadyAsync(
        CancellationToken token, string name = "photo.png", int size = 4096)
    {
        XrplFileLink link = await PublishAsync(_connections, name, "image/png", size);

        await using ILedgerConnection connection = await _connections.ConnectAsync(link.Network, token);
        ManifestRecord manifest = await new XrplFileReader(connection.Source).ManifestAsync(link, token);

        return (new ReadRun(_connections, _picker), link, manifest);
    }

    [Fact]
    public async Task A_read_starts_idle()
    {
        (ReadRun run, _, _) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReadState.Idle, run.Snapshot.State);
    }

    [Fact]
    public async Task A_read_succeeds_and_reports_progress()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        ReadSnapshot snapshot = run.Snapshot;
        Assert.Equal(ReadState.Succeeded, snapshot.State);
        Assert.Equal(snapshot.BlocksTotal, snapshot.BlocksDone);
        Assert.Equal(4096, snapshot.BytesDone);
        Assert.Equal("photo.png", snapshot.Name);
        Assert.Null(snapshot.Failure);
    }

    [Fact]
    public async Task What_was_read_is_byte_for_byte_what_the_library_reads()
    {
        // The differential test (design 3.1). Four things moved out of XrplFileReader.ReadAsync and
        // into this run: the ceiling, the history check, the assembly and the final length. If the
        // application ever assembles a file differently from the reference implementation, this is
        // what says so.
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(
            TestContext.Current.CancellationToken, size: 40_960);

        run.Start(link, manifest);
        await run.Completion;
        await run.SaveAsync(TestContext.Current.CancellationToken);

        await using ILedgerConnection connection =
            await _connections.ConnectAsync(link.Network, TestContext.Current.CancellationToken);
        ReadResult expected = await new XrplFileReader(connection.Source)
            .ReadAsync(link, TestContext.Current.CancellationToken);

        Assert.Equal(expected.Bytes, _picker.SavedStream);
    }

    [Fact]
    public async Task Saving_suggests_the_name_from_the_manifest()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        run.Start(link, manifest);
        await run.Completion;
        await run.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal("photo.png", _picker.SavedStreamName);
        Assert.True(run.Snapshot.Saved);
    }

    [Fact]
    public async Task A_file_published_with_no_name_still_gets_a_suggestion()
    {
        XrplFileLink link = await PublishAsync(_connections, name: null, "application/octet-stream", 2048);

        await using ILedgerConnection connection =
            await _connections.ConnectAsync(link.Network, TestContext.Current.CancellationToken);
        ManifestRecord manifest = await new XrplFileReader(connection.Source)
            .ManifestAsync(link, TestContext.Current.CancellationToken);

        ReadRun run = new(_connections, _picker);
        run.Start(link, manifest);
        await run.Completion;
        await run.SaveAsync(TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(_picker.SavedStreamName));
    }

    [Fact]
    public async Task A_second_read_while_one_is_going_is_refused()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(link, manifest));
        Assert.False(run.Start(link, manifest));

        await run.Completion;
    }

    [Fact]
    public async Task A_node_whose_history_is_too_short_refuses_before_any_block_is_fetched()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        // The manifest names the ledger the file's first chunk was written in. A node that has
        // pruned past it cannot answer, and finding that out one block at a time wastes the
        // person's time.
        _connections.Ledger.EarliestLedger = manifest.Manifest.FirstLedger + 1;

        run.Start(link, manifest);
        await run.Completion;

        Assert.Equal(ReadState.Failed, run.Snapshot.State);
        Assert.Equal(ReadErrorMessages.For(XrplFileError.NodeLacksHistory), run.Snapshot.Failure);
    }

    [Fact]
    public async Task Nothing_is_left_on_disk_after_a_run_is_reset()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        run.Start(link, manifest);
        await run.Completion;

        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        run.Reset();

        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task Stopping_deletes_the_temporary_file_too()
    {
        // Not gated: over this fixture's 4096-byte, single-block file nothing the run touches
        // before finishing ever actually observes the cancellation token (FakeConnectionFactory and
        // FakeLedgerSource check none of their own calls against it), so the run always reaches
        // Succeeded before StopAsync's wait returns -- the same fact the brief's own note next to
        // this file's cancellation test calls out. What this proves instead: StopAsync's own Reset()
        // deletes whatever a run -- however it ended -- left on disk, exactly as a direct Reset()
        // call does in the test above. The temporary path has to be read while the file is known to
        // still exist, before StopAsync's Reset() clears it back to null: reading it afterward and
        // falling back to Path.GetTempFileName() on null would create a brand-new, genuinely
        // existing temporary file and make the assertion below pass for the wrong reason.
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        run.Start(link, manifest);
        await run.Completion;

        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        await run.StopAsync(TimeSpan.FromMinutes(1));

        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task Cancelling_while_a_block_is_being_fetched_stops_the_run_and_cleans_up()
    {
        // Made deterministic the way PublicationRunTests does it (GatedSubmitSink): a decorator over
        // ILedgerSource holds the run's one AccountTransactionsAsync call -- the call
        // XrplFileReader.BlockAsync makes to fetch the single block this fixture's file has -- open
        // until the test releases it, so Cancel() is known to land on a run that is genuinely still
        // going, never on one that had already finished.
        (ReadRun _, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        GatedAccountTransactionsSource gate = new(_connections.Ledger);
        ReadRun run = new(new GatedReadConnectionFactory(_connections, gate), _picker);

        run.Start(link, manifest);
        await gate.Reached;

        // The block list has already been fetched and verified, and the temporary file already
        // exists, by the time the gate is reached -- proving this cancels a download in progress,
        // not one that never started.
        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        run.Cancel();
        gate.Release();

        await run.Completion;

        Assert.Equal(ReadState.Cancelled, run.Snapshot.State);
        Assert.False(File.Exists(temporary));
    }

    /// <summary>
    /// Holds the <paramref name="gateAt"/>-th <see cref="AccountTransactionsAsync"/> call open until
    /// <see cref="Release"/> is called, mirroring <c>GatedSubmitSink</c>: deliberately not looking at
    /// the caller's <see cref="CancellationToken"/> until then, so a test decides exactly when the
    /// gate lets go rather than racing real time.
    /// </summary>
    private sealed class GatedAccountTransactionsSource(ILedgerSource inner, int gateAt = 1) : ILedgerSource
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        /// <summary>Resolves the instant the gated call begins, before it waits to be released.</summary>
        public Task Reached => _reached.Task;

        /// <summary>Lets the gated call proceed.</summary>
        public void Release() => _release.TrySetResult();

        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) =>
            inner.TransactionAsync(hash, cancellationToken);

        public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
            string account, long fromLedger, long toLedger, bool forward,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == gateAt)
            {
                _reached.TrySetResult();

                // Not awaited against cancellationToken: the test decides exactly when this
                // proceeds by calling Release().
                await _release.Task.ConfigureAwait(false);

                // Once released, honour cancellation exactly where a real scan's own checks would --
                // this is what lets a test call Cancel() while the run is held here and see it
                // actually take effect once Release() lets the run notice.
                cancellationToken.ThrowIfCancellationRequested();
            }

            await foreach (LedgerTransaction tx in inner
                .AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return tx;
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

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) =>
            inner.DestinationAsync(account, cancellationToken);
    }

    /// <summary>
    /// A connection factory that hands out <see cref="FakeConnectionFactory"/>'s own ledger and sink,
    /// wrapped so its source's account-transaction scans go through a
    /// <see cref="GatedAccountTransactionsSource"/>.
    /// </summary>
    private sealed class GatedReadConnectionFactory(FakeConnectionFactory inner, GatedAccountTransactionsSource source)
        : IConnectionFactory
    {
        public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
        {
            ILedgerConnection connection = await inner.ConnectAsync(network, cancellationToken).ConfigureAwait(false);
            return new Connection(connection, source);
        }

        private sealed class Connection(ILedgerConnection inner, ILedgerSource source) : ILedgerConnection
        {
            public ILedgerSource Source => source;

            public ILedgerSink Sink => inner.Sink;

            public string Url => inner.Url;

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
