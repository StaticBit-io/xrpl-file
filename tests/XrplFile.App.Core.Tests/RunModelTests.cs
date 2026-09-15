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
        RunModel idleModel = new(NewRun());
        AssertViewMatchesProperties(idleModel);

        FakeConnectionFactory connections = new();
        GatedSubmitSink gate = new(connections.Sink, gateAt: 1);
        GatedConnectionFactory gated = new(connections, gate);

        (PublicationRun run, PublishRequest request) =
            await ReadyAsync(TestContext.Current.CancellationToken, connections: gated, ledger: connections.Ledger);
        RunModel model = new(run);

        Assert.True(run.Start(request));

        await WaitForGateAsync(gate, run);
        AssertViewMatchesProperties(model);

        gate.Release();
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, model.Snapshot.State);
        AssertViewMatchesProperties(model);
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
        Assert.Equal(model.StageText, view.StageText);
        Assert.Equal(model.Percent, view.Percent);
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
        PublishRequest request = new(
            new byte[4096], "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1");

        return (run, request);
    }
}
