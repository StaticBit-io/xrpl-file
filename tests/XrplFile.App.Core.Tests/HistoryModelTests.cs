using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class FakeClipboard : IClipboardService
{
    public string? Copied { get; private set; }

    public TimeSpan? Expiry { get; private set; }

    public Task CopySecretAsync(string value, TimeSpan expiry, CancellationToken cancellationToken)
    {
        Copied = value;
        Expiry = expiry;
        return Task.CompletedTask;
    }

    public Task CopyAsync(string value, CancellationToken cancellationToken)
    {
        Copied = value;
        Expiry = null;
        return Task.CompletedTask;
    }
}

public sealed class HistoryModelTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-historymodel-").FullName;
    private readonly FakeClipboard _clipboard = new();

    private PublicationHistory History => new(Path.Combine(_directory, "publications.json"));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static PublicationRecord Record(string link) =>
        new(link, "photo.png", 4096, "image/png", "testnet", "rWriter", 7, 84, DateTimeOffset.UnixEpoch);

    [Fact]
    public void An_empty_history_shows_nothing_and_says_nothing_is_wrong()
    {
        HistoryModel model = new(History, _clipboard);
        model.Refresh();

        Assert.Empty(model.Records);
        Assert.Null(model.Message);
    }

    [Fact]
    public void A_recorded_publication_is_listed()
    {
        History.Append(Record("xrpl-file://testnet/ABC"));

        HistoryModel model = new(History, _clipboard);
        model.Refresh();

        Assert.Equal("xrpl-file://testnet/ABC", Assert.Single(model.Records).Link);
    }

    [Fact]
    public async Task Copying_a_link_does_not_put_it_on_a_clearing_timer()
    {
        History.Append(Record("xrpl-file://testnet/ABC"));

        HistoryModel model = new(History, _clipboard);
        model.Refresh();
        await model.CopyLinkAsync(model.Records[0], TestContext.Current.CancellationToken);

        Assert.Equal("xrpl-file://testnet/ABC", _clipboard.Copied);
        Assert.Null(_clipboard.Expiry);
    }

    [Fact]
    public void Forgetting_a_record_removes_it_from_the_list_and_the_file()
    {
        PublicationHistory history = History;
        history.Append(Record("xrpl-file://testnet/ABC"));

        HistoryModel model = new(history, _clipboard);
        model.Refresh();
        model.Forget(model.Records[0]);

        Assert.Empty(model.Records);
        Assert.Empty(History.Read());
    }

    [Fact]
    public void Clearing_empties_the_list()
    {
        PublicationHistory history = History;
        history.Append(Record("xrpl-file://testnet/ABC"));
        history.Append(Record("xrpl-file://testnet/DEF"));

        HistoryModel model = new(history, _clipboard);
        model.Clear();

        Assert.Empty(model.Records);
    }
}
