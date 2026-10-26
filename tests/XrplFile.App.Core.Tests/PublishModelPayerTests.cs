using XrplFile.Keys;
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class PublishModelPayerTests : VaultHarness
{
    private readonly FakeFilePicker _picker = new();
    private readonly FakeConnectionFactory _connections = new();
    private readonly string _files = Directory.CreateTempSubdirectory("xrpl-file-paid-by-").FullName;

    public override async ValueTask DisposeAsync()
    {
        Directory.Delete(_files, recursive: true);
        await base.DisposeAsync();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private FilePayerSettingsStore Payers => new(Path.Combine(_files, "payers.json"));

    private async Task<(PublishModel Model, Dictionary<string, string> References)> OpenAsync(params (string Label, string Network, string? Address)[] keys)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), Token);
        Dictionary<string, string> references = [];

        foreach ((string label, string network, string? address) in keys)
        {
            NewVaultEntry entry = Entry(label) with { Network = network, Address = address ?? "rAddressOf" + label };
            references[label] = await vault.StoreAsync(entry, Token);
        }

        PublishModel model = new(
            vault, _picker, new FileNodeSettingsStore(Path.Combine(_files, "nodes.json")),
            new BatchAvailability(_connections, new ManualTimeProvider()), Payers);
        await model.RefreshKeysAsync(Token);

        return (model, references);
    }

    [Fact]
    public async Task Paid_by_lists_the_other_keys_on_the_writers_network_and_never_the_writer()
    {
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(
            ("writer", "testnet", "rWriter"), ("upper", "TESTNET", null), ("twin", "Testnet", "rWriter"), ("elsewhere", "devnet", null));

        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);

        Assert.Equal(["upper"], model.PayerChoices.Select(key => key.Label).ToArray());
    }

    [Fact]
    public async Task The_default_payer_is_the_last_one_used_on_that_network_then_the_first_other_key_then_nobody()
    {
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(
            ("writer", "testnet", null), ("first", "testnet", null), ("second", "testnet", null), ("alone", "devnet", null));

        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);
        Assert.Equal(refs["first"], model.SelectedPayerReference);

        Payers.Remember("testnet", refs["second"]);
        model.SelectKey(refs["first"]);
        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);
        Assert.Equal(refs["second"], model.SelectedPayerReference);

        model.SelectKey(refs["alone"]);
        await model.RefreshPayerAsync(Token);
        Assert.Null(model.SelectedPayerReference);
        Assert.Equal(PublishModel.NobodyNote, model.PayerNote);
    }

    [Fact]
    public async Task A_network_without_Batch_fixes_Paid_by_on_Nobody_and_says_why()
    {
        _connections.Ledger.EnabledAmendments.Clear();
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(("writer", "testnet", null), ("payer", "testnet", null));

        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);

        Assert.True(model.PayerLocked);
        Assert.Null(model.SelectedPayer);
        Assert.Equal("This network does not support Batch, so publishing uses tickets.", model.PayerNote);
    }

    [Fact]
    public async Task A_failed_amendment_read_leaves_Paid_by_usable()
    {
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "down");
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(("writer", "testnet", null), ("payer", "testnet", null));

        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);

        Assert.Null(model.BatchEnabled);
        Assert.False(model.PayerLocked);
        Assert.Equal(refs["payer"], model.SelectedPayerReference);
    }

    [Fact]
    public async Task Choosing_Nobody_shows_its_muted_note_and_the_request_carries_no_payer()
    {
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(("writer", "testnet", null), ("payer", "testnet", null));
        string path = Path.Combine(_files, "f.bin");
        File.WriteAllBytes(path, new byte[2000]);
        _picker.PathToReturn = path;
        await model.PickFileAsync(Token);

        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);
        model.SelectPayer(null);

        Assert.Equal("Slower, and holds up to 50 XRP of reserve while publishing.", model.PayerNote);
        PublishRequest request = (await model.BuildRequestAsync(Token))!;
        Assert.Null(request.PayerReference);
        Assert.Equal(path, request.SourcePath);
    }

    [Fact]
    public async Task Starting_a_run_puts_its_file_away_and_keeps_the_key_and_the_payer()
    {
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(("writer", "testnet", null), ("payer", "testnet", null));
        string first = Path.Combine(_files, "first.bin");
        string second = Path.Combine(_files, "second.bin");
        File.WriteAllBytes(first, new byte[2000]);
        File.WriteAllBytes(second, new byte[3000]);
        _picker.PathToReturn = first;
        await model.PickFileAsync(Token);
        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);

        model.RecordStarted((await model.BuildRequestAsync(Token))!);

        Assert.Null(model.FilePath);
        Assert.Null(model.FileName);
        Assert.Equal(0, model.FileSize);
        Assert.Null(model.Message);
        Assert.Null(model.Warning);
        Assert.False(model.CanEstimate);
        Assert.Equal(refs["writer"], model.SelectedReference);
        Assert.Equal(refs["payer"], model.SelectedPayerReference);

        _picker.PathToReturn = second;
        await model.PickFileAsync(Token);
        PublishRequest next = (await model.BuildRequestAsync(Token))!;

        Assert.Equal("second.bin", next.Name);
        Assert.Equal(3000, next.Size);
    }

    [Fact]
    public async Task A_request_with_a_payer_names_both_accounts_and_starting_it_remembers_the_payer()
    {
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(("writer", "testnet", null), ("payer", "testnet", null));
        string path = Path.Combine(_files, "f.bin");
        File.WriteAllBytes(path, new byte[2000]);
        _picker.PathToReturn = path;
        await model.PickFileAsync(Token);
        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);

        PublishRequest request = (await model.BuildRequestAsync(Token))!;
        model.RecordStarted(request);

        Assert.Equal(refs["payer"], request.PayerReference);
        Assert.Equal("rAddressOfpayer", request.PayerAccount);
        Assert.Equal("writer", request.WriterLabel);
        Assert.Equal("payer", request.PayerLabel);
        Assert.Equal(refs["payer"], Payers.LastPayer("testnet"));
    }
}
