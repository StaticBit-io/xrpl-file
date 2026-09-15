using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public sealed class FakeFilePicker : IFilePickerService
{
    public string? PathToReturn { get; set; }

    public Task<bool> SaveFileAsync(string suggestedFileName, byte[] content, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the publish screen never saves a file");

    public Task<byte[]?> PickFileAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("the publish screen picks a path, not bytes");

    public Task<string?> PickExistingFilePathAsync(CancellationToken cancellationToken) =>
        Task.FromResult(PathToReturn);
}

public sealed class PublishModelTests : VaultHarness
{
    private readonly FakeFilePicker _picker = new();
    private readonly string _files = Directory.CreateTempSubdirectory("xrpl-file-publish-").FullName;

    /// <inheritdoc/>
    /// <remarks>
    /// Follows <see cref="VaultHarness.DisposeAsync"/>'s own pattern for its own temporary
    /// directory: this class owns a second one, for the files a publication picks rather than the
    /// vault that signs it, and it must not outlive the test run either.
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        Directory.Delete(_files, recursive: true);
        await base.DisposeAsync();
    }

    private string WriteFile(string name, long size)
    {
        string path = Path.Combine(_files, name);
        File.WriteAllBytes(path, new byte[size]);
        _picker.PathToReturn = path;
        return path;
    }

    private async Task<(PublishModel Model, KeyVault Vault, string Reference)> OpenAsync(
        CancellationToken token, INodeSettingsStore? nodes = null)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);
        string reference = await vault.StoreAsync(Entry("publishing key"), token);

        PublishModel model = new(vault, _picker, nodes ?? new FileNodeSettingsStore(Path.Combine(_files, "nodes.json")));
        await model.RefreshKeysAsync(token);
        return (model, vault, reference);
    }

    [Fact]
    public async Task With_nothing_chosen_it_refuses_and_says_what_is_missing()
    {
        (PublishModel model, _, _) = await OpenAsync(TestContext.Current.CancellationToken);

        Assert.False(model.CanEstimate);
        Assert.Null(await model.BuildRequestAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(model.Message);
    }

    [Fact]
    public async Task A_key_with_no_file_is_still_refused()
    {
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);

        model.SelectKey(reference);

        Assert.False(model.CanEstimate);
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);
        WriteFile("empty.bin", 0);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        Assert.False(model.CanEstimate);
        Assert.Contains("empty", model.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_file_and_a_key_build_a_request()
    {
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);
        WriteFile("photo.png", 2048);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        Assert.True(model.CanEstimate);

        PublishRequest? request = await model.BuildRequestAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(request);
        Assert.Equal("photo.png", request.Name);
        Assert.Equal("image/png", request.Type);
        Assert.Equal(2048, request.File.Length);
        Assert.Equal(reference, request.KeyReference);
        Assert.Equal("standalone", request.Network);
    }

    [Fact]
    public async Task An_unknown_extension_publishes_as_an_octet_stream()
    {
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);
        WriteFile("notes.wibble", 16);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        PublishRequest? request = await model.BuildRequestAsync(TestContext.Current.CancellationToken);

        Assert.Equal("application/octet-stream", request!.Type);
    }

    [Fact]
    public async Task A_network_with_no_node_url_is_named_before_anything_is_spent()
    {
        // The harness's entries are on "standalone", which Endpoints.For does not know, so an
        // empty settings store leaves the network unreachable.
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);
        WriteFile("photo.png", 2048);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        Assert.True(model.CanEstimate);
        Assert.Null(model.NodeUrl);
        Assert.Contains("Settings", model.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_past_the_readers_default_limit_is_warned_about_and_still_allowed()
    {
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);

        // Nothing this size is written to disk: the warning is a function of the recorded size.
        model.SelectKey(reference);
        model.DescribeFileForTests("huge.bin", PublishModel.ReaderDefaultMaxBytes + 1);

        Assert.NotNull(model.Warning);
        Assert.True(model.CanEstimate);
    }

    [Fact]
    public async Task A_file_past_the_memory_ceiling_is_refused()
    {
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);

        model.SelectKey(reference);
        model.DescribeFileForTests("enormous.bin", PublishModel.MaxFileBytes + 1);

        Assert.False(model.CanEstimate);
        Assert.NotNull(model.Message);
    }

    [Fact]
    public async Task A_locked_vault_refuses_to_build_a_request()
    {
        (PublishModel model, KeyVault vault, string reference) = await OpenAsync(TestContext.Current.CancellationToken);
        WriteFile("photo.png", 2048);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        await vault.LockAsync();

        Assert.Null(await model.BuildRequestAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(model.Message);
    }
}
