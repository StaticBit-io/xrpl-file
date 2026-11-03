using XrplFile.App.Core.Localization;
using Xunit;

namespace XrplFile.App.Core.Tests.Localization;

/// <summary>The phrases of the run, unfinished-runs and history screens whose English changes with a number, a position in a sentence or the language's locale.</summary>
public sealed class RunAreaTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static RunSnapshot Running(string stage, long done, long total) =>
        new(PublicationRunState.Running, new PublishProgress(stage, done, total), null, null, "f.bin", "testnet", null)
        {
            StartedAt = Now - TimeSpan.FromSeconds(5),
        };

    [Theory]
    [InlineData(1, "Reading the ledger history — 1 record · 0:05 elapsed")]
    [InlineData(2, "Reading the ledger history — 2 records · 0:05 elapsed")]
    public void The_record_count_agrees_with_its_number(long done, string expected)
    {
        Assert.Equal(expected, TestText.En(RunModel.StageTextOf(Running("history", done, 0), Now, rate: null)));
    }

    [Theory]
    [InlineData(1, "It stays on this account", "owner reserve, until a later publication from this account spends it")]
    [InlineData(3, "They stay on this account", "each holding 2 XRP of owner reserve")]
    public void Tickets_that_stay_are_named_in_the_singular_or_the_plural(int locked, string start, string further)
    {
        PublicationReport report = new(
            new XrplFileLink("testnet", new string('A', 64)),
            new Manifest("application/octet-stream", 4096, 1024, "AB", 1, 2, 1, 0, "file.bin"),
            "rWriter", 6, 72, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, 0, new string('B', 64))
        {
            TicketReleaseFeeDrops = 0,
            TicketReleaseFeeDropsInDoubt = 0,
            TicketsNotReleased = locked,
        };
        RunSnapshot snapshot = new(PublicationRunState.Succeeded, null, report, null, "big.bin", "1", null)
        {
            UsesTickets = true,
            OwnerReserveDrops = 2_000_000,
        };

        string text = TestText.En(RunModel.TicketNoteOf(snapshot));

        Assert.Contains(start, text, StringComparison.Ordinal);
        Assert.Contains(further, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stage_this_application_does_not_know_keeps_its_raw_name_on_the_line_and_in_a_sentence()
    {
        Assert.Equal("something-new", TestText.En(RunModel.StageNameOf("something-new")));

        RunSnapshot stopped = Running("something-new", 3, 9) with { State = PublicationRunState.Cancelled, FinishedAt = Now };
        Assert.Equal("Stopped while something-new — 3 of 9 · 0:05 elapsed", TestText.En(RunModel.StageTextOf(stopped, Now, rate: null)));
    }

    [Fact]
    public void The_stage_in_a_sentence_is_its_own_phrase_not_the_title_lowered()
    {
        RunSnapshot stopped = Running("lists", 3, 9) with { State = PublicationRunState.Failed, FinishedAt = Now };

        UiText line = RunModel.StageTextOf(stopped, Now, rate: null);

        Assert.Contains(RunText.MidLists, FlattenIds(line));
        Assert.DoesNotContain(RunText.StageLists, FlattenIds(line));
    }

    [Theory]
    [InlineData(52.4, "52 chunks/s")]
    [InlineData(1.3, "1.3 chunks/s")]
    public void A_rate_is_written_in_whole_numbers_from_ten_up_and_in_tenths_below(double rate, string expected)
    {
        string text = TestText.En(RunModel.StageTextOf(Running("chunks", 530, 7124) with { UsesBatch = true }, Now, rate));

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_and_a_note_about_the_list_are_told_together()
    {
        UiText both = UiText.Of(HistoryText.FailureAndNote, ("failure", HistoryText.ListUnreadable), ("note", HistoryText.HeldBack));

        Assert.Equal(
            TestText.En(HistoryText.ListUnreadable) + " " + TestText.En(HistoryText.HeldBack),
            TestText.En(both));
    }

    [Theory]
    [InlineData(1, "That file is 1 byte and this run was publishing 3,000.")]
    [InlineData(2, "That file is 2 bytes and this run was publishing 3,000.")]
    public void A_size_mismatch_agrees_with_the_size_of_the_picked_file(long size, string start)
    {
        UiText text = UiText.Of(UnfinishedText.FileSizeDiffers, ("count", size), ("expected", 3000L));

        Assert.StartsWith(start, TestText.En(text), StringComparison.Ordinal);
    }

    private static IEnumerable<TextId> FlattenIds(UiText text)
    {
        yield return text.Id;
        foreach (UiArg arg in text.Args)
        {
            if (arg.Value is TextId id) yield return id;
            if (arg.Value is UiText nested)
            {
                foreach (TextId inner in FlattenIds(nested)) yield return inner;
            }
        }
    }
}
