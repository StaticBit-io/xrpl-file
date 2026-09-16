using System.Runtime.CompilerServices;

using Xunit;

namespace XrplFile.App.Core.Tests;

// See HeavySigningCollection.cs: this class's differential test signs roughly a thousand chunk
// transactions and reliably contends with PublicationRunTests' own heavy test when both run in
// xUnit's default parallel collections.
[Collection("HeavySigning")]
public sealed class ReadRunTests : IDisposable
{
    private readonly FakeConnectionFactory _connections = new();
    private readonly FakeFilePicker _picker = new();

    /// <summary>
    /// Every <see cref="ReadRun"/> this class has constructed, so <see cref="Dispose"/> can put each
    /// one away. A succeeded run's temporary file survives until something resets it --
    /// <see cref="ReadRun.Reset"/>, <see cref="ReadRun.StopAsync"/> or a following
    /// <see cref="ReadRun.Start"/> -- and most of these tests end on
    /// Succeeded without calling any of those themselves, which otherwise leaves one
    /// "xrpl-file-read-*" directory behind per test, every run, forever.
    /// </summary>
    private readonly List<ReadRun> _runs = [];

    private ReadRun Track(ReadRun run)
    {
        _runs.Add(run);
        return run;
    }

    public void Dispose()
    {
        foreach (ReadRun run in _runs)
        {
            try
            {
                run.Reset();
            }
            catch (Exception)
            {
                // ReadRun.Reset already swallows its own cleanup failures (DeleteTemporary's catch
                // is unconditional); this exists only in case a future change reintroduces one that
                // does not, so a flaky teardown here can never replace whatever the test itself
                // actually failed on -- the same reasoning PublishedFileFixture's own guarded cleanup
                // follows.
            }
        }
    }

    private async Task<(ReadRun Run, XrplFileLink Link, ManifestRecord Manifest)> ReadyAsync(
        CancellationToken token, string name = "photo.png", int size = 4096)
    {
        XrplFileLink link = await PublishAsync(_connections, name, "image/png", size);

        await using ILedgerConnection connection = await _connections.ConnectAsync(link.Network, token);
        ManifestRecord manifest = await new XrplFileReader(connection.Source).ManifestAsync(link, token);

        return (Track(new ReadRun(_connections, _picker)), link, manifest);
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
        Assert.Equal(4096, snapshot.BytesTotal);
        Assert.Equal("photo.png", snapshot.Name);
        Assert.Null(snapshot.Failure);
    }

    [Fact]
    public async Task What_was_read_is_byte_for_byte_what_the_library_reads()
    {
        // The differential test (design 3.1). Four things moved out of XrplFileReader.ReadAsync and
        // into this run: the ceiling, the history check, the assembly and the final length. If the
        // application ever assembles a file differently from the reference implementation, this is
        // what says so -- but only if the file genuinely has more than one block. With Blocks == 1,
        // XrplFileReader.BlockListAsync short-circuits identically on both sides (no block-list memos
        // are even read), and BlockAsync is then called exactly once on each side, comparing a single
        // array against itself regardless of how -- or whether -- the assembly loop actually works.
        // A block is 1024 chunks of up to 1000 bytes each (MemoLayout.ChunkBytes), so the smallest
        // file with more than one block is 1,024,001 bytes. 1_100_437 clears that with room to spare
        // (1101 chunks, the last one ragged at 437 bytes, across two blocks: 1024 full chunks and 77)
        // while keeping the fixture's chunk-transaction count close to the coordinator's own "about a
        // thousand" estimate -- see the report for the measured publish cost.
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(
            TestContext.Current.CancellationToken, size: 1_100_437);

        // Asserted before the comparison, not only implied by the size above: if this fixture ever
        // drifted back to one block in some future edit, the comparison below would keep passing,
        // silently vacuous again -- which is exactly how it got here the first time.
        Assert.True(
            manifest.Manifest.Blocks > 1,
            "the fixture must produce more than one block, or this test cannot detect an assembly-order defect");

        Assert.True(run.Start(link, manifest));
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

        Assert.True(run.Start(link, manifest));
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

        ReadRun run = Track(new ReadRun(_connections, _picker));
        Assert.True(run.Start(link, manifest));
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
    public async Task A_manifest_larger_than_the_ceiling_is_refused_before_any_block_is_fetched()
    {
        // The first of the four things design 3.1 moves out of ReadAsync. ReadModel.LookUpAsync
        // already refuses an oversized manifest before a person is ever offered a read button
        // (ReadModel.cs) -- this proves ReadRun.Start is correct on its own terms too, since it is
        // public and can be handed a ManifestRecord from anywhere else, not only from ReadModel.
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        // Built with `with` rather than by publishing an oversized file for real: Manifest and
        // ManifestRecord are both records, and nothing about the ceiling check depends on the bytes
        // actually existing in the ledger -- it runs before a connection is even opened.
        ManifestRecord oversized = manifest with
        {
            Manifest = manifest.Manifest with { Size = new ReaderOptions().MaxBytes + 1 },
        };

        int connectionsBefore = _connections.Connections;

        Assert.True(run.Start(link, oversized));
        await run.Completion;

        ReadSnapshot snapshot = run.Snapshot;
        Assert.Equal(ReadState.Failed, snapshot.State);
        Assert.Equal(ReadErrorMessages.For(XrplFileError.TooLarge), snapshot.Failure);

        // Refused before ConnectAsync, not merely before a block: no new connection was opened for
        // this run at all.
        Assert.Equal(connectionsBefore, _connections.Connections);
        Assert.Null(run.TemporaryPathForTests);
    }

    [Fact]
    public async Task A_node_whose_history_is_too_short_refuses_before_any_block_is_fetched()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        // The manifest names the ledger the file's first chunk was written in. A node that has
        // pruned past it cannot answer, and finding that out one block at a time wastes the
        // person's time.
        _connections.Ledger.EarliestLedger = manifest.Manifest.FirstLedger + 1;

        int callsBefore = _connections.Ledger.AccountTransactionsCalls;

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        ReadSnapshot snapshot = run.Snapshot;
        Assert.Equal(ReadState.Failed, snapshot.State);
        Assert.Equal(ReadErrorMessages.For(XrplFileError.NodeLacksHistory), snapshot.Failure);

        // "Before any block is fetched" is what the test's own name claims: proven here, not merely
        // implied by the code's ordering -- no block-list or block scan of the ledger ever happened,
        // and nothing was written to disk or counted as progress.
        Assert.Equal(callsBefore, _connections.Ledger.AccountTransactionsCalls);
        Assert.Null(run.TemporaryPathForTests);
        Assert.Equal(0, snapshot.BlocksDone);
    }

    [Fact]
    public async Task Nothing_is_left_on_disk_after_a_run_is_reset()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        run.Reset();

        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task Nothing_is_left_on_disk_after_a_run_is_disposed()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        run.Dispose();

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
        // call does in the test above. The genuinely in-flight cancel-and-wait path StopAsync exists
        // for is covered separately below, gated the same way the cancellation test is.
        //
        // The temporary path has to be read while the file is known to still exist, before
        // StopAsync's Reset() clears it back to null: reading it afterward and falling back to
        // Path.GetTempFileName() on null would create a brand-new, genuinely existing temporary file
        // and make the assertion below pass for the wrong reason.
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        await run.StopAsync(TimeSpan.FromMinutes(1));

        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task Stopping_a_read_that_is_genuinely_in_flight_cancels_it_and_cleans_up()
    {
        // The window-close path StopAsync exists for: unlike the test above, this one is held open
        // by the same gate the cancellation test uses, so StopAsync's own cancel-and-wait branch
        // (Task.WhenAny over the run's Completion and a timeout) is the one actually exercised here,
        // not merely the fast path over an already-finished run.
        (ReadRun _, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        GatedAccountTransactionsSource gate = new(_connections.Ledger);
        ReadRun run = Track(new ReadRun(new GatedReadConnectionFactory(_connections, gate), _picker));

        Assert.True(run.Start(link, manifest));
        await gate.Reached;

        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        // StopAsync's own first act, synchronously before its first await, is to call Cancel() --
        // so by the time this returns a Task<bool>, the token held at the gate has already been
        // cancelled, and releasing the gate right away is enough to let it proceed and see that.
        Task<bool> stopping = run.StopAsync(TimeSpan.FromSeconds(30));
        gate.Release();

        // Not asserted here: run.Snapshot.State == Cancelled. StopAsync's own success path calls
        // Reset() immediately once the run finishes, which publishes Idle over whatever RunAsync's
        // own terminal block just set -- there is no await point between the two a test could
        // observe from outside. What proves the run was genuinely cancelled rather than merely
        // having raced to a natural finish is the gate itself: it does not let RunAsync past the
        // fetch at all until Release() runs, and Release() here only runs after Cancel() already
        // has, so RunAsync's own cancellationToken.ThrowIfCancellationRequested() is certain to fire
        // -- the same reasoning the plain Cancel()-based test above relies on.
        Assert.True(await stopping);
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task Stopping_returns_false_when_the_timeout_expires_with_the_read_still_going()
    {
        (ReadRun _, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        GatedAccountTransactionsSource gate = new(_connections.Ledger);
        ReadRun run = Track(new ReadRun(new GatedReadConnectionFactory(_connections, gate), _picker));

        Assert.True(run.Start(link, manifest));
        await gate.Reached;

        // Wrapped in try/finally: the run is parked inside the gated fetch from this point on, until
        // gate.Release() runs. Without the finally, either assertion failing here would leave that
        // background task blocked forever rather than just failing the test.
        try
        {
            bool stopped = await run.StopAsync(TimeSpan.FromMilliseconds(50));

            Assert.False(stopped);
            Assert.Equal(ReadState.Running, run.Snapshot.State);
        }
        finally
        {
            gate.Release();
        }

        await run.Completion;
        Assert.Equal(ReadState.Cancelled, run.Snapshot.State);
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
        ReadRun run = Track(new ReadRun(new GatedReadConnectionFactory(_connections, gate), _picker));

        Assert.True(run.Start(link, manifest));
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
