using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class XrpAmountTests
{
    [Theory]
    [InlineData(200_000, "0.2 XRP")]
    [InlineData(2_000_000, "2 XRP")]
    [InlineData(1_250_000, "1.25 XRP")]
    [InlineData(1, "0.000001 XRP")]
    [InlineData(0, "0 XRP")]
    [InlineData(12_345_000_000, "12,345 XRP")]
    public void Drops_read_as_xrp_with_no_trailing_zeros(long drops, string text)
    {
        Assert.Equal(text, XrpAmount.Format(drops));
    }
}
