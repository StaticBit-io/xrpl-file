using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class EstimateModelTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-estimate-").FullName;
    private readonly FakeConnectionFactory _connections = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private EstimateModel NewModel() => new(_connections, new RunPaths(_directory));

    private static PublishRequest Request(int size = 4096) =>
        new(new byte[size], "application/octet-stream", "file.bin", "ref-1", "rWriter", "standalone");

    [Fact]
    public async Task A_funded_account_gets_an_estimate_it_can_publish_from()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.NotNull(model.Estimate);
        Assert.True(model.CanPublish);
        Assert.Equal("fake://node", model.NodeUrl);
        Assert.Null(model.Message);
    }

    [Fact]
    public async Task An_account_that_cannot_cover_it_is_told_what_is_held_and_what_is_needed()
    {
        _connections.Ledger.Balances["rWriter"] = 1;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.NotNull(model.Estimate);
        Assert.False(model.CanPublish);
        Assert.NotNull(model.Message);
    }

    [Fact]
    public async Task A_node_that_does_not_answer_says_so_in_its_own_words()
    {
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Null(model.Estimate);
        Assert.False(model.CanPublish);
        Assert.Equal(PublishErrorMessages.For(XrplFileError.NodeUnreachable), model.Message);
    }

    [Fact]
    public async Task The_connection_is_closed_whatever_happened()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(_connections.Connections, _connections.Disposals);
    }

    [Fact]
    public async Task A_second_estimate_replaces_the_first()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(size: 4096), TestContext.Current.CancellationToken);
        long first = model.Estimate!.Chunks;

        await model.EstimateAsync(Request(size: 40_960), TestContext.Current.CancellationToken);

        Assert.True(model.Estimate!.Chunks > first);
    }
}
