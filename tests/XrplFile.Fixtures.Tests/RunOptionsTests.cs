using Xunit;

namespace XrplFile.Fixtures.Tests;

public sealed class RunOptionsTests
{
    private const string Writer = "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh";
    private const string Payer = "rPT1Sjq2YGrBMTttX4GZHjKu9dyfzbpAYe";
    private const string Hash = "9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5";

    private static string[] Own(params string[] extra) => ["p.json", "m", "o", .. extra];

    private static PlanEntry Entry(string key, string writer = "a", string? cover = null) =>
        new(key, key + ".bin", "application/octet-stream", key, writer, cover);

    [Fact]
    public void ThreePositionalsMeanTheTestnetThrowawayMode()
    {
        RunOptions options = RunOptions.Parse(["p.json", "m", "o"]);

        Assert.Equal(FixtureNetwork.Testnet, options.Network);
        Assert.Equal("wss://s.altnet.rippletest.net:51233", options.NodeUrl);
        Assert.False(options.OwnAccounts);
        Assert.Equal("testnet", options.NetworkName);
    }

    [Fact]
    public void MainnetNeedsBothAddresses()
    {
        PlanException e = Assert.Throws<PlanException>(() => RunOptions.Parse(Own("--network", "mainnet", "--writer", Writer)));

        Assert.Contains(e.Problems, problem => problem.Contains("--payer", StringComparison.Ordinal));
    }

    [Fact]
    public void MainnetDefaultsToXrplCluster()
    {
        RunOptions options = RunOptions.Parse(Own("--network", "mainnet", "--writer", Writer, "--payer", Payer));

        Assert.Equal("wss://xrplcluster.com", options.NodeUrl);
        Assert.Equal("mainnet", options.NetworkName);
        Assert.True(options.OwnAccounts);
        Assert.Equal(Writer, options.WriterAddress);
        Assert.Equal(Payer, options.PayerAddress);
    }

    [Fact]
    public void WriterEqualToPayerIsRefused()
    {
        PlanException e = Assert.Throws<PlanException>(() => RunOptions.Parse(Own("--network", "mainnet", "--writer", Writer, "--payer", Writer)));

        Assert.Contains(e.Problems, problem => problem.Contains("different", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInvalidAddressIsRefused()
    {
        PlanException e = Assert.Throws<PlanException>(() => RunOptions.Parse(Own("--network", "mainnet", "--writer", "rNotAnAddress", "--payer", Payer)));

        Assert.Contains(e.Problems, problem => problem.Contains("rNotAnAddress", StringComparison.Ordinal));
    }

    [Fact]
    public void ANodeMustBeWss()
    {
        PlanException e = Assert.Throws<PlanException>(() => RunOptions.Parse(Own("--network", "mainnet", "--writer", Writer, "--payer", Payer, "--node", "https://x")));

        Assert.Contains(e.Problems, problem => problem.Contains("wss://", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownOptionIsRefused()
    {
        PlanException e = Assert.Throws<PlanException>(() => RunOptions.Parse(Own("--seed", "abc")));

        Assert.Contains(e.Problems, problem => problem.Contains("--seed", StringComparison.Ordinal) && problem.Contains("typed at the prompt", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryProblemIsListedAtOnce()
    {
        PlanException e = Assert.Throws<PlanException>(() => RunOptions.Parse(Own("--network", "mainnet", "--writer", "rNotAnAddress", "--node", "http://x", "--bogus")));

        Assert.True(e.Problems.Count >= 4, string.Join("; ", e.Problems));
    }

    [Theory]
    [InlineData("--writer")]
    [InlineData("--payer")]
    [InlineData("--network")]
    [InlineData("--node")]
    public void ARepeatedOptionIsAProblem(string option)
    {
        string[] first = option switch
        {
            "--writer" => [option, Writer],
            "--payer" => [option, Payer],
            "--network" => [option, "testnet"],
            _ => [option, "wss://a.example"],
        };
        string[] second = [option, option == "--writer" ? Payer : option == "--payer" ? Writer : option == "--network" ? "mainnet" : "wss://b.example"];

        PlanException e = Assert.Throws<PlanException>(() => RunOptions.Parse(Own([.. first, .. second, "--writer", Writer, "--payer", Payer])));

        Assert.Contains(e.Problems, problem => problem.Contains(option, StringComparison.Ordinal) && problem.Contains("given twice", StringComparison.Ordinal));
    }

    [Fact]
    public void TestnetWithOwnAccountsIsAllowed()
    {
        RunOptions options = RunOptions.Parse(Own("--network", "testnet", "--writer", Writer, "--payer", Payer));

        Assert.True(options.OwnAccounts);
        Assert.Equal(FixtureNetwork.Testnet, options.Network);
        Assert.Equal("wss://s.altnet.rippletest.net:51233", options.NodeUrl);
    }

    [Fact]
    public void OwnAccountsRefuseWriterB()
    {
        PlanException e = Assert.Throws<PlanException>(() => Plan.ValidateOwnAccounts([Entry("one"), Entry("two", writer: "b")]));

        Assert.Single(e.Problems);
        Assert.Contains("'two'", e.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void OwnAccountsRefuseBrokenCover()
    {
        PlanException e = Assert.Throws<PlanException>(() => Plan.ValidateOwnAccounts([Entry("cover"), Entry("big-broken-cover", cover: "cover")]));

        Assert.Single(e.Problems);
        Assert.Contains("cannot be published with your own accounts", e.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void OwnAccountsAcceptAPlanOfWriterA()
    {
        Plan.ValidateOwnAccounts([Entry("one"), Entry("two", cover: "one")]);
    }

    [Fact]
    public void LinksOfAnotherNetworkAreRefused()
    {
        Dictionary<string, string> links = new() { ["a"] = $"xrpl-file://testnet/{Hash}" };

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Links.RequireNetwork(links, "mainnet", "o/links.json"));

        Assert.Contains("o/links.json", e.Message, StringComparison.Ordinal);
        Links.RequireNetwork(links, "testnet", "o/links.json");
    }
}
