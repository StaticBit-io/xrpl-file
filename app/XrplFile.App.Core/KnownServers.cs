namespace XrplFile.App.Core;

/// <summary>A public server Settings can point a network at.</summary>
/// <param name="Url">The WebSocket URL saved as the network's override.</param>
/// <param name="Label">What the person reads: the host, plus a caveat when the server is not a full-history node.</param>
/// <param name="FullHistory">Whether the server keeps full ledger history, which reading a file needs.</param>
public sealed record KnownServer(string Url, string Label, bool FullHistory);

/// <summary>One entry of a network's server drop-down in Settings.</summary>
/// <param name="Value">Empty for the built-in endpoint, a server's URL, or <see cref="KnownServers.CustomValue"/>.</param>
public sealed record ServerOption(string Value, string Label);

/// <summary>
/// The servers Settings offers per network, so a person picks one instead of typing a URL.
/// </summary>
/// <remarks>
/// <see cref="Endpoints"/> stays the source of what a network is reached at when nothing is
/// overridden; this adds the other servers worth choosing and says which of them are not
/// full-history nodes. Every <see cref="Endpoints.For"/> entry comes first and is flagged full
/// history -- <see cref="Endpoints"/> is pinned to full-history nodes -- so a server added there
/// shows up here without this type being touched. A choice among these saves its URL as the override;
/// the built-in one saves nothing.
/// </remarks>
public static class KnownServers
{
    /// <summary>The <see cref="ServerOption.Value"/> that reveals the free-text URL box.</summary>
    public const string CustomValue = "custom";

    private const string RecentHistoryOnly = " — recent history only";

    private static readonly IReadOnlyDictionary<string, KnownServer[]> Extra =
        new Dictionary<string, KnownServer[]>(StringComparer.Ordinal)
        {
            ["mainnet"] = [new KnownServer("wss://s1.ripple.com", "s1.ripple.com" + RecentHistoryOnly, FullHistory: false)],
        };

    /// <summary>Whether <paramref name="network"/> has a catalogue here -- a row of any other network keeps only its text box.</summary>
    public static bool IsKnown(string network) => Endpoints.For(network).Count > 0;

    /// <summary>The servers for <paramref name="network"/>, its built-in endpoints first, or an empty list when none is known.</summary>
    public static IReadOnlyList<KnownServer> For(string network)
    {
        List<KnownServer> servers =
        [
            .. Endpoints.For(network).Select(url => new KnownServer(url, HostOf(url), FullHistory: true)),
        ];

        if (Extra.TryGetValue(network, out KnownServer[]? extra))
        {
            servers.AddRange(extra);
        }

        return servers;
    }

    /// <summary>
    /// The drop-down for <paramref name="network"/>: the built-in endpoint as an empty value (no
    /// override), then every other known server by URL, then <see cref="CustomValue"/>.
    /// </summary>
    public static IReadOnlyList<ServerOption> Options(string network)
    {
        IReadOnlyList<KnownServer> servers = For(network);
        List<ServerOption> options = [];

        if (servers.Count > 0)
        {
            options.Add(new ServerOption(string.Empty, $"Built-in ({servers[0].Label})"));
            options.AddRange(servers.Skip(1).Select(server => new ServerOption(server.Url, server.Label)));
        }

        options.Add(new ServerOption(CustomValue, "Custom URL…"));
        return options;
    }

    /// <summary>
    /// Which <see cref="Options"/> value a stored override corresponds to: empty for none, the URL
    /// itself when it is one of the listed alternatives, otherwise <see cref="CustomValue"/>. The
    /// built-in endpoint's own URL stored as an override counts as custom -- it is kept as written
    /// rather than silently turned into "no override", which would follow the built-in if it moved.
    /// </summary>
    public static string Selection(string network, string storedUrl)
    {
        string trimmed = storedUrl.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return Options(network).Any(option => option.Value.Length > 0
                && option.Value != CustomValue
                && string.Equals(option.Value, trimmed, StringComparison.Ordinal))
            ? trimmed
            : CustomValue;
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : url;
}
