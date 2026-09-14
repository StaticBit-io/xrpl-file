using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public class PasswordChangeModelDescribeTests
{
    [Theory]
    [InlineData(VaultPasswordChange.Changed, 1)]
    [InlineData(VaultPasswordChange.Changed | VaultPasswordChange.DeviceUnlockDropped, 2)]
    [InlineData(VaultPasswordChange.Changed | VaultPasswordChange.TheOldCopyRemains, 2)]
    [InlineData(VaultPasswordChange.Changed | VaultPasswordChange.TheRecordsIntegrityCodeDidNotMatch, 2)]
    [InlineData(
        VaultPasswordChange.Changed | VaultPasswordChange.DeviceUnlockDropped
        | VaultPasswordChange.TheOldCopyRemains | VaultPasswordChange.TheRecordsIntegrityCodeDidNotMatch, 4)]
    public void Every_flag_that_is_set_is_something_the_user_is_told(VaultPasswordChange outcome, int expected)
    {
        // Each of these exists because silence about it misled somebody. A screen that says only
        // "done" puts the silence back.
        PasswordChangeReport report = PasswordChangeModel.Describe(outcome, vaultPath: @"C:\vault\vault.json");

        Assert.True(report.Changed);
        Assert.Equal(expected, report.ThingsToTell.Count);
    }

    [Fact]
    public void A_wrong_old_password_is_not_a_change()
    {
        PasswordChangeReport report = PasswordChangeModel.Describe(VaultPasswordChange.WrongPassword, @"C:\vault\vault.json");

        Assert.False(report.Changed);
        Assert.Empty(report.ThingsToTell);
        Assert.Null(report.SurvivingCopyPath);
    }

    [Fact]
    public void The_surviving_copy_is_named_when_there_is_one()
    {
        PasswordChangeReport report = PasswordChangeModel.Describe(
            VaultPasswordChange.Changed | VaultPasswordChange.TheOldCopyRemains, @"C:\vault\vault.json");

        Assert.Equal(@"C:\vault\vault.json.bak", report.SurvivingCopyPath);
    }

    [Fact]
    public void No_surviving_copy_is_named_when_the_flag_is_not_set()
    {
        PasswordChangeReport report = PasswordChangeModel.Describe(VaultPasswordChange.Changed, @"C:\vault\vault.json");

        Assert.Null(report.SurvivingCopyPath);
    }

    [Fact]
    public void Changed_alone_says_only_that_the_password_changed()
    {
        // A failing sentence-count test only says "expected N, got M" — this pins the actual text
        // for the simplest case, so a reader of a failure here can tell it is this sentence, not
        // merely that a count moved.
        PasswordChangeReport report = PasswordChangeModel.Describe(VaultPasswordChange.Changed, @"C:\vault\vault.json");

        string sentence = Assert.Single(report.ThingsToTell);
        Assert.Contains("password", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("changed", sentence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_flag_produces_a_sentence_naming_what_it_is_about()
    {
        // Pins content, not just count: a reader of a failure here can tell which sentence is
        // missing rather than only that the total moved. Each assertion checks the sentence that
        // flag is documented to add exists and mentions the thing it is about.
        PasswordChangeReport report = PasswordChangeModel.Describe(
            VaultPasswordChange.Changed
            | VaultPasswordChange.DeviceUnlockDropped
            | VaultPasswordChange.TheOldCopyRemains
            | VaultPasswordChange.TheRecordsIntegrityCodeDidNotMatch,
            @"C:\vault\vault.json");

        Assert.Contains(
            report.ThingsToTell, s => s.Contains("password", StringComparison.OrdinalIgnoreCase)
                                       && s.Contains("changed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            report.ThingsToTell, s => s.Contains("device", StringComparison.OrdinalIgnoreCase)
                                       && s.Contains("off", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            report.ThingsToTell, s => s.Contains(@"C:\vault\vault.json.bak", StringComparison.Ordinal));
        Assert.Contains(
            report.ThingsToTell, s => s.Contains("integrity", StringComparison.OrdinalIgnoreCase));
    }
}

public class PasswordChangeModelTests : VaultHarness
{
    [Fact]
    public async Task Changing_with_the_right_old_password_reports_the_change_and_the_new_password_opens_the_vault()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old password"), token);
        PasswordChangeModel model = new(vault, VaultPath);

        PasswordChangeReport report = await model.ChangeAsync(Password("old password"), Password("new password"), token);

        Assert.True(report.Changed);
        Assert.Single(report.ThingsToTell);

        await vault.LockAsync();
        Assert.Equal(VaultUnlockResult.Success, await vault.UnlockAsync(Password("new password"), token));
    }

    [Fact]
    public async Task Changing_with_the_wrong_old_password_changes_nothing_and_the_old_password_still_opens_the_vault()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old password"), token);
        PasswordChangeModel model = new(vault, VaultPath);

        PasswordChangeReport report = await model.ChangeAsync(Password("wrong password"), Password("new password"), token);

        Assert.False(report.Changed);
        Assert.Empty(report.ThingsToTell);
        Assert.Null(report.SurvivingCopyPath);

        await vault.LockAsync();
        Assert.Equal(VaultUnlockResult.Success, await vault.UnlockAsync(Password("old password"), token));
    }
}
