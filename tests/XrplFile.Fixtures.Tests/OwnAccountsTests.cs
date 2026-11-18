using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Fixtures.Tests;

public sealed class OwnAccountsTests
{
    private static XrplWallet Generate(string algorithm) => XrplWallet.Generate(algorithm);

    private static void AssertSeedIsMasterKey(string algorithm)
    {
        XrplWallet wallet = Generate(algorithm);

        OwnKey key = OwnAccounts.FromSeed(wallet.Seed!, wallet.ClassicAddress);

        Assert.False(key.IsRegularKey);
        Assert.Equal(wallet.ClassicAddress, key.Account);
        Assert.Equal(wallet.ClassicAddress, key.Wallet.ClassicAddress);
    }

    [Fact]
    public void Ed25519SeedOfTheAccountIsItsMasterKey() => AssertSeedIsMasterKey(XrplWallet.Ed25519);

    [Fact]
    public void Secp256k1SeedOfTheAccountIsItsMasterKey() => AssertSeedIsMasterKey(XrplWallet.Secp256k1);

    [Fact]
    public void SeedWithSurroundingWhitespaceStillMatches()
    {
        XrplWallet wallet = Generate(XrplWallet.Ed25519);

        OwnKey key = OwnAccounts.FromSeed("  " + wallet.Seed + "\r\n", wallet.ClassicAddress);

        Assert.False(key.IsRegularKey);
    }

    [Fact]
    public void SeedOfAnotherAccountIsARegularKeyCandidate()
    {
        XrplWallet signer = Generate(XrplWallet.Secp256k1);
        XrplWallet account = Generate(XrplWallet.Ed25519);

        OwnKey key = OwnAccounts.FromSeed(signer.Seed!, account.ClassicAddress);

        Assert.True(key.IsRegularKey);
        Assert.Equal(account.ClassicAddress, key.Account);
        Assert.Equal(account.ClassicAddress, key.Wallet.ClassicAddress);
        Assert.Equal(signer.PublicKey, key.Wallet.PublicKey);
    }

    [Fact]
    public void GarbageSeedFailsWithoutEchoingIt()
    {
        const string Garbage = "sNotASeedAtAll123";
        XrplWallet account = Generate(XrplWallet.Ed25519);

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => OwnAccounts.FromSeed(Garbage, account.ClassicAddress));

        Assert.DoesNotContain(Garbage, e.Message, StringComparison.Ordinal);
        Assert.Contains(account.ClassicAddress, e.Message, StringComparison.Ordinal);
        Assert.Null(e.InnerException);
    }

    [Fact]
    public void RedirectedInputIsRefusedOnMainnet()
    {
        using StringReader input = new("sEdTM1uX8pu2do5XvTnutH6HsouMaM2\n");

        Assert.Throws<InvalidOperationException>(() => OwnAccounts.ReadSeed("writer seed for rX: ", FixtureNetwork.Mainnet, input, inputRedirected: true));
    }

    [Fact]
    public void RedirectedInputIsReadOnTestnet()
    {
        using StringReader input = new("sEdTM1uX8pu2do5XvTnutH6HsouMaM2\nsecond\n");

        string seed = OwnAccounts.ReadSeed("writer seed for rX: ", FixtureNetwork.Testnet, input, inputRedirected: true);

        Assert.Equal("sEdTM1uX8pu2do5XvTnutH6HsouMaM2", seed);
    }
}
