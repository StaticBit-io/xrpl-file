using Xunit;

namespace XrplFile.Tests;

public class XrplFileLinkTests
{
    private const string Hash = "9F2C0B7D1E4A6C8B3D5F7A9C1E3B5D7F9A1C3E5B7D9F1A3C5E7B9D1F3A5C7E9B";

    [Fact]
    public void A_canonical_link_round_trips()
    {
        XrplFileLink link = XrplFileLink.Parse($"xrpl-file://mainnet/{Hash}");

        Assert.Equal("mainnet", link.Network);
        Assert.Equal(Hash, link.TxHash);
        Assert.Equal($"xrpl-file://mainnet/{Hash}", link.ToString());
    }

    [Fact]
    public void Scheme_network_and_hash_are_case_insensitive_on_input()
    {
        XrplFileLink link = XrplFileLink.Parse($"XRPL-FILE://TestNet/{Hash.ToLowerInvariant()}");

        Assert.Equal("testnet", link.Network);
        Assert.Equal(Hash, link.TxHash);
    }

    [Fact]
    public void A_numeric_network_id_is_kept_as_digits()
    {
        XrplFileLink link = XrplFileLink.Parse($"xrpl-file://21338/{Hash}");

        Assert.Equal("21338", link.Network);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ipfs://bafybeigdyrzt5sfp7udm7hu76uh7y26nf3efuylqabf3oclgtqy55fbzdi")]
    [InlineData("xrpl-file://mainnet")]
    [InlineData("xrpl-file:///9F2C")]
    [InlineData("xrpl-file://mainnet/9F2C")]
    [InlineData("xrpl-file://mainnet/9F2C0B7D1E4A6C8B3D5F7A9C1E3B5D7F9A1C3E5B7D9F1A3C5E7B9D1F3A5C7E9G")]
    [InlineData("xrpl-file://main net/9F2C0B7D1E4A6C8B3D5F7A9C1E3B5D7F9A1C3E5B7D9F1A3C5E7B9D1F3A5C7E9B")]
    [InlineData("xrpl-file://0123/9F2C0B7D1E4A6C8B3D5F7A9C1E3B5D7F9A1C3E5B7D9F1A3C5E7B9D1F3A5C7E9B")]
    [InlineData("xrpl-file://mainnet/9F2C0B7D1E4A6C8B3D5F7A9C1E3B5D7F9A1C3E5B7D9F1A3C5E7B9D1F3A5C7E9B?x=1")]
    [InlineData("xrpl-file://mainnet/9F2C0B7D1E4A6C8B3D5F7A9C1E3B5D7F9A1C3E5B7D9F1A3C5E7B9D1F3A5C7E9B/more")]
    public void Anything_else_is_refused_as_an_invalid_link(string text)
    {
        XrplFileException error = Assert.Throws<XrplFileException>(() => XrplFileLink.Parse(text));

        Assert.Equal(XrplFileError.InvalidLink, error.Error);
        Assert.False(XrplFileLink.TryParse(text, out XrplFileLink? _));
    }

    [Fact]
    public void Try_parse_accepts_what_parse_accepts()
    {
        Assert.True(XrplFileLink.TryParse($"xrpl-file://devnet/{Hash}", out XrplFileLink? link));
        Assert.Equal("devnet", link!.Network);
    }

    [Theory]
    [InlineData("mainnet", "mainnet")]
    [InlineData("testnet", "testnet")]
    [InlineData("devnet", "devnet")]
    [InlineData("TestNet", "testnet")]
    [InlineData("21338", "21338")]
    public void Try_normalize_network_accepts_what_the_format_can_address(string network, string expectedCanonical)
    {
        Assert.True(XrplFileLink.TryNormalizeNetwork(network, out string? canonical));
        Assert.Equal(expectedCanonical, canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123")]
    [InlineData("standalone")]
    public void Try_normalize_network_refuses_what_the_format_cannot_address(string network)
    {
        Assert.False(XrplFileLink.TryNormalizeNetwork(network, out string? canonical));
        Assert.Null(canonical);
    }
}
