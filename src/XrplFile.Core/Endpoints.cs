namespace XrplFile;

/// <summary>Public nodes with full history, per network. Reading needs full history: s1.ripple.com does not keep it.</summary>
public static class Endpoints
{
    /// <summary>The first ledger mainnet history goes back to; a node whose history starts later is not a full-history node.</summary>
    public const long MainnetFirstLedger = 32570;

    private static readonly string[] KnownNetworkNames = ["mainnet", "testnet", "devnet"];

    private static readonly Dictionary<string, string[]> Known = new(StringComparer.Ordinal)
    {
        ["mainnet"] = ["wss://xrplcluster.com", "wss://s2.ripple.com"],
        ["testnet"] = ["wss://s.altnet.rippletest.net:51233"],
        ["devnet"] = ["wss://s.devnet.rippletest.net:51233"],
    };

    /// <summary>
    /// The network names this type knows a built-in endpoint for, in the fixed order a caller should
    /// offer them in. A caller that needs to list them -- a settings screen editing overrides, for
    /// one -- reads this rather than keeping its own copy that would silently miss a network added
    /// here later.
    /// </summary>
    public static IReadOnlyList<string> KnownNetworks => KnownNetworkNames;

    public static IReadOnlyList<string> For(string network) =>
        Known.TryGetValue(network, out string[]? endpoints) ? endpoints : [];
}
