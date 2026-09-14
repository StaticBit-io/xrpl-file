using System.Text;

using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// The rule itself, and every model that applies it. A vault with no recovery phrase cannot be
/// sealed under nothing, and neither can the export container that is the only way back from a
/// forgotten master password.
/// </summary>
public class PasswordPolicyTests : VaultHarness
{
    [Fact]
    public void An_empty_password_is_never_long_enough()
    {
        Assert.False(PasswordPolicy.IsLongEnough([]));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(PasswordPolicy.MinimumLength - 1, false)]
    [InlineData(PasswordPolicy.MinimumLength, true)]
    [InlineData(PasswordPolicy.MinimumLength + 1, true)]
    public void The_boundary_is_the_minimum_itself(int characters, bool expected)
    {
        Assert.Equal(expected, PasswordPolicy.IsLongEnough(Encoding.UTF8.GetBytes(new string('x', characters))));
    }

    [Fact]
    public void The_rule_counts_characters_and_not_bytes()
    {
        // Eight Cyrillic characters are sixteen UTF-8 bytes, and four are eight: a rule measured in
        // bytes would accept the four-character one and would have accepted the eight-character one
        // for the wrong reason. Both answers below are about characters.
        Assert.True(PasswordPolicy.IsLongEnough(Encoding.UTF8.GetBytes("паровоз8")));
        Assert.False(PasswordPolicy.IsLongEnough(Encoding.UTF8.GetBytes("паро")));
    }

    [Fact]
    public async Task Creating_a_vault_under_an_empty_master_password_is_refused_and_writes_nothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);

        VaultCreateResult result = await model.CreateAsync(Password(string.Empty), token);

        Assert.Equal(VaultCreateResult.PasswordTooShort, result);
        Assert.NotNull(model.LastCreateMessage);
        Assert.NotEqual(UnlockStage.Unlocked, model.Stage);
        Assert.False(await vault.ExistsAsync(token));
    }

    [Fact]
    public async Task Creating_a_vault_one_character_short_of_the_minimum_is_refused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);

        Assert.Equal(
            VaultCreateResult.PasswordTooShort,
            await model.CreateAsync(Password(new string('x', PasswordPolicy.MinimumLength - 1)), token));
        Assert.False(await vault.ExistsAsync(token));
    }

    [Fact]
    public async Task A_short_password_that_already_opens_a_vault_is_still_accepted_at_the_door()
    {
        // The rule applies where a password is chosen, never where one is entered. A vault created
        // before this rule existed -- here, straight through KeyVault, which has no such rule --
        // must still open, or enforcing the rule retroactively would lose its owner every key.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), token);
        await vault.LockAsync();
        UnlockModel model = new(vault);

        Assert.Equal(VaultUnlockResult.Success, await model.UnlockAsync(Password("old"), token));
    }

    [Fact]
    public async Task Changing_to_a_password_below_the_minimum_is_refused_and_the_old_one_still_opens_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old password"), token);
        PasswordChangeModel model = new(vault, VaultPath);

        PasswordChangeReport report = await model.ChangeAsync(Password("old password"), Password(""), token);

        Assert.NotNull(report.Refusal);
        Assert.False(report.Changed);
        Assert.Empty(report.ThingsToTell);

        // And the refusal is told apart from a wrong current password, which carries no Refusal.
        Assert.Null((await model.ChangeAsync(Password("not the old one"), Password("a long enough one"), token)).Refusal);

        await vault.LockAsync();
        Assert.Equal(VaultUnlockResult.Success, await vault.UnlockAsync(Password("old password"), token));
    }
}
