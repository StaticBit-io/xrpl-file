using XrplFile.App.Core.Tutorial;
using Xunit;

namespace XrplFile.App.Core.Tests.Tutorial;

public sealed class FileTutorialSettingsStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-tutorial-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Missing_file_was_not_offered()
    {
        Assert.False(new FileTutorialSettingsStore(Path.Combine(_directory, "tutorial-offered.txt")).WasOffered());
    }

    [Fact]
    public void Marked_was_offered()
    {
        string path = Path.Combine(_directory, "nested", "tutorial-offered.txt");

        Assert.True(new FileTutorialSettingsStore(path).TryMarkOffered());

        Assert.True(new FileTutorialSettingsStore(path).WasOffered());
        Assert.Equal("offered", File.ReadAllText(path).Trim());
    }

    [Fact]
    public void An_empty_or_unreadable_file_counts_as_offered()
    {
        string path = Path.Combine(_directory, "tutorial-offered.txt");
        File.WriteAllText(path, string.Empty);

        Assert.True(new FileTutorialSettingsStore(path).WasOffered());
    }

    [Fact]
    public void A_failed_write_reports_false()
    {
        string blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "x");

        bool marked = new FileTutorialSettingsStore(Path.Combine(blocker, "tutorial-offered.txt")).TryMarkOffered();

        Assert.False(marked);
    }
}
