using Xunit;

namespace XrplFile.App.Core.Tests;

public class CreateMessageTests
{
    [Theory]
    [InlineData(VaultCreateResult.Success)]
    [InlineData(VaultCreateResult.VaultAlreadyExists)]
    [InlineData(VaultCreateResult.StorageUnavailable)]
    public void Every_outcome_has_its_own_words(VaultCreateResult result)
    {
        CreateMessage message = CreateMessages.For(result);

        Assert.False(string.IsNullOrWhiteSpace(message.Headline));
        Assert.False(string.IsNullOrWhiteSpace(message.Detail));
    }

    [Fact]
    public void No_two_outcomes_share_a_headline()
    {
        VaultCreateResult[] all = Enum.GetValues<VaultCreateResult>();

        Assert.Equal(
            all.Length,
            all.Select(r => CreateMessages.For(r).Headline).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_vault_already_existing_never_names_the_path()
    {
        // The defect this type exists to close: DurableFile.WriteAsync's own exception message
        // names the full local vault path ("Writing the vault file at '{path}' failed."), and a
        // screen that relayed an exception's message verbatim put that path on screen. Nothing
        // here may ever carry a path, a drive letter, or a directory separator.
        CreateMessage message = CreateMessages.For(VaultCreateResult.VaultAlreadyExists);

        Assert.DoesNotContain(":\\", message.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("/", message.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(".json", message.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Storage_unavailable_never_names_the_path_or_the_underlying_exception()
    {
        CreateMessage message = CreateMessages.For(VaultCreateResult.StorageUnavailable);

        Assert.DoesNotContain(":\\", message.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("/", message.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(".json", message.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", message.Detail, StringComparison.Ordinal);
    }
}
