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
}

/// <summary>An <see cref="IVaultLocationStore"/> that answers with a fixed path and never touches disk.</summary>
internal sealed class FakeVaultLocationStore(string? chosenPath) : IVaultLocationStore
{
    public List<string> Written { get; } = [];

    public string? ReadChosenPath() => chosenPath;

    public void WriteChosenPath(string path) => Written.Add(path);
}
