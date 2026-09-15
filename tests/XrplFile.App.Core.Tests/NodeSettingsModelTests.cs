using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class NodeSettingsModelTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-nodesettingsmodel-").FullName;

    private string SettingPath => Path.Combine(_directory, "node-urls.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private NodeSettingsModel NewModel() => new(new FileNodeSettingsStore(SettingPath));

    [Fact]
    public void With_nothing_configured_the_three_known_networks_are_listed_with_empty_boxes()
    {
        NodeSettingsModel model = NewModel();
        model.Refresh();

        Assert.Equal(
            ["mainnet", "testnet", "devnet"],
            model.Rows.Select(row => row.Network));
        Assert.All(model.Rows, row => Assert.Equal(string.Empty, row.Url));
    }

    [Fact]
    public void A_network_already_in_the_store_but_not_known_to_Endpoints_gets_its_own_row()
    {
        FileNodeSettingsStore store = new(SettingPath);
        store.Write(new Dictionary<string, string>(StringComparer.Ordinal) { ["standalone"] = "ws://localhost:6006" });

        NodeSettingsModel model = new(store);
        model.Refresh();

        NodeSettingRow row = Assert.Single(model.Rows, row => string.Equals(row.Network, "standalone", StringComparison.Ordinal));
        Assert.Equal("ws://localhost:6006", row.Url);

        // Still exactly the three known networks plus this one -- nothing is dropped and nothing
        // is duplicated.
        Assert.Equal(4, model.Rows.Count);
    }

    [Fact]
    public void An_override_is_shown_on_its_own_row()
    {
        FileNodeSettingsStore store = new(SettingPath);
        store.Write(new Dictionary<string, string>(StringComparer.Ordinal) { ["testnet"] = "ws://localhost:51233" });

        NodeSettingsModel model = new(store);
        model.Refresh();

        Assert.Equal("ws://localhost:51233", model.Rows.Single(row => row.Network == "testnet").Url);
    }

    [Fact]
    public void Saving_a_good_url_writes_it_and_it_reads_back()
    {
        NodeSettingsModel model = NewModel();
        model.Refresh();

        bool saved = model.Save(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mainnet"] = "wss://example.com:51233",
            ["testnet"] = string.Empty,
            ["devnet"] = string.Empty,
        });

        Assert.True(saved);
        Assert.Null(model.Message);
        Assert.Equal("wss://example.com:51233", model.Rows.Single(row => row.Network == "mainnet").Url);
    }

    [Fact]
    public void A_blank_box_is_dropped_rather_than_written_as_an_empty_override()
    {
        FileNodeSettingsStore store = new(SettingPath);
        store.Write(new Dictionary<string, string>(StringComparer.Ordinal) { ["mainnet"] = "wss://example.com" });

        NodeSettingsModel model = new(store);
        model.Refresh();

        Assert.True(model.Save(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mainnet"] = "   ",
            ["testnet"] = string.Empty,
            ["devnet"] = string.Empty,
        }));

        Assert.Empty(store.Read());
        Assert.Equal(string.Empty, model.Rows.Single(row => row.Network == "mainnet").Url);
    }

    [Fact]
    public void A_value_that_is_not_a_websocket_url_is_refused_and_nothing_is_written()
    {
        NodeSettingsModel model = NewModel();
        model.Refresh();

        bool saved = model.Save(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mainnet"] = "https://example.com",
            ["testnet"] = string.Empty,
            ["devnet"] = string.Empty,
        });

        Assert.False(saved);
        Assert.NotNull(model.Message);
        Assert.Contains("mainnet", model.Message, StringComparison.Ordinal);
        Assert.Empty(new FileNodeSettingsStore(SettingPath).Read());
    }

    [Fact]
    public void One_bad_row_refuses_the_whole_save_even_when_another_row_is_fine()
    {
        NodeSettingsModel model = NewModel();
        model.Refresh();

        bool saved = model.Save(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mainnet"] = "wss://good.example.com",
            ["testnet"] = "not a url at all",
            ["devnet"] = string.Empty,
        });

        Assert.False(saved);
        Assert.Empty(new FileNodeSettingsStore(SettingPath).Read());
    }

    [Fact]
    public void A_saved_url_is_trimmed()
    {
        NodeSettingsModel model = NewModel();
        model.Refresh();

        Assert.True(model.Save(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mainnet"] = "  wss://example.com  ",
            ["testnet"] = string.Empty,
            ["devnet"] = string.Empty,
        }));

        Assert.Equal("wss://example.com", new FileNodeSettingsStore(SettingPath).Read()["mainnet"]);
    }

    /// <summary>
    /// The case this model exists to serve: a locally run standalone node, whose network is a
    /// NetworkID that is in neither Endpoints.KnownNetworks nor the store on first use, so Refresh
    /// alone can never produce a row for it.
    /// </summary>
    [Fact]
    public void Adding_a_NetworkID_not_already_listed_creates_its_row_and_writes_it()
    {
        NodeSettingsModel model = NewModel();
        model.Refresh();

        Assert.True(model.AddNetwork("1", "ws://localhost:6006"));
        Assert.Null(model.Message);

        NodeSettingRow row = Assert.Single(model.Rows, row => row.Network == "1");
        Assert.Equal("ws://localhost:6006", row.Url);
        Assert.Equal("ws://localhost:6006", new FileNodeSettingsStore(SettingPath).Read()["1"]);
    }

    [Fact]
    public void Adding_a_network_no_link_can_carry_is_refused_and_nothing_is_written()
    {
        NodeSettingsModel model = NewModel();
        model.Refresh();

        Assert.False(model.AddNetwork("not a network", "ws://localhost:6006"));
        Assert.NotNull(model.Message);
        Assert.Empty(new FileNodeSettingsStore(SettingPath).Read());
    }

    [Fact]
    public void Adding_a_network_with_a_blank_url_is_refused()
    {
        NodeSettingsModel model = NewModel();
        model.Refresh();

        Assert.False(model.AddNetwork("1", "   "));
        Assert.NotNull(model.Message);
        Assert.Empty(new FileNodeSettingsStore(SettingPath).Read());
    }

    [Fact]
    public void Adding_a_network_with_a_bad_url_is_refused()
    {
        NodeSettingsModel model = NewModel();
        model.Refresh();

        Assert.False(model.AddNetwork("1", "https://localhost:6006"));
        Assert.NotNull(model.Message);
        Assert.Empty(new FileNodeSettingsStore(SettingPath).Read());
    }

    [Fact]
    public void Adding_a_network_normalizes_its_case_to_the_canonical_form()
    {
        NodeSettingsModel model = NewModel();
        model.Refresh();

        Assert.True(model.AddNetwork("TESTNET", "wss://example.com"));

        // "testnet" is already a known row -- AddNetwork's canonicalisation must land the override on
        // that same row, not create a second, differently-cased one beside it.
        Assert.Single(model.Rows, row => row.Network == "testnet");
        Assert.Equal("wss://example.com", model.Rows.Single(row => row.Network == "testnet").Url);
    }

    [Fact]
    public void Adding_a_network_preserves_other_existing_overrides()
    {
        FileNodeSettingsStore store = new(SettingPath);
        store.Write(new Dictionary<string, string>(StringComparer.Ordinal) { ["mainnet"] = "wss://existing.example.com" });

        NodeSettingsModel model = new(store);
        model.Refresh();

        Assert.True(model.AddNetwork("2", "ws://localhost:6007"));

        IReadOnlyDictionary<string, string> written = store.Read();
        Assert.Equal("wss://existing.example.com", written["mainnet"]);
        Assert.Equal("ws://localhost:6007", written["2"]);
    }
}
