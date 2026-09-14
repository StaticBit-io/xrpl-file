using Xunit;

namespace XrplFile.App.Core.Tests;

public class FileVaultLocationStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-app-location-store-").FullName;

    private string SettingPath => Path.Combine(_directory, "vault-location.txt");

    [Fact]
    public void ReadChosenPath_returns_null_when_no_setting_file_exists()
    {
        FileVaultLocationStore store = new(SettingPath);

        Assert.Null(store.ReadChosenPath());
    }

    [Fact]
    public void WriteChosenPath_then_ReadChosenPath_round_trips_the_normalised_path()
    {
        FileVaultLocationStore store = new(SettingPath);
        string relative = Path.Combine(_directory, ".", "vault.json");

        store.WriteChosenPath(relative);

        Assert.Equal(Path.GetFullPath(relative), store.ReadChosenPath());
    }

    [Fact]
    public void WriteChosenPath_creates_the_settings_own_directory_when_it_does_not_exist_yet()
    {
        string nestedSettingPath = Path.Combine(_directory, "nested", "vault-location.txt");
        FileVaultLocationStore store = new(nestedSettingPath);

        store.WriteChosenPath(Path.Combine(_directory, "vault.json"));

        Assert.True(File.Exists(nestedSettingPath));
    }

    [Fact]
    public void ReadChosenPath_returns_null_for_a_blank_setting_file()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingPath, "   ");
        FileVaultLocationStore store = new(SettingPath);

        Assert.Null(store.ReadChosenPath());
    }

    [Fact]
    public void WriteChosenPath_throws_for_a_null_or_empty_path()
    {
        FileVaultLocationStore store = new(SettingPath);

        Assert.Throws<ArgumentException>(() => store.WriteChosenPath(""));
    }

    [Fact]
    public void ClearChosenPath_removes_a_previously_written_setting()
    {
        FileVaultLocationStore store = new(SettingPath);
        store.WriteChosenPath(Path.Combine(_directory, "vault.json"));

        store.ClearChosenPath();

        Assert.Null(store.ReadChosenPath());
        Assert.False(File.Exists(SettingPath));
    }

    [Fact]
    public void ClearChosenPath_is_a_no_op_when_nothing_was_ever_chosen()
    {
        FileVaultLocationStore store = new(SettingPath);

        store.ClearChosenPath();

        Assert.Null(store.ReadChosenPath());
    }

    [Fact]
    public void The_default_constructor_places_the_setting_beside_the_default_vault()
    {
        FileVaultLocationStore store = new();

        // Not exercised end to end here (that would touch this machine's real application-data
        // directory); this only pins where the parameterless constructor computes the setting path
        // to be, the same way VaultLocationTests pins VaultLocation.DefaultPath's own shape.
        string expected = Path.Combine(Path.GetDirectoryName(VaultLocation.DefaultPath)!, "vault-location.txt");
        Assert.Equal(expected, store.SettingPath);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
