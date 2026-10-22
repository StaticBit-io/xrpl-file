using System.Text;

using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class PublicationHistoryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-history-").FullName;

    private string HistoryPath => Path.Combine(_directory, "publications.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static PublicationRecord Record(string link, DateTimeOffset finishedAt) =>
        new(link, "photo.png", 4096, "image/png", "testnet", "rWriter", 7, 84, finishedAt);

    [Fact]
    public void An_absent_file_reads_as_an_empty_history_with_no_failure()
    {
        PublicationHistory history = new(HistoryPath);

        Assert.Empty(history.Read());
        Assert.Null(history.Failure);
    }

    [Fact]
    public void An_appended_record_reads_back()
    {
        PublicationHistory history = new(HistoryPath);
        history.Append(Record("xrpl-file://testnet/ABC", DateTimeOffset.UnixEpoch));

        Assert.Equal("xrpl-file://testnet/ABC", Assert.Single(new PublicationHistory(HistoryPath).Read()).Link);
    }

    [Fact]
    public void A_payer_is_written_and_read_back_only_when_the_record_has_one()
    {
        PublicationHistory history = new(HistoryPath);
        history.Append(Record("xrpl-file://testnet/ONE", DateTimeOffset.UnixEpoch));
        Assert.DoesNotContain("Payer", File.ReadAllText(HistoryPath), StringComparison.OrdinalIgnoreCase);

        history.Append(Record("xrpl-file://testnet/TWO", DateTimeOffset.UnixEpoch.AddDays(1)) with { Payer = "rPayer" });

        PublicationRecord paid = Assert.Single(new PublicationHistory(HistoryPath).Read(), r => r.Link.EndsWith("TWO", StringComparison.Ordinal));
        Assert.Equal("rPayer", paid.Payer);
        Assert.Null(Assert.Single(new PublicationHistory(HistoryPath).Read(), r => r.Link.EndsWith("ONE", StringComparison.Ordinal)).Payer);
    }

    [Fact]
    public void The_newest_record_comes_first()
    {
        PublicationHistory history = new(HistoryPath);
        history.Append(Record("xrpl-file://testnet/OLD", DateTimeOffset.UnixEpoch));
        history.Append(Record("xrpl-file://testnet/NEW", DateTimeOffset.UnixEpoch.AddDays(1)));

        Assert.Equal("xrpl-file://testnet/NEW", history.Read()[0].Link);
    }

    [Fact]
    public void A_forgotten_record_is_gone_and_the_others_stay()
    {
        PublicationHistory history = new(HistoryPath);
        history.Append(Record("xrpl-file://testnet/ONE", DateTimeOffset.UnixEpoch));
        history.Append(Record("xrpl-file://testnet/TWO", DateTimeOffset.UnixEpoch.AddDays(1)));

        history.Forget("xrpl-file://testnet/ONE");

        Assert.Equal("xrpl-file://testnet/TWO", Assert.Single(history.Read()).Link);
    }

    [Fact]
    public void Clearing_leaves_an_empty_history()
    {
        PublicationHistory history = new(HistoryPath);
        history.Append(Record("xrpl-file://testnet/ONE", DateTimeOffset.UnixEpoch));

        history.Clear();

        Assert.Empty(history.Read());
    }

    [Fact]
    public void A_file_that_is_not_json_reads_as_empty_and_says_why_and_is_not_overwritten()
    {
        byte[] garbage = Encoding.UTF8.GetBytes("{ this is not json");
        File.WriteAllBytes(HistoryPath, garbage);

        PublicationHistory history = new(HistoryPath);

        Assert.Empty(history.Read());
        Assert.NotNull(history.Failure);
        Assert.Equal(garbage, File.ReadAllBytes(HistoryPath));
    }

    [Fact]
    public void Appending_leaves_no_temporary_file_behind()
    {
        PublicationHistory history = new(HistoryPath);
        history.Append(Record("xrpl-file://testnet/ONE", DateTimeOffset.UnixEpoch));

        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Forgetting_against_a_corrupt_file_leaves_the_file_original_bytes_on_disk()
    {
        byte[] garbage = Encoding.UTF8.GetBytes("{ this is not json");
        File.WriteAllBytes(HistoryPath, garbage);

        PublicationHistory history = new(HistoryPath);
        history.Forget("xrpl-file://testnet/ONE");

        Assert.Equal(garbage, File.ReadAllBytes(HistoryPath));
        Assert.NotNull(history.Failure);
    }

    [Fact]
    public void Clearing_against_a_corrupt_file_still_empties_it()
    {
        byte[] garbage = Encoding.UTF8.GetBytes("{ this is not json");
        File.WriteAllBytes(HistoryPath, garbage);

        PublicationHistory history = new(HistoryPath);
        history.Clear();

        Assert.Empty(new PublicationHistory(HistoryPath).Read());
    }

    [Fact]
    public void A_record_whose_name_is_cyrillic_round_trips_unescaped_in_the_file()
    {
        PublicationRecord record = new("xrpl-file://testnet/ABC", "фото.png", 4096, "image/png", "testnet", "rWriter", 7, 84, DateTimeOffset.UnixEpoch);
        PublicationHistory history = new(HistoryPath);
        history.Append(record);

        PublicationHistory newHistory = new(HistoryPath);
        Assert.Equal("фото.png", newHistory.Read()[0].Name);

        string fileContent = File.ReadAllText(HistoryPath);
        Assert.Contains("фото.png", fileContent);
        Assert.DoesNotContain("\\u", fileContent);
    }

    [Fact]
    public void Clearing_a_corrupt_file_clears_the_failure_message()
    {
        byte[] garbage = Encoding.UTF8.GetBytes("{ this is not json");
        File.WriteAllBytes(HistoryPath, garbage);

        PublicationHistory history = new(HistoryPath);
        history.Read();
        Assert.NotNull(history.Failure);

        history.Clear();

        Assert.Empty(history.Read());
        Assert.Null(history.Failure);
    }

    [Fact]
    public void Appending_to_a_corrupt_file_clears_the_failure_message_once_the_write_succeeds()
    {
        byte[] garbage = Encoding.UTF8.GetBytes("{ this is not json");
        File.WriteAllBytes(HistoryPath, garbage);

        PublicationHistory history = new(HistoryPath);

        // Append reads first -- which sets Failure for the corrupt file -- and then replaces that
        // same file with one holding just the new record. Failure must reflect only the write's own
        // outcome once Append returns, not the read's: the write succeeded, and "the file has been
        // left as it is" (Read's own sentence) would be a false thing to say about a file Append
        // just deliberately overwrote.
        history.Append(Record("xrpl-file://testnet/NEW", DateTimeOffset.UnixEpoch));

        Assert.Null(history.Failure);
        Assert.Equal("xrpl-file://testnet/NEW", Assert.Single(history.Read()).Link);
    }

    [Fact]
    public void Appending_to_a_healthy_history_returns_recorded_with_no_note()
    {
        PublicationHistory history = new(HistoryPath);
        HistoryAppendResult result = history.Append(Record("xrpl-file://testnet/ONE", DateTimeOffset.UnixEpoch));

        Assert.True(result.Recorded);
        Assert.Null(result.Note);
    }

    [Fact]
    public void Appending_to_a_corrupt_file_returns_recorded_with_a_note_about_the_replacement()
    {
        byte[] garbage = Encoding.UTF8.GetBytes("{ this is not json");
        File.WriteAllBytes(HistoryPath, garbage);

        PublicationHistory history = new(HistoryPath);
        HistoryAppendResult result = history.Append(Record("xrpl-file://testnet/NEW", DateTimeOffset.UnixEpoch));

        // Recorded, not a failure -- the publication is safely in the list -- but still worth a note:
        // the file could not be read at all, so whatever else it used to hold could not be carried
        // forward, and Append replaced it outright rather than merging into it.
        Assert.True(result.Recorded);
        Assert.NotNull(result.Note);
        Assert.Equal("xrpl-file://testnet/NEW", Assert.Single(history.Read()).Link);
    }

    [Fact]
    public void Appending_when_the_write_itself_fails_returns_not_recorded_with_a_note()
    {
        // A plain file occupying the path the history's own directory would need to be, so
        // Directory.CreateDirectory throws every time Save is asked to write -- the write itself
        // fails here, not the read, which is the one case that is genuinely a failure to record.
        string blockedParent = Path.Combine(_directory, "blocked-history-parent");
        File.WriteAllBytes(blockedParent, [1]);
        PublicationHistory history = new(Path.Combine(blockedParent, "publications.json"));

        HistoryAppendResult result = history.Append(Record("xrpl-file://testnet/ONE", DateTimeOffset.UnixEpoch));

        Assert.False(result.Recorded);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void Concurrent_appends_do_not_corrupt_each_others_writes()
    {
        PublicationHistory history = new(HistoryPath);

        // What two overlapping PublicationRun continuations hitting one PublicationHistory instance
        // looks like from this type's own side, without needing PublicationRun in the loop at all:
        // Save writes one fixed .tmp path under FileShare.None, so two genuinely overlapping writers
        // race that single file, and Failure is a plain auto-property either can clobber regardless
        // of which one actually failed. Twenty, run through Parallel.For rather than sequentially,
        // to give the runtime room to actually overlap them rather than happening to interleave one
        // at a time on the same thread.
        Parallel.For(0, 20, i => history.Append(Record($"xrpl-file://testnet/R{i}", DateTimeOffset.UnixEpoch.AddSeconds(i))));

        Assert.Null(history.Failure);
        Assert.Equal(20, history.Read().Count);
    }

    private string StateDirectory => Path.Combine(_directory, "runs");

    private string FinishedState(string name, string manifestHash)
    {
        Directory.CreateDirectory(StateDirectory);
        string path = Path.Combine(StateDirectory, name);
        new PublicationState(
            PublicationState.CurrentVersion, "testnet", "rWriter", "key-1", "text/plain", "done.txt", 2500, MemoLayout.ChunkBytes, "AB",
            42, "done", new Dictionary<int, string>(), Strategy: "sequence", ManifestHash: manifestHash).Save(path);
        return path;
    }

    [Fact]
    public void A_finished_run_missing_from_History_is_recorded_from_its_state_and_its_state_removed()
    {
        string statePath = FinishedState("1-done.json", new string('A', 64));
        PublicationHistory history = new(HistoryPath);

        Assert.Equal(1, history.RecordFinishedRuns(StateDirectory));

        PublicationRecord record = Assert.Single(history.Read());
        Assert.Equal(new XrplFileLink("testnet", new string('A', 64)).ToString(), record.Link);
        Assert.Equal("done.txt", record.Name);
        Assert.Equal(0, record.Transactions);
        Assert.True(record.Recovered);
        Assert.False(File.Exists(statePath));
    }

    [Fact]
    public void A_record_the_person_forgot_does_not_come_back_at_start_up()
    {
        FinishedState("1-done.json", new string('B', 64));
        PublicationHistory history = new(HistoryPath);
        history.RecordFinishedRuns(StateDirectory);

        history.Forget(new XrplFileLink("testnet", new string('B', 64)).ToString());

        Assert.Equal(0, history.RecordFinishedRuns(StateDirectory));
        Assert.Empty(history.Read());
    }

    [Fact]
    public void A_state_whose_link_is_already_listed_is_only_removed()
    {
        string link = new XrplFileLink("testnet", new string('C', 64)).ToString();
        PublicationHistory history = new(HistoryPath);
        history.Append(new PublicationRecord(link, "done.txt", 2500, "text/plain", "testnet", "rWriter", 4, 40, DateTimeOffset.UtcNow));
        string statePath = FinishedState("1-done.json", new string('C', 64));

        Assert.Equal(0, history.RecordFinishedRuns(StateDirectory));
        Assert.Single(history.Read());
        Assert.False(File.Exists(statePath));
    }

    [Fact]
    public void A_recovered_record_is_written_so_an_older_application_still_reads_the_list()
    {
        FinishedState("1-done.json", new string('D', 64));
        new PublicationHistory(HistoryPath).RecordFinishedRuns(StateDirectory);

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(HistoryPath));
        System.Text.Json.JsonElement written = document.RootElement[0];

        Assert.Equal(System.Text.Json.JsonValueKind.Number, written.GetProperty("Transactions").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Number, written.GetProperty("FeeDropsEstimated").ValueKind);
        Assert.False(written.TryGetProperty("Payer", out _));
    }
}
