using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class EstimateModelConfirmationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-confirm-").FullName;
    private readonly FakeConnectionFactory _connections = new();
    private readonly FakeFiles _files = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private EstimateModel NewModel() => new(_connections, new RunPaths(_directory), _files.Open);

    private PublishRequest Batch(string path, int size) =>
        new(size, "application/octet-stream", "f.bin", "ref-w", "rWriter", "standalone", "ref-p", "rPayer", _files.Add(path, new byte[size]))
        {
            WriterLabel = "writer",
            PayerLabel = "payer",
        };

    public EstimateModelConfirmationTests()
    {
        _connections.Ledger.Balances["rWriter"] = 1_000_000_000;
        _connections.Ledger.Balances["rPayer"] = 1_000_000_000;
    }

    [Fact]
    public async Task Below_the_threshold_nothing_needs_confirming()
    {
        EstimateModel model = NewModel();

        await model.EstimateAsync(Batch("small.bin", 9_000), Token);

        Assert.False(model.NeedsConfirmation);
        Assert.True(model.CanPublish);
        Assert.NotNull(model.Approval);
        Assert.Equal(["Payer", "Writer"], model.Summary!.Accounts.Select(account => account.Role));
    }

    [Fact]
    public async Task Above_the_threshold_Publish_waits_for_the_checkbox()
    {
        EstimateModel model = NewModel();

        // 5,000 chunks, one list and the manifest: 5,002 memos. At a 30-drop ceiling the payer's worst
        // case is ceil(5,002 x 7.5) = 37,515 units x 30 = 1,125,450 drops.
        await model.EstimateAsync(Batch("large.bin", 5_000_000), Token);

        Assert.True(model.NeedsConfirmation);
        Assert.False(model.CanPublish);
        Assert.Null(model.Approval);
        Assert.Null(model.HandOff());

        model.Confirm(true);

        Assert.True(model.CanPublish);
        Assert.NotNull(model.HandOff());
    }

    [Fact]
    public async Task The_checkbox_resets_when_the_estimate_changes()
    {
        EstimateModel model = NewModel();
        await model.EstimateAsync(Batch("large.bin", 5_000_000), Token);
        model.Confirm(true);

        await model.EstimateAsync(Batch("other.bin", 5_000_000), Token);

        Assert.False(model.Confirmed);
        Assert.False(model.CanPublish);
    }

    [Fact]
    public async Task The_checkbox_resets_when_the_estimate_is_discarded()
    {
        EstimateModel model = NewModel();
        await model.EstimateAsync(Batch("large.bin", 5_000_000), Token);
        model.Confirm(true);

        await model.DiscardAsync();

        Assert.False(model.Confirmed);
    }

    [Fact]
    public void The_label_is_the_specs()
    {
        Assert.Equal("I understand the cost and the time", EstimateModel.ConfirmationLabel);
    }
}
