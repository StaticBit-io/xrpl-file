using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public class VaultErrorMessagesTests
{
    [Theory]
    [InlineData(VaultError.Corrupt)]
    [InlineData(VaultError.Unsupported)]
    [InlineData(VaultError.Locked)]
    [InlineData(VaultError.NoSuchEntry)]
    [InlineData(VaultError.DuplicateAddress)]
    [InlineData(VaultError.EntryDamaged)]
    [InlineData(VaultError.StorageFailed)]
    [InlineData(VaultError.Degraded)]
    [InlineData(VaultError.AddressMismatch)]
    public void Every_error_has_words(VaultError error)
    {
        Assert.False(string.IsNullOrWhiteSpace(VaultErrorMessages.For(error)));
    }

    [Fact]
    public void No_two_errors_share_a_sentence()
    {
        VaultError[] all = Enum.GetValues<VaultError>();

        Assert.Equal(
            all.Length,
            all.Select(VaultErrorMessages.For).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Nothing_here_ever_names_a_path()
    {
        // The same defect CreateMessageTests guards against: KeyVault's own exception messages are
        // written for a developer, not a screen, and can carry a reference id or a path fragment.
        // This type must compose its own words rather than relay VaultException.Message.
        foreach (VaultError error in Enum.GetValues<VaultError>())
        {
            string message = VaultErrorMessages.For(error);

            Assert.DoesNotContain(":\\", message, StringComparison.Ordinal);
            Assert.DoesNotContain(".json", message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
