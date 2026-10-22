using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class RunModelEtaTests : IDisposable
{
    private const long SevenMegabytes = 7_123_692;

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-eta-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static RunSnapshot Running(string stage, long done, long total, bool batch, TimeSpan elapsed, long fileBytes = SevenMegabytes) =>
        new(PublicationRunState.Running, new PublishProgress(stage, done, total), null, null, "f.bin", "testnet", null)
        {
            UsesBatch = batch,
            StartedAt = Now - elapsed,
            FileBytes = fileBytes,
        };

    private PublicationRun NewRun() =>
        new(new FakeConnectionFactory(), new NeverVault(), new RunPaths(_directory), new PublicationHistory(new RunPaths(_directory).HistoryPath));

    [Fact]
    public void A_measured_rate_gives_the_rate_the_time_left_with_a_settle_per_pass_to_come_and_the_elapsed_time()
    {
        string text = RunModel.StageTextOf(Running("chunks", 1240, 7124, batch: true, TimeSpan.FromSeconds(24)), Now, rate: 52);

        // 5,884 left at 52/s is 113 s, plus a settle for the list pass and one for the manifest: 137 s.
        Assert.Equal($"Writing the file — {1240:N0} of {7124:N0} · 52 chunks/s · about 3 min left · 0:24 elapsed", text);
    }

    [Fact]
    public void Before_thirty_seconds_the_Batch_path_assumes_seventy_memos_a_second_and_shows_no_rate()
    {
        string text = RunModel.StageTextOf(Running("chunks", 120, 7124, batch: true, TimeSpan.FromSeconds(5)), Now, rate: null);

        Assert.Equal($"Writing the file — {120:N0} of {7124:N0} · about 3 min left · 0:05 elapsed", text);
    }

    [Fact]
    public void Before_thirty_seconds_the_ticket_path_says_it_is_estimating()
    {
        string text = RunModel.StageTextOf(Running("chunks", 120, 7124, batch: false, TimeSpan.FromSeconds(5)), Now, rate: null);

        Assert.Equal($"Writing the file — {120:N0} of {7124:N0} · estimating… · 0:05 elapsed", text);
    }

    [Fact]
    public void Nothing_landing_for_thirty_seconds_reads_as_estimating_never_as_an_endless_wait()
    {
        string text = RunModel.StageTextOf(Running("chunks", 120, 7124, batch: true, TimeSpan.FromSeconds(90)), Now, rate: 0);

        Assert.Contains("estimating…", text, StringComparison.Ordinal);
        Assert.DoesNotContain("∞", text, StringComparison.Ordinal);
        Assert.DoesNotContain("left", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pass_whose_memos_have_all_landed_claims_no_seconds_left()
    {
        string text = RunModel.StageTextOf(Running("chunks", 7124, 7124, batch: true, TimeSpan.FromSeconds(40)), Now, rate: 300);

        Assert.Equal($"Writing the file — {7124:N0} of {7124:N0} · finishing this pass · 0:40 elapsed", text);
    }

    [Fact]
    public async Task Resume_by_path_of_a_run_no_longer_listed_says_so()
    {
        UnfinishedModel model = new(new RunPaths(_directory), NewRun(), new FakeFilePicker());

        Assert.False(await model.ResumeByPathAsync(Path.Combine(_directory, "gone.json"), TestContext.Current.CancellationToken));
        Assert.Equal("This run is no longer listed under unfinished runs, so there is nothing to resume.", model.Message);
    }

    [Fact]
    public void The_fence_wait_says_what_it_waits_for_and_roughly_how_long()
    {
        Assert.Equal(
            "Waiting for earlier transactions to expire — about 40 s",
            RunModel.StageTextOf(Running("fence", 0, 10, batch: true, TimeSpan.FromSeconds(3)), Now, rate: null));
    }

    [Fact]
    public void A_fence_already_behind_the_ledger_never_shows_a_negative_wait()
    {
        string text = RunModel.StageTextOf(Running("fence", 12, 10, batch: true, TimeSpan.FromSeconds(3)), Now, rate: null);

        Assert.DoesNotContain("-", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rate_is_measured_over_the_last_thirty_seconds_on_the_models_clock()
    {
        ManualTimeProvider time = new();
        RunModel model = new(NewRun(), time);
        DateTimeOffset start = time.GetUtcNow();

        RunSnapshot first = Running("chunks", 0, 7124, batch: true, TimeSpan.Zero) with { StartedAt = start };
        Assert.DoesNotContain("chunks/s", model.Describe(first), StringComparison.Ordinal);

        time.Advance(TimeSpan.FromSeconds(30));
        RunSnapshot later = Running("chunks", 1500, 7124, batch: true, TimeSpan.Zero) with { StartedAt = start };

        Assert.Contains("50 chunks/s", model.Describe(later), StringComparison.Ordinal);
        Assert.Contains("0:30 elapsed", model.Describe(later), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_run_whose_state_is_still_unfinished_can_be_resumed_from_its_screen()
    {
        string path = Path.Combine(_directory, "run.json");
        new PublicationState(
            PublicationState.CurrentVersion, "testnet", "rWriter", "key-1", "text/plain", "f.bin", 2500, MemoLayout.ChunkBytes, "AB",
            42, "chunks", new Dictionary<int, string>()).Save(path);

        RunSnapshot failed = new(PublicationRunState.Failed, null, null, "it failed", "f.bin", "testnet", null) { StatePath = path };

        Assert.True(RunModel.IsResumable(failed));
        Assert.False(RunModel.IsResumable(failed with { State = PublicationRunState.Succeeded }));
        Assert.False(RunModel.IsResumable(failed with { StatePath = Path.Combine(_directory, "gone.json") }));

        (PublicationState.Load(path) with { Phase = "done" }).Save(path);
        Assert.False(RunModel.IsResumable(failed));
    }
}
