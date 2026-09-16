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
    public void MaxBytes_is_the_readers_own_default_not_a_copy_of_it()
    {
        // The remedy Ruling 8 already chose for this shape (PublishModel.ReaderDefaultMaxBytes is
        // its earlier instance): derive the number rather than let a second literal drift from the
        // library's own. ReadModel.MaxBytes is now `new ReaderOptions().MaxBytes` by construction,
        // so this is a change-detector for exactly one thing -- somebody reintroducing a literal here.
        Assert.Equal(new ReaderOptions().MaxBytes, ReadModel.MaxBytes);
    }

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

    [Fact]
    public async Task Changing_the_link_after_a_successful_lookup_forgets_the_previous_manifest()
    {
        XrplFileLink link = await PublishAsync(_connections, "photo.png", "image/png", 4096);

        ReadModel model = NewModel();
        model.Link = link.ToString();
        await model.LookUpAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(model.Manifest);

        // Editing a character: still not the same text, so this must not leave the previous
        // file's manifest sitting under a link that no longer names it.
        model.Link = link.ToString() + "x";

        Assert.Null(model.Manifest);
        Assert.Null(model.Message);
        Assert.False(model.CanRead);
    }

    [Fact]
    public async Task Reassigning_the_identical_link_text_does_not_blank_a_loaded_manifest()
    {
        XrplFileLink link = await PublishAsync(_connections, "photo.png", "image/png", 4096);

        ReadModel model = NewModel();
        model.Link = link.ToString();
        await model.LookUpAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(model.Manifest);

        // The exact same string, character for character: nothing actually changed, so nothing
        // here should either.
        model.Link = link.ToString();

        Assert.NotNull(model.Manifest);
        Assert.True(model.CanRead);
    }

    [Fact]
    public async Task A_second_lookup_while_one_is_in_flight_is_refused()
    {
        GatedConnectFactory gated = new(_connections);
        ReadModel model = new(gated, new FileNodeSettingsStore(Path.Combine(_directory, "node-urls.json")));
        model.Link = "xrpl-file://testnet/" + new string('A', 64);

        Task first = model.LookUpAsync(TestContext.Current.CancellationToken);
        await gated.Reached;

        // The first call is held at the node; a second call, for the same link, must not itself
        // reach the node at all -- it is refused outright while the first is still running.
        await model.LookUpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, gated.Attempts);
        Assert.False(first.IsCompleted);

        gated.Release();
        await first;

        Assert.Equal(1, gated.Attempts);
        Assert.Equal(ReadErrorMessages.For(XrplFileError.ManifestNotFound), model.Message);
    }

    [Fact]
    public async Task A_lookup_that_finishes_after_the_link_changed_is_discarded()
    {
        XrplFileLink link = await PublishAsync(_connections, "photo.png", "image/png", 4096);

        GatedConnectFactory gated = new(_connections);
        ReadModel model = new(gated, new FileNodeSettingsStore(Path.Combine(_directory, "node-urls.json")));
        model.Link = link.ToString();

        Task lookup = model.LookUpAsync(TestContext.Current.CancellationToken);
        await gated.Reached;

        // The person retypes over the link while the node is still being asked about the old one.
        model.Link = "xrpl-file://testnet/" + new string('B', 64);

        gated.Release();
        await lookup;

        // The old lookup did fetch a real manifest -- gated.Attempts proves it reached the node --
        // but it must not have been allowed to land, because the link it was for is not the one
        // shown any more.
        Assert.Equal(1, gated.Attempts);
        Assert.Null(model.Manifest);
        Assert.False(model.CanRead);
    }

    /// <summary>
    /// Holds every <see cref="ConnectAsync"/> call open until <see cref="Release"/> is called, so a
    /// test can observe <see cref="ReadModel.LookUpAsync"/> paused mid-flight -- past the point it
    /// has committed to a link and a generation, but before the node has answered -- without racing
    /// the wall clock. Gates the connection itself, one step earlier than
    /// <c>GatedSubmission.cs</c>'s <c>GatedSubmitSink</c> gates a submission: this model never
    /// submits anything, so the only node round trip it makes at all is the connection request.
    /// </summary>
    private sealed class GatedConnectFactory(IConnectionFactory inner) : IConnectionFactory
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many times <see cref="ConnectAsync"/> was actually called.</summary>
        public int Attempts { get; private set; }

        /// <summary>Resolves the instant a call reaches the gate, before it waits to be released.</summary>
        public Task Reached => _reached.Task;

        /// <summary>Lets a call gated here proceed.</summary>
        public void Release() => _release.TrySetResult();

        public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
        {
            Attempts++;
            _reached.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return await inner.ConnectAsync(network, cancellationToken).ConfigureAwait(false);
        }
    }
}
