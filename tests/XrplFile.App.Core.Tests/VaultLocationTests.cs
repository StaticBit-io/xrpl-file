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
}
