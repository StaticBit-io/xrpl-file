using Xunit;

namespace XrplFile.Fixtures.Tests;

public sealed class NodeNetworkTests
{
    [Theory]
    [InlineData(null, "mainnet")]
    [InlineData(0u, "mainnet")]
    [InlineData(1u, "testnet")]
    [InlineData(2u, "devnet")]
    [InlineData(21337u, "21337")]
    public void NameFollowsTheNodesNetworkId(uint? networkId, string expected) =>
        Assert.Equal(expected, NodeNetwork.NameOf(networkId));

    [Theory]
    [InlineData(null, "mainnet")]
    [InlineData(0u, "mainnet")]
    [InlineData(1u, "testnet")]
    public void ANodeOfTheNamedNetworkPasses(uint? networkId, string expected) =>
        NodeNetwork.Require(networkId, expected, "wss://node.example");

    [Fact]
    public void ATestnetRunOnAMainnetNodeIsRefused()
    {
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(
            () => NodeNetwork.Require(null, "testnet", "wss://xrplcluster.com"));

        Assert.Contains("--network is testnet", e.Message, StringComparison.Ordinal);
        Assert.Contains("serves mainnet", e.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was signed", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMainnetRunOnATestnetNodeIsRefused()
    {
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(
            () => NodeNetwork.Require(1, "mainnet", "wss://s.altnet.rippletest.net:51233"));

        Assert.Contains("serves testnet", e.Message, StringComparison.Ordinal);
    }
}
