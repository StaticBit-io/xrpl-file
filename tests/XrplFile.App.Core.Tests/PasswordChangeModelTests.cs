using Xunit;

using XrplFile.App.Core.Localization;
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
    public async Task A_new_password_below_the_minimum_is_refused_with_the_minimum_as_a_value()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        PasswordChangeModel model = new(new NeverVault(), @"C:aultault.json");

        PasswordChangeReport report = await model.ChangeAsync(
            System.Text.Encoding.UTF8.GetBytes("old password"), System.Text.Encoding.UTF8.GetBytes("short"), token);

        Assert.False(report.Changed);
        Assert.Equal(
            UiText.Of(VaultText.ChangeTooShort, ("minimum", PasswordPolicy.MinimumLength)),
            report.Refusal);
        Assert.StartsWith("Choose a new password of at least 8 characters.", TestText.En(report.Refusal), StringComparison.Ordinal);
    }

    [Fact]
    public void Changed_alone_says_only_that_the_password_changed()
    {
        // A failing sentence-count test only says "expected N, got M" — this pins the actual text
        // for the simplest case, so a reader of a failure here can tell it is this sentence, not
        // merely that a count moved. "master password" and "was changed" are checked together,
        // not as two independent words: a sentence about a password unrelated to this change (or
        // one that changed for a different reason) could contain "password" and "changed" apart
        // without saying what actually happened here.
        PasswordChangeReport report = PasswordChangeModel.Describe(VaultPasswordChange.Changed, @"C:\vault\vault.json");

        string sentence = TestText.En(Assert.Single(report.ThingsToTell));
        Assert.Contains("master password", sentence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("was changed", sentence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_flag_produces_a_sentence_naming_what_it_is_about()
    {
        // Pins content, not just count: a reader of a failure here can tell which sentence is
        // missing rather than only that the total moved. Each assertion checks the sentence that
        // flag is documented to add exists and mentions the thing it is about.
        //
        // Every check below anchors on a phrase that ties the fact to its meaning, not on
        // independent words that a weaker or even wrong sentence could contain apart from each
        // other. The device-unlock check is the one this was found to be missing for: "device" and
        // "off" as separate substrings would pass for almost any sentence that happens to mention a
        // device being off, including one that says nothing about the setting this model turned on
        // now being off. "remember on this device" plus "turned off" only appears together in the
        // sentence that actually says that. The same reasoning applies to the integrity sentence,
        // strengthened here alongside it: bare "integrity" is a single word that any passing
        // mention of integrity would satisfy, so it is anchored on "integrity code" together with
        // "repaired" instead. The surviving-copy check already anchors on the literal computed
        // path, which only the intended sentence can contain, so it needed no change.
        PasswordChangeReport report = PasswordChangeModel.Describe(
            VaultPasswordChange.Changed
            | VaultPasswordChange.DeviceUnlockDropped
            | VaultPasswordChange.TheOldCopyRemains
            | VaultPasswordChange.TheRecordsIntegrityCodeDidNotMatch,
            @"C:\vault\vault.json");

        string[] sentences = [.. report.ThingsToTell.Select(text => TestText.En(text))];

        Assert.Contains(
            sentences, s => s.Contains("master password", StringComparison.OrdinalIgnoreCase)
                             && s.Contains("was changed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            sentences, s => s.Contains("remember on this device", StringComparison.OrdinalIgnoreCase)
                             && s.Contains("turned off", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            sentences, s => s.Contains(@"C:\vault\vault.json.bak", StringComparison.Ordinal));
        Assert.Contains(
            sentences, s => s.Contains("integrity code", StringComparison.OrdinalIgnoreCase)
                             && s.Contains("repaired", StringComparison.OrdinalIgnoreCase));
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
