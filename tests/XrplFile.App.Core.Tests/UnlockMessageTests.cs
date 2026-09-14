using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public class UnlockMessageTests
{
    [Fact]
    public void Only_a_wrong_password_counts_against_an_attempt_limit()
    {
        // A failing disk reported as a user's mistake is how healthy data gets destroyed.
        Assert.True(UnlockMessages.For(VaultUnlockResult.WrongPassword).CountsAsAFailedAttempt);
        Assert.False(UnlockMessages.For(VaultUnlockResult.VaultCorrupt).CountsAsAFailedAttempt);
        Assert.False(UnlockMessages.For(VaultUnlockResult.VaultUnavailable).CountsAsAFailedAttempt);
    }

    [Fact]
    public void A_corrupt_vault_is_never_told_the_password_is_fine()
    {
        // WrongPassword and VaultCorrupt cannot be told apart when the record was already damaged,
        // so the message must not claim to know.
        UnlockMessage message = UnlockMessages.For(VaultUnlockResult.VaultCorrupt);

        Assert.DoesNotContain("password is correct", message.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not the password", message.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(VaultUnlockResult.Success)]
    [InlineData(VaultUnlockResult.WrongPassword)]
    [InlineData(VaultUnlockResult.VaultMissing)]
    [InlineData(VaultUnlockResult.VaultCorrupt)]
    [InlineData(VaultUnlockResult.VaultDegraded)]
    [InlineData(VaultUnlockResult.VaultUnsupported)]
    [InlineData(VaultUnlockResult.VaultUnavailable)]
    public void Every_outcome_has_its_own_words(VaultUnlockResult result)
    {
        UnlockMessage message = UnlockMessages.For(result);

        Assert.False(string.IsNullOrWhiteSpace(message.Headline));
        Assert.False(string.IsNullOrWhiteSpace(message.Detail));
    }

    [Fact]
    public void No_two_outcomes_share_a_headline()
    {
        VaultUnlockResult[] all = Enum.GetValues<VaultUnlockResult>();

        Assert.Equal(all.Length, all.Select(r => UnlockMessages.For(r).Headline).Distinct(StringComparer.Ordinal).Count());
    }
}
