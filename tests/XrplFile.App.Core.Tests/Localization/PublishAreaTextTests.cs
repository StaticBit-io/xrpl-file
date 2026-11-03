using XrplFile.App.Core.Localization;
using Xunit;

namespace XrplFile.App.Core.Tests.Localization;

/// <summary>The phrases of the publish and estimate screens whose English changes with a number or a role.</summary>
public sealed class PublishAreaTextTests
{
    [Theory]
    [InlineData(1, "1 byte")]
    [InlineData(2, "2 bytes")]
    [InlineData(1_234_567, "1,234,567 bytes")]
    public void A_byte_count_agrees_with_its_number(long count, string expected)
    {
        Assert.Equal(expected, TestText.En(UiText.Of(CommonText.Bytes, ("count", count))));
    }

    [Theory]
    [InlineData(1, "1 drop")]
    [InlineData(10, "10 drops")]
    [InlineData(1_000, "1,000 drops")]
    public void A_fee_in_drops_agrees_with_its_number(long count, string expected)
    {
        Assert.Equal(expected, TestText.En(UiText.Of(EstimateText.Drops, ("count", count))));
    }

    [Theory]
    [InlineData("en", 1_500_000, "1.5 XRP")]
    [InlineData("ru", 1_500_000, "1,5 XRP")]
    [InlineData("en", 200_000, "0.2 XRP")]
    public void An_amount_reads_in_the_locale_of_the_language(string code, long drops, string expected)
    {
        Assert.Equal(expected, TextCatalog.For(UiLanguage.FromCode(code)!).Xrp(drops));
    }

    [Theory]
    [InlineData(AccountRole.Writer, "Writer w needs", "Writer fees in the worst case")]
    [InlineData(AccountRole.Payer, "Payer w needs", "Payer fees in the worst case")]
    public void An_account_row_names_its_role_in_its_own_phrase(AccountRole role, string needs, string fees)
    {
        AccountCost account = new(role, "w", 2_000_000, 5);

        Assert.Equal(needs, TestText.En(account.NeedsLabel));
        Assert.Equal(fees, TestText.En(account.WorstCaseFeesLabel));
    }
}
