using Xunit;

namespace XrplFile.App.Core.Tests;

public class VaultLocationTests
{
    [Fact]
    public void The_default_path_is_under_the_applications_data_directory()
    {
        string path = VaultLocation.DefaultPath;

        Assert.EndsWith(VaultLocation.FileName, path, StringComparison.Ordinal);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            path,
            StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(path), path);   // the device secret is keyed by this string
    }

    [Fact]
    public void Resolve_returns_the_default_path_when_the_store_has_nothing_chosen()
    {
        string resolved = VaultLocation.Resolve(new FakeVaultLocationStore(null));

        Assert.Equal(VaultLocation.DefaultPath, resolved);
    }

    [Fact]
    public void Resolve_returns_the_stores_chosen_path_normalised()
    {
        string relative = Path.Combine(".", "elsewhere", "vault.json");

        string resolved = VaultLocation.Resolve(new FakeVaultLocationStore(relative));

        Assert.Equal(Path.GetFullPath(relative), resolved);
        Assert.NotEqual(VaultLocation.DefaultPath, resolved);
    }

    [Fact]
    public void Resolve_throws_for_a_null_store()
    {
        Assert.Throws<ArgumentNullException>(() => VaultLocation.Resolve(null!));
    }

    [Fact]
    public void IsDefaultPath_is_true_for_the_default_path_itself()
    {
        Assert.True(VaultLocation.IsDefaultPath(VaultLocation.DefaultPath));
    }

    [Fact]
    public void IsDefaultPath_is_false_for_a_chosen_path()
    {
        Assert.False(VaultLocation.IsDefaultPath(Path.Combine(Path.GetTempPath(), "elsewhere", "vault.json")));
    }

    // Windows paths are case-insensitive (NTFS is case-preserving, not case-sensitive), and this
    // application targets Windows only. Path.GetFullPath normalises "." / ".." segments but never
    // letter case, so the default path typed with different casing must still compare as the
    // default path -- see VaultLocation.IsDefaultPath's own remarks.
    [Fact]
    public void IsDefaultPath_ignores_case()
    {
        Assert.True(VaultLocation.IsDefaultPath(VaultLocation.DefaultPath.ToUpperInvariant()));
        Assert.True(VaultLocation.IsDefaultPath(VaultLocation.DefaultPath.ToLowerInvariant()));
    }
}

/// <summary>An <see cref="IVaultLocationStore"/> that answers with a fixed path and never touches disk.</summary>
internal sealed class FakeVaultLocationStore(string? chosenPath) : IVaultLocationStore
{
    private string? _chosenPath = chosenPath;

    public List<string> Written { get; } = [];

    public bool Cleared { get; private set; }

    public string? ReadChosenPath() => _chosenPath;

    public void WriteChosenPath(string path)
    {
        Written.Add(path);
        _chosenPath = path;
    }

    public void ClearChosenPath()
    {
        Cleared = true;
        _chosenPath = null;
    }
}
