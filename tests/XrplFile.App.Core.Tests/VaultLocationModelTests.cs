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

    // Windows paths are case-insensitive; GetFullPath does not normalise case, so the comparison
    // itself must be case-insensitive or the default path typed with different casing reads as a
    // chosen one. See VaultLocation.IsDefaultPath's own remarks.
    [Fact]
    public void IsDefault_is_true_for_the_default_path_typed_with_different_casing()
    {
        VaultLocationModel model = new(new FakeVaultLocationStore(null), VaultLocation.DefaultPath.ToUpperInvariant());

        Assert.True(model.IsDefault);
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

    [Fact]
    public void ForgetChosenPath_clears_the_store_and_changes_nothing_about_the_current_path()
    {
        FakeVaultLocationStore store = new("C:\\elsewhere\\vault.json");
        VaultLocationModel model = new(store, "C:\\elsewhere\\vault.json");

        model.ForgetChosenPath();

        Assert.True(store.Cleared);
        // Unchanged: a running process never repoints itself, same as ChoosePath.
        Assert.Equal("C:\\elsewhere\\vault.json", model.CurrentPath);
    }

    [Fact]
    public void ChoosePath_reports_a_setting_that_cannot_be_written_instead_of_throwing()
    {
        // The store deliberately lets IOException and UnauthorizedAccessException escape, and both
        // screens that call this model -- including VaultUnreachable.razor, the one a person
        // reaches when their vault is already missing -- used to let them travel straight into the
        // framework's "An unhandled error has occurred."
        RefusingVaultLocationStore store = new(new UnauthorizedAccessException("read-only"));
        VaultLocationModel model = new(store, VaultLocation.DefaultPath);

        VaultLocationModel.Outcome outcome = model.ChoosePath("D:\\backup\\vault.json");

        Assert.False(outcome.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(TestText.En(outcome.Message)));
    }

    [Fact]
    public void ForgetChosenPath_reports_a_setting_that_cannot_be_deleted_instead_of_throwing()
    {
        RefusingVaultLocationStore store = new(new IOException("the disk is full"));
        VaultLocationModel model = new(store, "C:\\elsewhere\\vault.json");

        VaultLocationModel.Outcome outcome = model.ForgetChosenPath();

        Assert.False(outcome.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(TestText.En(outcome.Message)));
    }

    [Fact]
    public void A_refusal_never_names_a_path()
    {
        // The same rule CreateMessages already enforces: nothing a screen shows may carry a path,
        // and the only path a failure here could name is the application's own setting file.
        RefusingVaultLocationStore store = new(new UnauthorizedAccessException("read-only"));
        VaultLocationModel model = new(store, "C:\\elsewhere\\vault.json");

        foreach (string message in new[]
        {
            TestText.En(model.ChoosePath("D:\\backup\\vault.json").Message),
            TestText.En(model.ForgetChosenPath().Message),
        })
        {
            Assert.DoesNotContain("\\", message, StringComparison.Ordinal);
            Assert.DoesNotContain(":", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_successful_choice_and_a_successful_forget_each_say_so()
    {
        FakeVaultLocationStore store = new(null);
        VaultLocationModel model = new(store, VaultLocation.DefaultPath);

        Assert.True(model.ChoosePath("D:\\backup\\vault.json").Succeeded);
        Assert.True(model.ForgetChosenPath().Succeeded);
    }
}

/// <summary>
/// A store whose two writing members throw whatever the test hands it, standing in for the
/// read-only or full profile directory <see cref="FileVaultLocationStore"/> deliberately does not
/// hide.
/// </summary>
internal sealed class RefusingVaultLocationStore(Exception failure) : IVaultLocationStore
{
    public string? ReadChosenPath() => null;

    public void WriteChosenPath(string path) => throw failure;

    public void ClearChosenPath() => throw failure;
}

