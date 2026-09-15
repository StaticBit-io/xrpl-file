using Xunit;

namespace XrplFile.Tests;

public class EndpointsTests
{
    [Fact]
    public void KnownNetworks_names_exactly_the_three_networks_For_answers()
    {
        Assert.Equal(["mainnet", "testnet", "devnet"], Endpoints.KnownNetworks);

        foreach (string network in Endpoints.KnownNetworks)
        {
            Assert.NotEmpty(Endpoints.For(network));
        }
    }

    [Fact]
    public void A_network_not_in_KnownNetworks_has_no_built_in_endpoint()
    {
        Assert.DoesNotContain("standalone", Endpoints.KnownNetworks);
        Assert.Empty(Endpoints.For("standalone"));
    }
}
