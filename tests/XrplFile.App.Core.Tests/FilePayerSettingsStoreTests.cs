using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class FilePayerSettingsStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-payers-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void The_last_payer_is_remembered_per_network()
    {
        string path = Path.Combine(_directory, "payers.json");
        new FilePayerSettingsStore(path).Remember("testnet", "ref-a");
        new FilePayerSettingsStore(path).Remember("devnet", "ref-b");
        new FilePayerSettingsStore(path).Remember("testnet", "ref-c");

        FilePayerSettingsStore store = new(path);
        Assert.Equal("ref-c", store.LastPayer("testnet"));
        Assert.Equal("ref-b", store.LastPayer("devnet"));
        Assert.Null(store.LastPayer("mainnet"));
    }

    [Fact]
    public void A_settings_file_that_cannot_be_read_remembers_nothing()
    {
        string path = Path.Combine(_directory, "payers.json");
        File.WriteAllText(path, "not json");

        Assert.Null(new FilePayerSettingsStore(path).LastPayer("testnet"));
    }
}
