using Xunit;

namespace XrplFile.App.Core.Tests;

public class CreateMessageTests
{
    [Theory]
    [InlineData(VaultCreateResult.Success)]
    [InlineData(VaultCreateResult.VaultAlreadyExists)]
    [InlineData(VaultCreateResult.StorageUnavailable)]
    [InlineData(VaultCreateResult.PasswordTooShort)]
    public void Every_outcome_has_its_own_words(VaultCreateResult result)
    {
        CreateMessage message = CreateMessages.For(result);

        Assert.False(string.IsNullOrWhiteSpace(TestText.En(message.Headline)));
        Assert.False(string.IsNullOrWhiteSpace(TestText.En(message.Detail)));
    }

    [Fact]
    public void No_two_outcomes_share_a_headline()
    {
        VaultCreateResult[] all = Enum.GetValues<VaultCreateResult>();

        Assert.Equal(
            all.Length,
            all.Select(r => TestText.En(CreateMessages.For(r).Headline)).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_vault_already_existing_never_names_the_path()
    {
        // The defect this type exists to close: DurableFile.WriteAsync's own exception message
        // names the full local vault path ("Writing the vault file at '{path}' failed."), and a
        // screen that relayed an exception's message verbatim put that path on screen. Nothing
        // here may ever carry a path, a drive letter, or a directory separator.
        string detail = TestText.En(CreateMessages.For(VaultCreateResult.VaultAlreadyExists).Detail);

        Assert.DoesNotContain(":\\", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("/", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(".json", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Storage_unavailable_never_names_the_path_or_the_underlying_exception()
    {
        string detail = TestText.En(CreateMessages.For(VaultCreateResult.StorageUnavailable).Detail);

        Assert.DoesNotContain(":\\", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("/", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(".json", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", detail, StringComparison.Ordinal);
    }
}
