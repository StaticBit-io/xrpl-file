using System.Diagnostics.CodeAnalysis;

namespace XrplFile;

/// <summary>
/// <c>xrpl-file://&lt;network&gt;/&lt;tx-hash&gt;</c>: the hash of the manifest transaction on a named network.
/// Nothing else is in the link; the MIME type, size and name live in the manifest.
/// </summary>
public sealed record XrplFileLink
{
    public const string Scheme = "xrpl-file";

    private const string Prefix = Scheme + "://";
    private const int HashLength = 64;

    private static readonly HashSet<string> KnownNetworks = new(StringComparer.Ordinal) { "mainnet", "testnet", "devnet" };

    public string Network { get; }

    public string TxHash { get; }

    public XrplFileLink(string network, string txHash)
    {
        Network = NormalizeNetwork(network)
            ?? throw new XrplFileException(XrplFileError.InvalidLink, $"'{network}' is neither a network name nor a NetworkID");
        TxHash = NormalizeHash(txHash)
            ?? throw new XrplFileException(XrplFileError.InvalidLink, "the transaction hash is not 64 hexadecimal characters");
    }

    public static XrplFileLink Parse(string text)
    {
        if (text is null || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new XrplFileException(XrplFileError.InvalidLink, $"an xrpl-file link starts with '{Prefix}'");
        }

        string rest = text.Substring(Prefix.Length);
        int slash = rest.IndexOf('/');

        if (slash <= 0 || slash == rest.Length - 1)
        {
            throw new XrplFileException(XrplFileError.InvalidLink, "an xrpl-file link is the network, a slash and the manifest transaction hash");
        }

        return new XrplFileLink(rest.Substring(0, slash), rest.Substring(slash + 1));
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out XrplFileLink? link)
    {
        try
        {
            link = Parse(text!);
            return true;
        }
        catch (XrplFileException)
        {
            link = null;
            return false;
        }
    }

    public override string ToString() => Prefix + Network + "/" + TxHash;

    private static string? NormalizeNetwork(string network)
    {
        if (string.IsNullOrEmpty(network)) return null;

        string lower = network.ToLowerInvariant();
        if (KnownNetworks.Contains(lower)) return lower;

        // Any other network is named by its NetworkID: digits only, no sign, no leading zero.
        bool digits = lower.All(character => character is >= '0' and <= '9');
        return digits && (lower.Length == 1 || lower[0] != '0') ? lower : null;
    }

    private static string? NormalizeHash(string hash)
    {
        if (hash is null || hash.Length != HashLength) return null;

        foreach (char character in hash)
        {
            if (!Uri.IsHexDigit(character)) return null;
        }

        return hash.ToUpperInvariant();
    }
}
