using System.Text;

using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class NodeSettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-nodes-").FullName;

    private string SettingPath => Path.Combine(_directory, "node-urls.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void An_absent_file_reads_as_no_overrides()
    {
        FileNodeSettingsStore store = new(SettingPath);

        Assert.Empty(store.Read());
    }

    [Fact]
    public void A_written_override_reads_back()
    {
        FileNodeSettingsStore store = new(SettingPath);

        store.Write(new Dictionary<string, string>(StringComparer.Ordinal) { ["standalone"] = "ws://localhost:6006" });

        Assert.Equal("ws://localhost:6006", store.Read()["standalone"]);
    }

    [Fact]
    public void A_file_that_is_not_json_reads_as_no_overrides()
    {
        File.WriteAllBytes(SettingPath, Encoding.UTF8.GetBytes("this is not json"));

        Assert.Empty(new FileNodeSettingsStore(SettingPath).Read());
    }

    [Fact]
    public void An_override_wins_over_the_known_endpoint()
    {
        FileNodeSettingsStore store = new(SettingPath);
        store.Write(new Dictionary<string, string>(StringComparer.Ordinal) { ["testnet"] = "ws://localhost:6006" });

        Assert.Equal("ws://localhost:6006", NodeEndpoint.Resolve(store, "testnet"));
    }

    [Fact]
    public void A_known_network_with_no_override_resolves_to_its_first_endpoint()
    {
        Assert.Equal(Endpoints.For("mainnet")[0], NodeEndpoint.Resolve(new FileNodeSettingsStore(SettingPath), "mainnet"));
    }

    [Fact]
    public void A_network_with_neither_resolves_to_nothing()
    {
        Assert.Null(NodeEndpoint.Resolve(new FileNodeSettingsStore(SettingPath), "standalone"));
    }

    [Fact]
    public void A_blank_override_is_no_override()
    {
        FileNodeSettingsStore store = new(SettingPath);
        store.Write(new Dictionary<string, string>(StringComparer.Ordinal) { ["mainnet"] = "   " });

        Assert.Equal(Endpoints.For("mainnet")[0], NodeEndpoint.Resolve(store, "mainnet"));
    }

    [Fact]
    public void The_default_constructor_places_the_setting_beside_the_default_vault()
    {
        FileNodeSettingsStore store = new();

        // Not exercised end to end here (that would touch this machine's real application-data
        // directory); this only pins where the parameterless constructor computes the setting path
        // to be, the same way VaultLocationTests pins VaultLocation.DefaultPath's own shape.
        string expected = Path.Combine(Path.GetDirectoryName(VaultLocation.DefaultPath)!, "node-urls.json");
        Assert.Equal(expected, store.SettingPath);
    }

    [Fact]
    public void An_unreadable_setting_is_left_on_disk_rather_than_quietly_deleted()
    {
        // Forgetting which endpoints somebody configured is not this method's decision to make: the
        // bytes may still be recoverable by hand, and the settings screen can overwrite them.
        File.WriteAllBytes(SettingPath, Encoding.UTF8.GetBytes("this is not json"));

        Assert.Empty(new FileNodeSettingsStore(SettingPath).Read());
        Assert.True(File.Exists(SettingPath));
    }
}
