using Xrpl.Wallet;
using XrplFile.Keys;
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class HeldSourceTests : VaultHarness
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-held-").FullName;
    private readonly FakeConnectionFactory _connections = new();
    private readonly FakeFiles _files = new();

    private RunPaths Paths => new(_directory);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private PublishRequest Picked(string path, int size) =>
        new(size, "application/octet-stream", "file.bin", "ref-1", "rWriter", "standalone", SourcePath: _files.Add(path, new byte[size]));

    private EstimateModel Model(IConnectionFactory? connections = null) => new(connections ?? _connections, Paths, _files.Open);

    [Fact]
    public async Task The_estimate_holds_the_file_and_a_second_estimate_lets_the_first_go()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;
        EstimateModel model = Model();

        await model.EstimateAsync(Picked("a.bin", 4_096), Token);
        await model.EstimateAsync(Picked("b.bin", 4_096), Token);

        Assert.Equal(2, _files.Opened.Count);
        Assert.True(_files.Opened[0].Disposed);
        Assert.False(_files.Opened[1].Disposed);
        Assert.Equal(0, _files.Opened[1].Reads);
    }

    [Fact]
    public async Task Discarding_the_estimate_lets_the_file_go()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;
        EstimateModel model = Model();
        await model.EstimateAsync(Picked("a.bin", 4_096), Token);

        await model.DiscardAsync();

        Assert.True(Assert.Single(_files.Opened).Disposed);
        Assert.Null(model.HandOff());
    }

    [Fact]
    public async Task Handing_off_gives_the_run_the_held_file_and_its_approval()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;
        EstimateModel model = Model();
        await model.EstimateAsync(Picked("a.bin", 4_096), Token);

        PublicationEstimate estimate = model.Estimate!;
        PublishRequest handed = model.HandOff()!;
        await model.DiscardAsync();

        Assert.Same(_files.Opened[0], handed.Source);
        Assert.False(_files.Opened[0].Disposed);
        Assert.Equal(SpendingApproval.For(estimate, "rWriter"), handed.Approval);
        Assert.Equal(4_096, handed.Size);
    }

    [Fact]
    public async Task Leaving_while_the_estimate_is_still_being_taken_lets_the_file_go_when_it_finishes()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;
        GatedConnections gated = new(_connections);
        EstimateModel model = Model(gated);

        Task estimating = model.EstimateAsync(Picked("a.bin", 4_096), Token);
        await gated.Reached.WaitAsync(Token);
        await model.DiscardAsync();
        gated.Release();
        await estimating;

        Assert.True(Assert.Single(_files.Opened).Disposed);
        Assert.Null(model.HandOff());
    }

    [Fact]
    public async Task A_run_disposes_the_file_it_was_handed_when_it_ends()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync();
        CountingPublicationSource held = (CountingPublicationSource)request.Source!;

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.True(held.Disposed);
    }

    [Fact]
    public async Task A_cancelled_run_disposes_the_file_too()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync();
        CountingPublicationSource held = (CountingPublicationSource)request.Source!;
        _connections.ConnectThrows = new OperationCanceledException();

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.True(held.Disposed);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_mid_run_says_so_and_stays_resumable()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync(size: 30_000);

        Assert.True(run.Start(request with { Source = new FailingSecondReadSource(request.Source!, failingChunk: 10) }));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal($"The file at {request.SourcePath} could not be read while it was being published. Make it available again and resume the run.", run.Snapshot.Failure);
        Assert.True(File.Exists(run.Snapshot.StatePath));
        Assert.True(RunModel.IsResumable(run.Snapshot));
    }

    [Fact]
    public async Task A_fresh_run_whose_file_cannot_be_read_while_it_is_hashed_does_not_offer_a_resume()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync(size: 30_000);

        Assert.True(run.Start(request with { Source = new UnreadableSource(request.Source!.Length) }));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal($"The file at {request.SourcePath} could not be read. Make it available and publish it again.", run.Snapshot.Failure);
        Assert.Null(run.Snapshot.StatePath);
        Assert.False(RunModel.IsResumable(run.Snapshot));
    }

    /// <summary>A held file whose drive is gone from the first read on.</summary>
    private sealed class UnreadableSource(long length) : IPublicationSource
    {
        public long Length => length;

        public int ReadChunk(long index, Span<byte> destination) => throw new IOException("fake: the drive the file was on went away");

        public ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken) =>
            throw new IOException("fake: the drive the file was on went away");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_resume_refused_for_another_reason_than_a_changed_file_does_not_mark_the_file()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync(size: 30_000);
        _connections.Sink.Throws[5] = new XrplFileException(XrplFileError.NodeUnreachable, "fake: the node went away");
        Assert.True(run.Start(request));
        await run.Completion;
        string statePath = run.Snapshot.StatePath!;

        // A state recorded for another chunk size is refused with ResumeMismatch, and the file is not to blame.
        PublicationState recorded = PublicationState.Load(statePath);
        (recorded with { Phase = "chunks", Chunk = 999 }).Save(statePath);
        run.Reset();

        Assert.True(run.Resume(statePath, request with { Source = null, SourceRecorded = true }));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.StartsWith(PublishErrorMessages.For(XrplFileError.ResumeMismatch), run.Snapshot.Failure, StringComparison.Ordinal);
        Assert.False(run.HasRejectedSource(statePath));
    }

    [Fact]
    public async Task A_resume_whose_recorded_file_cannot_be_opened_asks_for_it_again_without_a_node()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync(
            size: 30_000, openSource: _ => throw new IOException("fake: the drive the file was on went away"));
        string statePath = Path.Combine(_directory, "recorded.state");

        Assert.True(run.Resume(statePath, request with { Source = null, SourceRecorded = true }));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal($"The file at {request.SourcePath} could not be read. Choose it again.", run.Snapshot.Failure);
        Assert.Equal(0, _connections.Connections);
        Assert.False(run.HasRejectedSource(statePath));
    }

    [Fact]
    public async Task A_fresh_run_needs_a_held_file_and_an_approval()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync();

        Assert.Throws<ArgumentException>(() => run.Start(request with { Source = null }));
        Assert.Throws<ArgumentException>(() => run.Start(request with { Approval = null }));
    }

    [Fact]
    public async Task A_resume_opens_the_recorded_file_through_the_run_and_reads_it_as_a_stream()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync(size: 30_000);
        _connections.Sink.Throws[5] = new XrplFileException(XrplFileError.NodeUnreachable, "fake: the node went away");
        Assert.True(run.Start(request));
        await run.Completion;
        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        string statePath = run.Snapshot.StatePath!;
        _connections.Sink.Throws.Clear();

        UnfinishedModel unfinished = new(Paths, run, new FakeFilePicker());
        UnfinishedRow row = Assert.Single((await RefreshedAsync(unfinished)).Rows);
        Assert.True(await unfinished.ResumeAsync(row, Token));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        CountingPublicationSource reopened = _files.Opened[^1];
        Assert.True(reopened.Disposed);
        Assert.True(reopened.Reads > 0);
        Assert.Equal(statePath, row.Path);
    }

    private static async Task<UnfinishedModel> RefreshedAsync(UnfinishedModel model)
    {
        await model.RefreshAsync(Token);
        return model;
    }

    private async Task<(PublicationRun Run, PublishRequest Request)> RunReadyAsync(
        int size = 4_096, Func<string, IPublicationSource>? openSource = null)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), Token);
        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key", "standalone", wallet.ClassicAddress, wallet.Seed), Token);
        _connections.Ledger.Balances[wallet.ClassicAddress] = 100_000_000;

        // On disk for UnfinishedModel's checks of the recorded path, and in FakeFiles for the run, which counts its reads.
        string path = Path.Combine(_directory, "file.bin");
        byte[] bytes = VectorStream.Bytes("held", size);
        await File.WriteAllBytesAsync(path, bytes, Token);
        _files.Add(path, bytes);

        PublicationRun run = new(_connections, vault, Paths, new PublicationHistory(Paths.HistoryPath), openSource: openSource ?? _files.Open);
        PublishRequest request = Requests.Fresh(bytes, "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1", sourcePath: path)
            with { Source = _files.Open(path) };

        return (run, request);
    }

    /// <summary>Holds the first connection until released, so a test can act while an estimate is in flight.</summary>
    private sealed class GatedConnections(IConnectionFactory inner) : IConnectionFactory
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public void Release() => _release.TrySetResult();

        public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
        {
            _reached.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await inner.ConnectAsync(network, cancellationToken);
        }
    }
}
