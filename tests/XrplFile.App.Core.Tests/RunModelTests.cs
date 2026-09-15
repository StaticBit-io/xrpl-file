using Xrpl.Wallet;

using XrplFile.Keys;
using XrplFile.Tests;

using Xunit;

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
        RunModel model = new(NewRun());

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
        RunModel model = new(NewRun());

        Assert.False(model.CanCancel);
    }

    [Fact]
    public void An_idle_run_has_no_link_failure_or_history_note()
    {
        RunModel model = new(NewRun());

        Assert.Null(model.Link);
        Assert.Null(model.Failure);
        Assert.Null(model.HistoryNote);
    }

    [Fact]
    public void The_stage_text_says_starting_before_any_progress_is_reported()
    {
        RunModel model = new(NewRun());

        Assert.Equal("Starting", model.StageText);
    }

    [Fact]
    public void An_idle_run_is_not_active_and_was_not_cancelled()
    {
        RunModel model = new(NewRun());

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
        RunModel model = new(run);

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
        RunModel model = new(run);

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

    private static PublicationRun NewRun()
    {
        string directory = Directory.CreateTempSubdirectory("xrpl-file-runmodel-").FullName;
        RunPaths paths = new(directory);
        return new PublicationRun(
            new FakeConnectionFactory(), new NeverVault(), paths, new PublicationHistory(paths.HistoryPath));
    }

    /// <summary>
    /// A real, funded vault entry and a matching request — everything a run needs to actually leave
    /// Idle. Mirrors <c>PublicationRunTests.ReadyAsync</c>; not shared with it for the same reason
    /// <c>UnfinishedModelTests</c> keeps its own copy of the gated doubles below rather than
    /// promoting either to something shared.
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
        PublishRequest request = new(
            new byte[4096], "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1");

        return (run, request);
    }

    /// <summary>
    /// Copied rather than shared with <c>PublicationRunTests.GatedSubmitSink</c> — see that class's
    /// own remarks for why the gate exists at all (a bare cancel racing <c>PublicationSession.StartAsync</c>
    /// itself is not deterministic) and <c>UnfinishedModelTests</c>'s own remarks for why this
    /// repository copies this particular double per file rather than promoting it to something shared.
    /// </summary>
    private sealed class GatedSubmitSink(ILedgerSink inner, int gateAt) : ILedgerSink
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task Reached => _reached.Task;

        public void Release() => _release.TrySetResult();

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public async Task<SubmitOutcome> SubmitAsync(
            Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == gateAt)
            {
                _reached.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await inner.SubmitAsync(transaction, signer, cancellationToken).ConfigureAwait(false);
        }
    }

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

    private static async Task WaitForGateAsync(GatedSubmitSink gate, PublicationRun run)
    {
        Task finished = await Task.WhenAny(gate.Reached, run.Completion).ConfigureAwait(false);

        if (!ReferenceEquals(finished, gate.Reached))
        {
            throw new InvalidOperationException(
                $"the run finished ({run.Snapshot.State}) before reaching the gated submission, so there is "
                + "nothing left to release");
        }
    }
}
