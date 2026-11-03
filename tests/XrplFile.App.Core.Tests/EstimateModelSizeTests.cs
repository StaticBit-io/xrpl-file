using XrplFile.App.Core.Localization;
using Xunit;

namespace XrplFile.App.Core.Tests;

/// <summary>Spec S1, S16: the size policy holds for the file the estimate opens, whatever the picker measured earlier.</summary>
public sealed class EstimateModelSizeTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-estimate-size-").FullName;
    private readonly FakeConnectionFactory _connections = new();
    private readonly List<LengthOnlySource> _opened = [];

    public EstimateModelSizeTests()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000_000;
        _connections.Ledger.Balances["rPayer"] = 100_000_000_000;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A file of <paramref name="openedLength"/> bytes, picked as the 1 KiB the request still says.</summary>
    private EstimateModel Model(long openedLength) => new(
        _connections,
        new RunPaths(_directory),
        _ =>
        {
            LengthOnlySource source = new(openedLength);
            _opened.Add(source);
            return source;
        });

    private static PublishRequest Request(bool withPayer) =>
        withPayer
            ? new PublishRequest(1024, "application/octet-stream", "big.bin", "ref-w", "rWriter", "standalone", "ref-p", "rPayer", "big.bin")
            : new PublishRequest(1024, "application/octet-stream", "big.bin", "ref-w", "rWriter", "standalone", SourcePath: "big.bin");

    [Fact]
    public async Task A_file_that_grew_past_64_MiB_with_no_payer_is_refused_before_anything_is_estimated()
    {
        EstimateModel model = Model(64 * MiB + 1);

        await model.EstimateAsync(Request(withPayer: false), Token);

        Assert.Equal("Choose a payer — files larger than 64 MiB publish through Batch.", TestText.En(model.Message));
        Assert.False(model.CanPublish);
        Assert.Null(model.Estimate);
        Assert.Null(model.Approval);
        Assert.Null(model.HandOff());
        Assert.Equal(0, _connections.Connections);
        Assert.True(_opened.Single().Disposed);
    }

    [Fact]
    public async Task A_file_that_grew_past_16_GiB_is_refused_even_with_a_payer()
    {
        EstimateModel model = Model(16L * 1024 * MiB + 1);

        await model.EstimateAsync(Request(withPayer: true), Token);

        Assert.Equal("This application publishes files up to 16 GiB.", TestText.En(model.Message));
        Assert.False(model.CanPublish);
        Assert.Null(model.BatchEstimate);
        Assert.Equal(0, _connections.Connections);
        Assert.True(_opened.Single().Disposed);
    }

    [Fact]
    public async Task A_file_above_64_MiB_with_a_payer_is_estimated_through_Batch()
    {
        EstimateModel model = Model(100 * MiB);

        await model.EstimateAsync(Request(withPayer: true), Token);

        Assert.NotNull(model.BatchEstimate);
        Assert.Null(model.Message);
        Assert.False(_opened.Single().Disposed);
    }

    [Fact]
    public async Task A_file_of_exactly_64_MiB_with_no_payer_is_not_refused_for_its_size()
    {
        EstimateModel model = Model(64 * MiB);

        await model.EstimateAsync(Request(withPayer: false), Token);

        Assert.NotEqual(UiText.Of(PublishText.PayerRequired), model.Message);
        Assert.NotNull(model.Estimate);
    }

    /// <summary>A file known only by its length; the estimate never reads it.</summary>
    private sealed class LengthOnlySource(long length) : IPublicationSource
    {
        private int _disposed;

        public long Length => length;

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public int ReadChunk(long index, Span<byte> destination) => throw new InvalidOperationException("an estimate reads nothing");

        public ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("an estimate reads nothing");

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }
}
