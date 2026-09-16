using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class ReadModelTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-read-").FullName;
    private readonly FakeConnectionFactory _connections = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ReadModel NewModel() =>
        new(_connections, new FileNodeSettingsStore(Path.Combine(_directory, "node-urls.json")));

    [Fact]
    public async Task A_link_that_does_not_parse_is_refused_without_asking_a_node()
    {
        ReadModel model = NewModel();
        model.Link = "not a link";

        await model.LookUpAsync(TestContext.Current.CancellationToken);

        Assert.Null(model.Manifest);
        Assert.False(model.CanRead);
        Assert.Equal(ReadErrorMessages.For(XrplFileError.InvalidLink), model.Message);
        Assert.Equal(0, _connections.Connections);
    }

    [Fact]
    public async Task A_network_with_no_node_url_is_refused_and_names_settings()
    {
        ReadModel model = NewModel();
        model.Link = "xrpl-file://1/" + new string('A', 64);

        await model.LookUpAsync(TestContext.Current.CancellationToken);

        Assert.False(model.CanRead);
        Assert.Contains("Settings", model.Message!, StringComparison.Ordinal);
        Assert.Equal(0, _connections.Connections);
    }

    [Fact]
    public async Task A_node_that_has_no_such_transaction_says_so_in_the_readers_words()
    {
        ReadModel model = NewModel();
        model.Link = "xrpl-file://testnet/" + new string('A', 64);

        await model.LookUpAsync(TestContext.Current.CancellationToken);

        Assert.Null(model.Manifest);
        Assert.False(model.CanRead);
        Assert.Equal(ReadErrorMessages.For(XrplFileError.ManifestNotFound), model.Message);
    }

    [Fact]
    public async Task A_published_file_reads_back_its_manifest()
    {
        XrplFileLink link = await PublishAsync(_connections, "photo.png", "image/png", 4096);

        ReadModel model = NewModel();
        model.Link = link.ToString();

        await model.LookUpAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(model.Manifest);
        Assert.Equal("photo.png", model.Manifest.Manifest.Name);
        Assert.Equal("image/png", model.Manifest.Manifest.Type);
        Assert.Equal(4096, model.Manifest.Manifest.Size);
        Assert.True(model.CanRead);
        Assert.Null(model.Message);
    }

    [Fact]
    public async Task A_file_past_the_ceiling_is_refused_before_a_block_is_fetched()
    {
        // The manifest is read, so the size is known before anything large is moved. The ceiling
        // check belongs here rather than in the run, so the person is told before they agree.
        XrplFileLink link = await PublishAsync(_connections, "big.bin", "application/octet-stream", 4096);

        ReadModel model = NewModel();
        model.SetCeilingForTests(1024);
        model.Link = link.ToString();

        await model.LookUpAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(model.Manifest);
        Assert.False(model.CanRead);
        Assert.Equal(ReadErrorMessages.For(XrplFileError.TooLarge), model.Message);
    }

    [Fact]
    public async Task Clearing_forgets_the_manifest_and_the_message()
    {
        ReadModel model = NewModel();
        model.Link = "not a link";
        await model.LookUpAsync(TestContext.Current.CancellationToken);

        model.Clear();

        Assert.Null(model.Message);
        Assert.Null(model.Manifest);
        Assert.False(model.CanRead);
    }
}
