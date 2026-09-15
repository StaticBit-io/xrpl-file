using System.Text;

using Xrpl.Wallet;

using XrplFile.Keys;

using Xunit;

using static XrplFile.App.Core.Tests.GatedWait;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// Inherits <see cref="VaultHarness"/> rather than plain <see cref="IDisposable"/>: most of these
/// tests never start a run and are content with <see cref="NeverVault"/>, but the two resume tests
/// (<see cref="An_interrupted_readable_run_resumes_with_the_same_bytes"/> and
/// <see cref="A_fresh_process_can_resume_a_run_started_by_a_different_instance"/>) need a real,
/// working <see cref="IKeyVault"/> to actually publish and then resume -- the harness is what
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

    /// <summary>
    /// <see cref="UnfinishedModel.Delete"/> calls <see cref="UnfinishedModel.Refresh"/>
    /// unconditionally after a failed <see cref="File.Delete(string)"/> -- the row is still on disk,
    /// so the list has to be re-read regardless -- and <see cref="UnfinishedModel.Refresh"/> clears
    /// <see cref="UnfinishedModel.Message"/> on its own first line. Without the fix this test guards,
    /// that ordering means a failed delete's explanation is set and then immediately erased: the row
    /// stays (correctly), but the person is told nothing about why deleting it -- the one destructive
    /// action this screen offers -- did not work.
    ///
    /// Forces the failure the way each platform actually enforces it, rather than the way an earlier
    /// version of this test did -- opening the file with <see cref="FileShare.None"/>, which is a
    /// genuine sharing violation on Windows but is advisory, and silently ignored, on Unix, where
    /// CI runs this suite (<c>ubuntu-latest</c>). Deleting a file on Unix is governed entirely by the
    /// *containing directory's* write permission, not by anything about the file itself: a read-only
    /// attribute on the file changes nothing there (verified against a Linux container: the delete
    /// still succeeds), but taking write permission off <see cref="RunPaths.StateDirectory"/> itself
    /// reliably turns the delete into an <see cref="UnauthorizedAccessException"/>, the same type
    /// <see cref="File.Delete(string)"/> throws on Windows for the held-open file, while listing the
    /// directory's own contents (what <see cref="UnfinishedModel.Refresh"/> does next) only ever
    /// needed read and execute, which are left alone.
    /// </summary>
    [Fact]
    public void A_state_file_that_cannot_be_deleted_says_so_and_keeps_the_row()
    {
        WriteUnreadableState("broken.json");

        UnfinishedModel model = new(Paths, NewRun(), _picker);
        model.Refresh();
        UnfinishedRow row = Assert.Single(model.Rows);

        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(row.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                model.Delete(row);
            }
        }
        else
        {
            UnixFileMode original = File.GetUnixFileMode(Paths.StateDirectory);
            File.SetUnixFileMode(Paths.StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                model.Delete(row);
            }
            finally
            {
                // Restored before the test's own DisposeAsync tries to recursively delete _directory,
                // which needs write permission on every directory on the way down.
                File.SetUnixFileMode(Paths.StateDirectory, original);
            }
        }

        Assert.NotNull(model.Message);
        Assert.Single(model.Rows);
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
    /// A vault holding one funded, generated key, plus a connection factory whose ledger already
    /// knows that key's balance -- everything both resume tests need before they can start a run
    /// that will actually be interrupted.
    /// </summary>
    private async Task<(KeyVault Vault, XrplWallet Wallet, string Reference, FakeConnectionFactory Connections, byte[] Bytes)>
        CreateFundedVaultAsync(CancellationToken cancellationToken)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), cancellationToken);

        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key", "standalone", wallet.ClassicAddress, wallet.Seed),
            cancellationToken);

        FakeConnectionFactory connections = new();
        connections.Ledger.Balances[wallet.ClassicAddress] = 100_000_000;

        return (vault, wallet, reference, connections, new byte[4096]);
    }

    /// <summary>
    /// Starts <paramref name="run"/> and cancels it only once it is actually inside its first
    /// submission -- see <c>PublicationRunTests.A_cancelled_run_reports_cancelled_and_leaves_a_state_file</c>'s
    /// own remarks on why cancelling the instant <see cref="PublicationRun.Start"/> returns races
    /// <see cref="PublicationSession.StartAsync"/> itself and can drop the state file entirely.
    /// Leaves <paramref name="run"/> <see cref="PublicationRunState.Cancelled"/> with a readable
    /// state file on disk -- the fixture both resume tests below build on.
    /// </summary>
    private static async Task StartAndCancelAsync(PublicationRun run, GatedSubmitSink gate, PublishRequest request)
    {
        Assert.True(run.Start(request));

        await WaitForGateAsync(gate, run);
        run.Cancel();
        gate.Release();

        await run.Completion;
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
    }

    /// <summary>
    /// The only proof the resume path is actually wired end to end, not just refused for the reasons
    /// covered above. Builds a real interrupted run, then points a fresh <see cref="UnfinishedModel"/>
    /// at the same <see cref="RunPaths"/> and resumes it on the very same <see cref="PublicationRun"/>
    /// the interrupted run ran on (exactly what <see cref="PublicationRun.Resume"/> is for: the same
    /// instance, asked to pick a run back up). The gate is reused rather than rebuilt for the resumed
    /// submissions: its <c>gateAt: 1</c> only ever holds the first call across the sink's whole
    /// lifetime, so every submission the resumed run makes after that sails through ungated, and the
    /// run is free to run all the way to <see cref="PublicationRunState.Succeeded"/> -- stronger proof
    /// than stopping at "returned true and is Running" alone, though that is the minimum this test is
    /// required to show.
    ///
    /// See <see cref="A_fresh_process_can_resume_a_run_started_by_a_different_instance"/> for the
    /// companion case this one does not cover: a resume through a brand-new
    /// <see cref="PublicationRun"/>, standing in for the application having actually been restarted.
    /// </summary>
    [Fact]
    public async Task An_interrupted_readable_run_resumes_with_the_same_bytes()
    {
        (KeyVault vault, XrplWallet wallet, string reference, FakeConnectionFactory connections, byte[] bytes) =
            await CreateFundedVaultAsync(TestContext.Current.CancellationToken);

        GatedSubmitSink gate = new(connections.Sink, gateAt: 1);
        GatedConnectionFactory gated = new(connections, gate);

        RunPaths paths = Paths;
        PublicationRun run = new(gated, vault, paths, new PublicationHistory(paths.HistoryPath));

        PublishRequest request = new(
            bytes, "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1");

        await StartAndCancelAsync(run, gate, request);

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
    /// The scenario <see cref="An_interrupted_readable_run_resumes_with_the_same_bytes"/> does not
    /// cover: this application is never actually reached through the same, still-in-memory
    /// <see cref="PublicationRun"/> that started the interrupted publication -- a real resume follows
    /// a process restart, and nothing survives that except what
    /// <see cref="PublicationSession.StartAsync"/> persisted to <see cref="RunPaths.StateDirectory"/>.
    /// <see cref="PublicationRun.Start"/> and <see cref="PublicationRun.Resume"/> both reset every one
    /// of a run's instance fields on every call, so there is nothing left in the first run's
    /// <see cref="PublicationRun"/> for a second instance to need or conflict with -- but that is
    /// exactly the assumption worth pinning with its own test rather than only inferring from reading
    /// the source. Builds a second, brand-new
    /// <see cref="PublicationRun"/> over the same <see cref="RunPaths"/>, the same vault, and the same
    /// <see cref="IPublicationHistory"/>, and resumes through it instead of through the run that
    /// started the publication.
    /// </summary>
    [Fact]
    public async Task A_fresh_process_can_resume_a_run_started_by_a_different_instance()
    {
        (KeyVault vault, XrplWallet wallet, string reference, FakeConnectionFactory connections, byte[] bytes) =
            await CreateFundedVaultAsync(TestContext.Current.CancellationToken);

        GatedSubmitSink gate = new(connections.Sink, gateAt: 1);
        GatedConnectionFactory gated = new(connections, gate);

        RunPaths paths = Paths;
        PublicationHistory history = new(paths.HistoryPath);
        PublicationRun startingRun = new(gated, vault, paths, history);

        PublishRequest request = new(
            bytes, "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1");

        await StartAndCancelAsync(startingRun, gate, request);

        string filePath = Path.Combine(_directory, "resume-fresh.bin");
        await File.WriteAllBytesAsync(filePath, bytes, TestContext.Current.CancellationToken);
        _picker.PathToReturn = filePath;

        // The one thing this test is actually about: a second PublicationRun, sharing only the
        // paths, the vault and the history with the run that started the publication -- nothing in
        // memory carries over, matching an application that was closed and reopened.
        PublicationRun resumingRun = new(connections, vault, paths, history);
        UnfinishedModel model = new(paths, resumingRun, _picker);
        model.Refresh();

        UnfinishedRow row = Assert.Single(model.Rows);
        Assert.Null(row.Unreadable);

        Assert.True(await model.ResumeAsync(row, TestContext.Current.CancellationToken));
        Assert.Equal(PublicationRunState.Running, resumingRun.Snapshot.State);

        // The run that originally started the publication is untouched by the resume: it is still
        // sitting at Cancelled, not silently dragged along by a second instance resuming the same
        // state file.
        Assert.Equal(PublicationRunState.Cancelled, startingRun.Snapshot.State);

        await resumingRun.Completion;
        Assert.Equal(PublicationRunState.Succeeded, resumingRun.Snapshot.State);
    }
}
