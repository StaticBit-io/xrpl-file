using System.Text;

using Xrpl.Wallet;

using XrplFile.Keys;

using Xunit;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// Inherits <see cref="VaultHarness"/> rather than plain <see cref="IDisposable"/>: most of these
/// tests never start a run and are content with <see cref="NeverVault"/>, but
/// <see cref="An_interrupted_readable_run_resumes_with_the_same_bytes"/> needs a real, working
/// <see cref="IKeyVault"/> to actually publish and then resume -- the harness is what
/// <see cref="PublicationRunTests"/> uses for exactly that reason, and this class needs the same
/// thing for the same reason.
/// </summary>
public sealed class UnfinishedModelTests : VaultHarness
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-unfinished-").FullName;
    private readonly FakeFilePicker _picker = new();

    private RunPaths Paths => new(_directory);

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// A run that is never actually started or resumed in the test that calls this -- safe to build
    /// over <see cref="NeverVault"/>, which throws on every member.
    /// </summary>
    private PublicationRun NewRun()
    {
        RunPaths paths = Paths;
        return new PublicationRun(
            new FakeConnectionFactory(), new NeverVault(), paths, new PublicationHistory(paths.HistoryPath));
    }

    private void WriteUnreadableState(string fileName)
    {
        Directory.CreateDirectory(Paths.StateDirectory);
        File.WriteAllBytes(Path.Combine(Paths.StateDirectory, fileName), Encoding.UTF8.GetBytes("not a state file"));
    }

    [Fact]
    public void With_no_state_directory_there_are_no_rows_and_no_crash()
    {
        UnfinishedModel model = new(Paths, NewRun(), _picker);
        model.Refresh();

        Assert.Empty(model.Rows);
    }

    [Fact]
    public void A_state_file_that_cannot_be_read_is_still_listed_by_its_path()
    {
        WriteUnreadableState("broken.json");

        UnfinishedModel model = new(Paths, NewRun(), _picker);
        model.Refresh();

        UnfinishedRow row = Assert.Single(model.Rows);
        Assert.NotNull(row.Unreadable);
        Assert.Contains("broken.json", row.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_run_cannot_be_resumed_and_says_why()
    {
        WriteUnreadableState("broken.json");

        UnfinishedModel model = new(Paths, NewRun(), _picker);
        model.Refresh();

        Assert.False(await model.ResumeAsync(model.Rows[0], TestContext.Current.CancellationToken));
        Assert.NotNull(model.Message);
    }

    [Fact]
    public void Deleting_a_state_file_removes_the_row()
    {
        WriteUnreadableState("broken.json");

        UnfinishedModel model = new(Paths, NewRun(), _picker);
        model.Refresh();
        model.Delete(model.Rows[0]);

        Assert.Empty(model.Rows);
        Assert.Empty(Directory.GetFiles(Paths.StateDirectory));
    }

    [Fact]
    public async Task Resuming_with_no_file_chosen_does_nothing()
    {
        WriteUnreadableState("broken.json");
        _picker.PathToReturn = null;

        UnfinishedModel model = new(Paths, NewRun(), _picker);
        model.Refresh();

        Assert.False(await model.ResumeAsync(model.Rows[0], TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The only proof the resume path is actually wired end to end, not just refused for the reasons
    /// covered above. Builds a real interrupted run the way
    /// <c>PublicationRunTests.A_cancelled_run_reports_cancelled_and_leaves_a_state_file</c> does --
    /// gating the first submission so the run is cancelled only once
    /// <see cref="PublicationSession.StartAsync"/> has already written the state file, rather than
    /// racing it -- then points a fresh <see cref="UnfinishedModel"/> at the same <see cref="RunPaths"/>
    /// and resumes it, on the very same <see cref="PublicationRun"/> the interrupted run ran on
    /// (exactly what <see cref="PublicationRun.Resume"/> is for: the same instance, asked to pick a
    /// run back up). The gate is reused rather than rebuilt for the resumed submissions: its
    /// <c>gateAt: 1</c> only ever holds the first call across the sink's whole lifetime, so every
    /// submission the resumed run makes after that sails through ungated, and the run is free to run
    /// all the way to <see cref="PublicationRunState.Succeeded"/> -- stronger proof than stopping at
    /// "returned true and is Running" alone, though that is the minimum this test is required to show.
    /// </summary>
    [Fact]
    public async Task An_interrupted_readable_run_resumes_with_the_same_bytes()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), TestContext.Current.CancellationToken);

        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key", "standalone", wallet.ClassicAddress, wallet.Seed),
            TestContext.Current.CancellationToken);

        FakeConnectionFactory connections = new();
        connections.Ledger.Balances[wallet.ClassicAddress] = 100_000_000;

        GatedSubmitSink gate = new(connections.Sink, gateAt: 1);
        GatedConnectionFactory gated = new(connections, gate);

        RunPaths paths = Paths;
        PublicationRun run = new(gated, vault, paths, new PublicationHistory(paths.HistoryPath));

        byte[] bytes = new byte[4096];
        PublishRequest request = new(
            bytes, "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1");

        Assert.True(run.Start(request));

        // See PublicationRunTests' own remarks on why this wait, not an immediate Cancel(), is what
        // guarantees a state file actually exists to resume from.
        await WaitForGateAsync(gate, run);
        run.Cancel();
        gate.Release();

        await run.Completion;
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);

        IReadOnlyList<UnfinishedRun> unfinishedBeforeResume = PublicationSession.Unfinished(paths.StateDirectory);
        UnfinishedRun interrupted = Assert.Single(unfinishedBeforeResume);
        Assert.Null(interrupted.Unreadable);

        // PickExistingFilePathAsync only ever hands back a path, never bytes (see
        // IFilePickerService's own remarks), so the interrupted run's exact bytes have to actually
        // exist on disk for the picker double to point at.
        string filePath = Path.Combine(_directory, "resume.bin");
        await File.WriteAllBytesAsync(filePath, bytes, TestContext.Current.CancellationToken);
        _picker.PathToReturn = filePath;

        UnfinishedModel model = new(paths, run, _picker);
        model.Refresh();

        UnfinishedRow row = Assert.Single(model.Rows);
        Assert.Null(row.Unreadable);
        Assert.Equal(wallet.ClassicAddress, row.Writer);
        Assert.Equal(reference, row.KeyReference);

        Assert.True(await model.ResumeAsync(row, TestContext.Current.CancellationToken));
        Assert.Equal(PublicationRunState.Running, run.Snapshot.State);

        await run.Completion;
        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
    }

    /// <summary>
    /// Copied from <c>PublicationRunTests.GatedSubmitSink</c> rather than shared with it: that type
    /// is private to its own file, and the two files' needs are not identical enough (this one is
    /// used across two runs on one <see cref="PublicationRun"/>, not one) to be worth promoting a
    /// shared double over, the same judgement <see cref="PublicationRunTests"/> itself makes for its
    /// other gated doubles.
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
