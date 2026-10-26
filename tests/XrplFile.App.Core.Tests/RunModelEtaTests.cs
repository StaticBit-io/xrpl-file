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

        // 5,884 left at 52/s is 113 s, plus a settle for the list pass and one for the manifest: 137 s,
        // to the nearest minute as spec 6.3's own example has it.
        Assert.Equal($"Writing the file — {1240:N0} of {7124:N0} · 52 chunks/s · about 2 min left · 0:24 elapsed", text);
    }

    [Fact]
    public void A_run_with_nothing_sent_yet_says_starting_and_how_long_it_has_run()
    {
        RunSnapshot snapshot = Running("chunks", 0, 7124, batch: true, TimeSpan.FromSeconds(7)) with { Progress = null };

        Assert.Equal("Starting · 0:07 elapsed", RunModel.StageTextOf(snapshot, Now, rate: null));
    }

    [Fact]
    public void A_pass_that_is_sending_with_nothing_landed_yet_names_its_stage()
    {
        string text = RunModel.StageTextOf(Running("chunks", 0, 7124, batch: true, TimeSpan.FromSeconds(6)), Now, rate: null);

        // 7,124 left at 70/s is 102 s, plus two settles.
        Assert.Equal($"Writing the file — 0 of {7124:N0} · about 2 min left · 0:06 elapsed", text);
    }

    [Fact]
    public void Before_thirty_seconds_the_Batch_path_assumes_seventy_memos_a_second_and_shows_no_rate()
    {
        string text = RunModel.StageTextOf(Running("chunks", 120, 7124, batch: true, TimeSpan.FromSeconds(5)), Now, rate: null);

        // 7,004 left at 70/s is 100 s, plus two settles: 124 s.
        Assert.Equal($"Writing the file — {120:N0} of {7124:N0} · about 2 min left · 0:05 elapsed", text);
    }

    [Fact]
    public void A_rate_under_ten_keeps_a_decimal_and_hours_read_as_hours()
    {
        string text = RunModel.StageTextOf(Running("chunks", 530, 7124, batch: false, TimeSpan.FromSeconds(24)), Now, rate: 1.3);

        // 6,594 left at 1.3/s is 5,072 s, plus two settles: 5,097 s, 85 min.
        Assert.Equal($"Writing the file — {530:N0} of {7124:N0} · {1.3:N1} chunks/s · about 1 h 25 min left · 0:24 elapsed", text);
    }

    [Fact]
    public void A_rate_that_shows_as_zero_is_a_stall_not_a_time_left_in_days()
    {
        string text = RunModel.StageTextOf(Running("chunks", 120, 7124, batch: true, TimeSpan.FromSeconds(90)), Now, rate: 0.03);

        Assert.Equal($"Writing the file — {120:N0} of {7124:N0} · estimating… · 1:30 elapsed", text);
    }

    [Fact]
    public void A_Batch_pass_with_rounds_still_to_come_adds_a_settle_for_each()
    {
        const long fileBytes = 41_000_000;
        string text = RunModel.StageTextOf(Running("chunks", 0, 41_000, batch: true, TimeSpan.FromSeconds(6), fileBytes), Now, rate: 70);

        // 41,000 at 70/s is 586 s; two more rounds of this pass, the list pass and the manifest: 4 x 12 s. One settle
        // per pass would give 610 s, which reads "10 min"; the rounds' 634 s read "11 min".
        Assert.Equal($"Writing the file — 0 of {41_000:N0} · 70 chunks/s · about 11 min left · 0:06 elapsed", text);
    }

    [Fact]
    public void The_read_back_says_how_long_its_remaining_blocks_take()
    {
        string text = RunModel.StageTextOf(Running("read-back", 1, 4, batch: true, TimeSpan.FromSeconds(10)), Now, rate: null);

        // Three blocks of 1,024 records at 0.7 s per 400: 5.4 s.
        Assert.Equal("Reading it back from the ledger — 1 of 4 · about 6 s left · 0:10 elapsed", text);
    }

    [Fact]
    public void A_span_just_over_a_minute_is_not_rounded_up_to_two()
    {
        // 16 ledgers at the approximate interval is 64 s, which used to read "about 2 min".
        Assert.Equal(
            "Waiting for earlier transactions to expire — about 64 s",
            RunModel.StageTextOf(Running("fence", 0, 16, batch: true, TimeSpan.FromSeconds(3)), Now, rate: null));
    }

    [Fact]
    public void A_published_run_shows_how_long_it_took_and_stops_counting()
    {
        RunSnapshot published = Running("manifest", 1, 1, batch: true, TimeSpan.FromSeconds(283)) with
        {
            State = PublicationRunState.Succeeded,
            FinishedAt = Now - TimeSpan.FromSeconds(17),
        };

        Assert.Equal("Finished in 4:26", RunModel.StageTextOf(published, Now, rate: null));
        Assert.Equal("Finished in 4:26", RunModel.StageTextOf(published, Now + TimeSpan.FromMinutes(5), rate: 40));
    }

    [Fact]
    public void A_cancelled_run_shows_where_it_stopped_without_a_rate_or_a_time_left()
    {
        RunSnapshot cancelled = Running("chunks", 1240, 7124, batch: true, TimeSpan.FromSeconds(90)) with
        {
            State = PublicationRunState.Cancelled,
            FinishedAt = Now - TimeSpan.FromSeconds(66),
        };

        Assert.Equal(
            $"Stopped while writing the file — {1240:N0} of {7124:N0} · 0:24 elapsed",
            RunModel.StageTextOf(cancelled, Now, rate: 52));
    }

    [Fact]
    public void A_run_stopped_during_the_fence_or_before_any_progress_says_so()
    {
        RunSnapshot fence = Running("fence", 3, 27, batch: true, TimeSpan.FromSeconds(9)) with
        {
            State = PublicationRunState.Cancelled,
            FinishedAt = Now - TimeSpan.FromSeconds(4),
        };
        RunSnapshot early = fence with { State = PublicationRunState.Failed, Progress = null };

        Assert.Equal("Stopped while waiting for earlier transactions to expire · 0:05 elapsed", RunModel.StageTextOf(fence, Now, rate: null));
        Assert.Equal("Stopped while starting · 0:05 elapsed", RunModel.StageTextOf(early, Now, rate: null));
    }

    [Fact]
    public void The_fence_wait_uses_the_ledger_pace_it_measures()
    {
        ManualTimeProvider time = new();
        RunModel model = new(NewRun(), time);
        DateTimeOffset start = time.GetUtcNow();
        RunSnapshot Fence(long done) => Running("fence", done, 27, batch: true, TimeSpan.Zero) with { StartedAt = start };

        // 27 ledgers at the approximate interval: the only figure there is before a ledger closes.
        Assert.Equal("Waiting for earlier transactions to expire — about 2 min", model.Describe(Fence(0)));

        // Testnet: a ledger every 2 s, the first one 2 s in.
        for (int second = 1; second <= 12; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            model.Describe(Fence(second / 2));
        }

        // Five ledgers in the ten seconds since the first: 21 left at 0.5 a second is 42 s, not 84 s.
        Assert.Equal("Waiting for earlier transactions to expire — about 42 s", model.Describe(Fence(6)));
    }

    /// <summary>
    /// Seen in the lab: once the fence is behind it the session stops reporting while it prepares
    /// the resume, and a pace measured up to now grew the wait from 4 s to 29 s over half a minute.
    /// </summary>
    [Fact]
    public void A_fence_wait_does_not_grow_while_no_ledger_is_reported()
    {
        ManualTimeProvider time = new();
        RunModel model = new(NewRun(), time);
        DateTimeOffset start = time.GetUtcNow();
        RunSnapshot Fence(long done) => Running("fence", done, 27, batch: true, TimeSpan.Zero) with { StartedAt = start };

        model.Describe(Fence(0));
        for (int second = 1; second <= 52; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            model.Describe(Fence(Math.Min(second / 2, 26)));
        }

        // 25 ledgers in 50 s from the first: one left at 0.5 a second.
        Assert.Equal("Waiting for earlier transactions to expire — about 2 s", model.Describe(Fence(26)));

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal("Waiting for earlier transactions to expire — about 2 s", model.Describe(Fence(26)));
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
    public void The_rate_is_measured_over_thirty_seconds_from_the_first_memo_that_landed_on_the_models_clock()
    {
        ManualTimeProvider time = new();
        RunModel model = new(NewRun(), time);
        DateTimeOffset start = time.GetUtcNow();
        RunSnapshot Chunks(long done) => Running("chunks", done, 7124, batch: true, TimeSpan.Zero) with { StartedAt = start };

        Assert.DoesNotContain("chunks/s", model.Describe(Chunks(0)), StringComparison.Ordinal);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("chunks/s", model.Describe(Chunks(100)), StringComparison.Ordinal);

        time.Advance(TimeSpan.FromSeconds(30));
        string text = model.Describe(Chunks(1600));

        Assert.Contains("50 chunks/s", text, StringComparison.Ordinal);
        Assert.Contains("0:35 elapsed", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Report defect 5: at 112 chunks the line read "3 chunks/s · about 35 min left" and the run
    /// finished 190 s later. The opening seconds, signing and filling the window with nothing landed
    /// yet, were measured as part of the rate.
    /// </summary>
    [Fact]
    public void A_slow_opening_before_the_first_memo_lands_is_not_measured_as_the_rate()
    {
        ManualTimeProvider time = new();
        RunModel model = new(NewRun(), time);
        DateTimeOffset start = time.GetUtcNow();
        RunSnapshot Chunks(long done) => Running("chunks", done, 7124, batch: true, TimeSpan.Zero) with { StartedAt = start };

        for (int second = 0; second < 29; second++)
        {
            model.Describe(Chunks(0));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        model.Describe(Chunks(56));
        time.Advance(TimeSpan.FromSeconds(1));
        string text = model.Describe(Chunks(112));

        // The prior, not 112 chunks over 30 s: 7,012 left at 70/s is 100 s, plus two settles.
        Assert.Equal($"Writing the file — {112:N0} of {7124:N0} · about 2 min left · 0:30 elapsed", text);
    }

    [Fact]
    public void A_new_run_in_the_same_stage_does_not_inherit_the_last_runs_samples()
    {
        ManualTimeProvider time = new();
        RunModel model = new(NewRun(), time);
        DateTimeOffset first = time.GetUtcNow();

        model.Describe(Running("chunks", 0, 7124, batch: true, TimeSpan.Zero) with { StartedAt = first });
        time.Advance(TimeSpan.FromSeconds(5));
        model.Describe(Running("chunks", 100, 7124, batch: true, TimeSpan.Zero) with { StartedAt = first });
        time.Advance(TimeSpan.FromSeconds(35));
        Assert.Contains("chunks/s", model.Describe(Running("chunks", 1500, 7124, batch: true, TimeSpan.Zero) with { StartedAt = first }), StringComparison.Ordinal);

        DateTimeOffset second = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(1));
        model.Describe(Running("chunks", 1400, 7124, batch: true, TimeSpan.Zero) with { StartedAt = second });
        time.Advance(TimeSpan.FromSeconds(1));
        string text = model.Describe(Running("chunks", 1600, 7124, batch: true, TimeSpan.Zero) with { StartedAt = second });

        Assert.DoesNotContain("chunks/s", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Testnet, 2026-10-22: the chunks stood at 1,064 while a round waited on one refused outer, and for
    /// 30 s the line read "1.1 chunks/s · about 1 h 35 min left": the trickle of the last window, taken
    /// for the pace of a run that went on at full speed a minute later.
    /// </summary>
    [Fact]
    public void A_rate_that_falls_below_a_tenth_of_the_best_this_pass_has_shown_reads_as_a_stall()
    {
        ManualTimeProvider time = new();
        RunModel model = new(NewRun(), time);
        DateTimeOffset start = time.GetUtcNow();
        RunSnapshot Chunks(long done) => Running("chunks", done, 7124, batch: true, TimeSpan.Zero) with { StartedAt = start };
        static long Landed(int second) => second switch { < 6 => 0, < 11 => 24, < 22 => 1032, < 36 => 1048, _ => 1064 };

        string text = string.Empty;
        for (int second = 0; second <= 40; second++)
        {
            text = model.Describe(Chunks(Landed(second)));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        // At 0:40 the window still reaches back to 24 chunks: 1,040 in 30 s.
        Assert.Contains("35 chunks/s", text, StringComparison.Ordinal);

        // At 0:41 it starts at 1,032: 32 in 30 s, under a tenth of 35.
        text = model.Describe(Chunks(Landed(41)));

        Assert.Equal($"Writing the file — {1064:N0} of {7124:N0} · estimating… · 0:41 elapsed", text);
    }

    [Fact]
    public void A_screen_that_comes_back_after_a_minute_away_measures_across_the_gap()
    {
        ManualTimeProvider time = new();
        RunModel model = new(NewRun(), time);
        DateTimeOffset start = time.GetUtcNow();
        RunSnapshot Chunks(long done) => Running("chunks", done, 7124, batch: true, TimeSpan.Zero) with { StartedAt = start };

        model.Describe(Chunks(0));
        time.Advance(TimeSpan.FromSeconds(1));
        model.Describe(Chunks(50));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Contains("40 chunks/s", model.Describe(Chunks(1250)), StringComparison.Ordinal);

        time.Advance(TimeSpan.FromSeconds(90));

        Assert.Contains("40 chunks/s", model.Describe(Chunks(4850)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_run_whose_state_is_still_unfinished_can_be_resumed_from_its_screen()
    {
        string path = Path.Combine(_directory, "run.json");
        new PublicationState(
            PublicationState.CurrentVersion, "testnet", "rWriter", "key-1", "text/plain", "f.bin", 2500, MemoLayout.ChunkBytes, "AB",
            42, "chunks").Save(path);

        RunSnapshot failed = new(PublicationRunState.Failed, null, null, "it failed", "f.bin", "testnet", null) { StatePath = path };

        Assert.True(RunModel.IsResumable(failed));
        Assert.False(RunModel.IsResumable(failed with { State = PublicationRunState.Succeeded }));
        Assert.False(RunModel.IsResumable(failed with { StatePath = Path.Combine(_directory, "gone.json") }));

        (PublicationState.Load(path) with { Phase = "done" }).Save(path);
        Assert.False(RunModel.IsResumable(failed));
    }
}
