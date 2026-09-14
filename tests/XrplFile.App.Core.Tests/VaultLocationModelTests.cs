using Xunit;

namespace XrplFile.App.Core.Tests;

public class VaultLocationModelTests
{
    [Fact]
    public void CurrentPath_reports_whatever_it_was_constructed_with()
    {
        VaultLocationModel model = new(new FakeVaultLocationStore(null), "C:\\elsewhere\\vault.json");

        Assert.Equal("C:\\elsewhere\\vault.json", model.CurrentPath);
    }

    [Fact]
    public void IsDefault_is_true_when_the_current_path_is_the_default_path()
    {
        VaultLocationModel model = new(new FakeVaultLocationStore(null), VaultLocation.DefaultPath);

        Assert.True(model.IsDefault);
    }

    [Fact]
    public void IsDefault_is_false_for_a_chosen_path()
    {
        VaultLocationModel model = new(new FakeVaultLocationStore(null), "C:\\elsewhere\\vault.json");

        Assert.False(model.IsDefault);
    }

    [Fact]
    public void ChoosePath_writes_through_to_the_store_and_changes_nothing_about_the_current_path()
    {
        FakeVaultLocationStore store = new(null);
        VaultLocationModel model = new(store, VaultLocation.DefaultPath);

        model.ChoosePath("D:\\backup\\vault.json");

        Assert.Equal(["D:\\backup\\vault.json"], store.Written);
        // Unchanged: a running process never repoints itself (see the model's own remarks).
        Assert.Equal(VaultLocation.DefaultPath, model.CurrentPath);
        Assert.True(model.IsDefault);
    }
}
