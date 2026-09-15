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
        Network = RequireNetwork(network);
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

    /// <summary>
    /// The network in canonical form, or InvalidLink. The one place the rule lives: a writer
    /// checks its caller's network through this before it submits anything, so a name this
    /// constructor would refuse cannot be discovered after a thousand paid transactions.
    /// </summary>
    internal static string RequireNetwork(string network) =>
        TryNormalizeNetwork(network, out string? canonical)
            ? canonical
            : throw new XrplFileException(XrplFileError.InvalidLink, $"'{network}' is neither a network name nor a NetworkID");

    /// <summary>
    /// Whether <paramref name="network"/> is a network an xrpl-file link can name — <c>mainnet</c>,
    /// <c>testnet</c> or <c>devnet</c> in any case, or a NetworkID written as digits with no leading
    /// zero — and, when it is, <paramref name="canonical"/> holds its canonical form: the known name
    /// lower-cased, or the NetworkID unchanged. This is the same rule <see cref="RequireNetwork"/>
    /// enforces at construction, exposed so a caller can ask before committing to a network the
    /// format cannot address, rather than discovering it from a thrown <see cref="XrplFileException"/>.
    /// </summary>
    public static bool TryNormalizeNetwork(string? network, [NotNullWhen(true)] out string? canonical)
    {
        if (string.IsNullOrEmpty(network))
        {
            canonical = null;
            return false;
        }

        string lower = network.ToLowerInvariant();
        if (KnownNetworks.Contains(lower))
        {
            canonical = lower;
            return true;
        }

        // Any other network is named by its NetworkID: digits only, no sign, no leading zero.
        bool digits = lower.All(character => character is >= '0' and <= '9');
        if (digits && (lower.Length == 1 || lower[0] != '0'))
        {
            canonical = lower;
            return true;
        }

        canonical = null;
        return false;
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
