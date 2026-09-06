namespace XrplFile;

/// <summary>Public nodes with full history, per network. Reading needs full history: s1.ripple.com does not keep it.</summary>
public static class Endpoints
{
    /// <summary>The first ledger mainnet history goes back to; a node whose history starts later is not a full-history node.</summary>
    public const long MainnetFirstLedger = 32570;

    private static readonly Dictionary<string, string[]> Known = new(StringComparer.Ordinal)
    {
        ["mainnet"] = ["wss://xrplcluster.com", "wss://s2.ripple.com"],
        ["testnet"] = ["wss://s.altnet.rippletest.net:51233"],
        ["devnet"] = ["wss://s.devnet.rippletest.net:51233"],
    };

    public static IReadOnlyList<string> For(string network) =>
        Known.TryGetValue(network, out string[]? endpoints) ? endpoints : [];
}
