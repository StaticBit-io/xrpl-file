using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public sealed class FakeFilePicker : IFilePickerService
{
    public string? PathToReturn { get; set; }

    public byte[]? SavedStream { get; private set; }

    public string? SavedStreamName { get; private set; }

    public bool SaveStreamResult { get; set; } = true;

    /// <summary>When set, <see cref="SaveStreamAsync"/> throws this instead of doing anything else --
    /// the platform picker failing (a full disk, a revoked permission) rather than the person merely
    /// cancelling the dialog.</summary>
    public Exception? SaveStreamThrows { get; set; }

    public Task<bool> SaveFileAsync(string suggestedFileName, byte[] content, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the publish screen never saves a file");

    public async Task<bool> SaveStreamAsync(string suggestedFileName, Stream content, CancellationToken cancellationToken)
    {
        if (SaveStreamThrows is { } failure) throw failure;

        using MemoryStream buffer = new();
        await content.CopyToAsync(buffer, cancellationToken);

        SavedStreamName = suggestedFileName;
        SavedStream = buffer.ToArray();
        return SaveStreamResult;
    }

    public Task<byte[]?> PickFileAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("the publish screen picks a path, not bytes");

    /// <summary>When set, <see cref="PickExistingFilePathAsync"/> throws this: the dialog failing to
    /// open at all, which is what Windows does for a process running as administrator.</summary>
    public Exception? PickThrows { get; set; }

    public Task<string?> PickExistingFilePathAsync(CancellationToken cancellationToken) =>
        PickThrows is { } failure ? Task.FromException<string?>(failure) : Task.FromResult(PathToReturn);
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

    /// <param name="network">
    /// The entry's network. Defaults to <see cref="VaultHarness.Entry"/>'s own default,
    /// <c>"standalone"</c> — a network xrpl-file cannot address, since only tests about that refusal
    /// want it. Tests that need to actually build a request pass a network the format accepts.
    /// </param>
    private async Task<(PublishModel Model, KeyVault Vault, string Reference)> OpenAsync(
        CancellationToken token, INodeSettingsStore? nodes = null, string network = "standalone")
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);
        NewVaultEntry entry = Entry("publishing key") with { Network = network };
        string reference = await vault.StoreAsync(entry, token);

        PublishModel model = new(vault, _picker, nodes ?? new FileNodeSettingsStore(Path.Combine(_files, "nodes.json")));
        await model.RefreshKeysAsync(token);
        return (model, vault, reference);
    }

    [Fact]
    public void ReaderDefaultMaxBytes_is_the_readers_own_default_not_a_copy_of_it()
    {
        // Round 2's N1/M1 finding: this constant was still a bare 20_000_000 after
        // ReadModel.MaxBytes was fixed the same way (ReadModelTests' own
        // MaxBytes_is_the_readers_own_default_not_a_copy_of_it), and the only other test touching
        // it (A_file_past_the_readers_default_limit_is_warned_about_and_still_allowed) compares it
        // to itself, not to the library, so a drift here would have left the suite green.
        Assert.Equal(new ReaderOptions().MaxBytes, PublishModel.ReaderDefaultMaxBytes);
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
        (PublishModel model, _, string reference) =
            await OpenAsync(TestContext.Current.CancellationToken, network: "testnet");
        WriteFile("photo.png", 2048);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        Assert.True(model.CanEstimate);

        PublishRequest? request = await model.BuildRequestAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(request);
        Assert.Equal("photo.png", request.Name);
        Assert.Equal("image/png", request.Type);
        Assert.Equal(2048, request.Size);
        Assert.Equal(reference, request.KeyReference);
        Assert.Equal("testnet", request.Network);
    }

    [Fact]
    public async Task An_unknown_extension_publishes_as_an_octet_stream()
    {
        (PublishModel model, _, string reference) =
            await OpenAsync(TestContext.Current.CancellationToken, network: "testnet");
        WriteFile("notes.wibble", 16);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        PublishRequest? request = await model.BuildRequestAsync(TestContext.Current.CancellationToken);

        Assert.Equal("application/octet-stream", request!.Type);
    }

    [Fact]
    public async Task A_network_with_no_node_url_refuses_to_estimate_and_says_so()
    {
        // A NetworkID is a network xrpl-file can address, but Endpoints.For knows only the three
        // names, so an empty settings store leaves a NetworkID unreachable -- exactly the
        // standalone-stand case the override feature (INodeSettingsStore) exists for, and exactly
        // the case that used to leave Estimate live: pressing it read the whole file into memory
        // and moved to the estimate screen only to have XrplConnectionFactory resolve the very same
        // NodeEndpoint.Resolve and refuse to connect, reporting the misleading "node did not answer"
        // sentence instead of the accurate one already shown right here.
        (PublishModel model, _, string reference) =
            await OpenAsync(TestContext.Current.CancellationToken, network: "1");
        WriteFile("photo.png", 2048);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        Assert.False(model.CanEstimate);
        Assert.Null(model.NodeUrl);
        Assert.Contains("Settings", model.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_whose_network_the_format_cannot_address_stops_the_flow()
    {
        // OpenAsync's default network, "standalone", is exactly what a real local stand's key would
        // carry -- a name the vault happily stores but xrpl-file cannot put in a link.
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);
        WriteFile("photo.png", 2048);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        Assert.False(model.CanEstimate);
        Assert.Null(await model.BuildRequestAsync(TestContext.Current.CancellationToken));
        Assert.Contains("standalone", model.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("xrpl-file link", model.Message ?? string.Empty, StringComparison.Ordinal);

        // The whole point of this screen catching the refusal is that the person never sees the
        // library's own error name -- that is what used to arrive, deep inside a confirmed run, as
        // a bare "InvalidLink". Pin it so a future edit cannot paste it back into the message.
        Assert.DoesNotContain("InvalidLink", model.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_recorded_in_a_different_case_publishes_on_the_canonical_network()
    {
        (PublishModel model, _, string reference) =
            await OpenAsync(TestContext.Current.CancellationToken, network: "TESTNET");
        WriteFile("photo.png", 2048);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        Assert.True(model.CanEstimate);

        PublishRequest? request = await model.BuildRequestAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(request);
        Assert.Equal("testnet", request.Network);
    }

    [Fact]
    public async Task A_key_recorded_in_a_different_case_resolves_the_built_in_node()
    {
        // An empty settings store: whatever NodeUrl finds here comes from Endpoints.For alone, keyed
        // by the canonical name -- not from an override that happens to match the raw entry text.
        (PublishModel model, _, string reference) =
            await OpenAsync(TestContext.Current.CancellationToken, network: "TESTNET");
        WriteFile("photo.png", 2048);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        Assert.Equal(Endpoints.For("testnet")[0], model.NodeUrl);
        Assert.Null(model.Message);
    }

    [Fact]
    public async Task A_file_past_the_readers_default_limit_is_warned_about_and_still_allowed()
    {
        (PublishModel model, _, string reference) =
            await OpenAsync(TestContext.Current.CancellationToken, network: "testnet");

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
        (PublishModel model, KeyVault vault, string reference) =
            await OpenAsync(TestContext.Current.CancellationToken, network: "testnet");
        WriteFile("photo.png", 2048);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        await vault.LockAsync();

        Assert.Null(await model.BuildRequestAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(model.Message);
    }

    [Fact]
    public async Task IsSelected_is_true_only_for_the_chosen_key()
    {
        (PublishModel model, KeyVault vault, string reference) =
            await OpenAsync(TestContext.Current.CancellationToken, network: "testnet");
        NewVaultEntry other = Entry("other key") with { Network = "testnet" };
        string otherReference = await vault.StoreAsync(other, TestContext.Current.CancellationToken);
        await model.RefreshKeysAsync(TestContext.Current.CancellationToken);

        model.SelectKey(reference);

        Assert.True(model.IsSelected(model.Keys.Single(key => key.Reference == reference)));
        Assert.False(model.IsSelected(model.Keys.Single(key => key.Reference == otherReference)));
    }

    [Fact]
    public async Task IsSelected_is_false_for_every_key_before_any_is_chosen()
    {
        // Two keys, not one: with a single key the model now chooses it itself (see
        // With_one_key_it_is_chosen_without_asking), so "before any is chosen" needs a choice to exist.
        (PublishModel model, KeyVault vault, string reference) =
            await OpenAsync(TestContext.Current.CancellationToken, network: "testnet");
        string otherReference = await vault.StoreAsync(
            Entry("other key") with { Network = "testnet" }, TestContext.Current.CancellationToken);
        await model.RefreshKeysAsync(TestContext.Current.CancellationToken);

        Assert.False(model.IsSelected(model.Keys.Single(key => key.Reference == reference)));
        Assert.False(model.IsSelected(model.Keys.Single(key => key.Reference == otherReference)));
    }

    [Fact]
    public async Task With_one_key_it_is_chosen_without_asking()
    {
        (PublishModel model, _, string reference) =
            await OpenAsync(TestContext.Current.CancellationToken, network: "testnet");

        Assert.True(model.IsSelected(model.Keys.Single(key => key.Reference == reference)));
    }

    [Fact]
    public async Task With_two_keys_none_is_chosen_until_the_person_chooses()
    {
        (PublishModel model, KeyVault vault, _) =
            await OpenAsync(TestContext.Current.CancellationToken, network: "testnet");
        await vault.StoreAsync(Entry("other key") with { Network = "testnet" }, TestContext.Current.CancellationToken);

        await model.RefreshKeysAsync(TestContext.Current.CancellationToken);

        Assert.Null(model.SelectedKey);
    }

    [Fact]
    public async Task A_key_the_person_chose_stays_chosen_when_a_second_appears()
    {
        (PublishModel model, KeyVault vault, string reference) =
            await OpenAsync(TestContext.Current.CancellationToken, network: "testnet");
        model.SelectKey(reference);
        await vault.StoreAsync(Entry("other key") with { Network = "testnet" }, TestContext.Current.CancellationToken);

        await model.RefreshKeysAsync(TestContext.Current.CancellationToken);

        Assert.Equal(reference, model.SelectedKey?.Reference);
    }

    [Fact]
    public async Task A_file_dialog_that_fails_says_so_instead_of_throwing()
    {
        (PublishModel model, _, _) = await OpenAsync(TestContext.Current.CancellationToken);
        InvalidOperationException failure = new("no window to attach a dialog to");
        _picker.PickThrows = failure;

        await model.PickFileAsync(TestContext.Current.CancellationToken);

        Assert.Equal("The file dialog could not be opened.", model.Message);
        Assert.Same(failure, model.LastPickerFailure);
        Assert.Null(model.FileName);
    }

    [Fact]
    public async Task A_dialog_that_opens_after_one_that_failed_forgets_the_failure()
    {
        (PublishModel model, _, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _picker.PickThrows = new InvalidOperationException("first attempt");
        await model.PickFileAsync(TestContext.Current.CancellationToken);

        _picker.PickThrows = null;
        WriteFile("second.bin", 10);
        await model.PickFileAsync(TestContext.Current.CancellationToken);

        Assert.Null(model.LastPickerFailure);
        Assert.Equal("second.bin", model.FileName);
    }

    [Fact]
    public async Task Cancelling_the_dialog_is_not_reported_as_a_failure()
    {
        (PublishModel model, _, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _picker.PickThrows = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => model.PickFileAsync(TestContext.Current.CancellationToken));

        Assert.Null(model.LastPickerFailure);
    }
}
