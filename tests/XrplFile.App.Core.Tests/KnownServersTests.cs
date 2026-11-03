using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class KnownServersTests
{
    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("devnet")]
    public void Every_built_in_endpoint_is_offered_first_and_flagged_full_history(string network)
    {
        IReadOnlyList<KnownServer> servers = KnownServers.For(network);
        IReadOnlyList<string> builtIn = Endpoints.For(network);

        Assert.Equal(builtIn, servers.Take(builtIn.Count).Select(server => server.Url));
        Assert.All(servers.Take(builtIn.Count), server => Assert.True(server.FullHistory));
    }

    [Fact]
    public void Mainnet_also_lists_s1_which_keeps_recent_history_only()
    {
        KnownServer s1 = Assert.Single(KnownServers.For("mainnet"), server => server.Url == "wss://s1.ripple.com");

        Assert.False(s1.FullHistory);
        Assert.Equal("s1.ripple.com — recent history only", TestText.En(s1.Label));
        Assert.Equal(Endpoints.For("mainnet").Count + 1, KnownServers.For("mainnet").Count);
    }

    [Fact]
    public void The_test_networks_list_nothing_beyond_their_built_in_endpoint()
    {
        Assert.Equal(Endpoints.For("testnet"), KnownServers.For("testnet").Select(server => server.Url));
        Assert.Equal(Endpoints.For("devnet"), KnownServers.For("devnet").Select(server => server.Url));
    }

    [Fact]
    public void A_network_with_no_catalogue_has_no_servers_and_is_not_known()
    {
        Assert.Empty(KnownServers.For("1"));
        Assert.False(KnownServers.IsKnown("1"));
        Assert.True(KnownServers.IsKnown("mainnet"));
    }

    [Fact]
    public void Options_start_with_the_built_in_one_as_an_empty_value_labelled_with_its_host()
    {
        IReadOnlyList<ServerOption> options = KnownServers.Options("mainnet");

        Assert.Equal(string.Empty, options[0].Value);
        Assert.Equal("Built-in (xrplcluster.com)", TestText.En(options[0].Label));
        Assert.Equal(
            ["", "wss://s2.ripple.com", "wss://s1.ripple.com", KnownServers.CustomValue],
            options.Select(option => option.Value));
        Assert.Equal("Custom URL…", TestText.En(options[^1].Label));
        Assert.Equal("s2.ripple.com", TestText.En(options[1].Label));
    }

    [Fact]
    public void A_test_network_offers_built_in_and_custom_only()
    {
        IReadOnlyList<ServerOption> options = KnownServers.Options("testnet");

        Assert.Equal(["", KnownServers.CustomValue], options.Select(option => option.Value));
        Assert.Equal("Built-in (s.altnet.rippletest.net)", TestText.En(options[0].Label));
    }

    [Theory]
    [InlineData("mainnet", "", "")]
    [InlineData("mainnet", "   ", "")]
    [InlineData("mainnet", "wss://s2.ripple.com", "wss://s2.ripple.com")]
    [InlineData("mainnet", " wss://s1.ripple.com ", "wss://s1.ripple.com")]
    [InlineData("mainnet", "ws://localhost:6006", KnownServers.CustomValue)]
    [InlineData("mainnet", "wss://xrplcluster.com", KnownServers.CustomValue)]
    [InlineData("testnet", "wss://s2.ripple.com", KnownServers.CustomValue)]
    public void A_stored_url_selects_the_matching_option_or_custom(string network, string stored, string expected) =>
        Assert.Equal(expected, KnownServers.Selection(network, stored));
}
