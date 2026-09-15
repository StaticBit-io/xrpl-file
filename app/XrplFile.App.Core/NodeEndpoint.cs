namespace XrplFile.App.Core;

/// <summary>
/// Which URL a publication on a given network goes to: the person's override if there is one,
/// otherwise the first endpoint <see cref="Endpoints.For"/> knows, otherwise nothing at all
/// (design 5). A <see langword="null"/> answer is a network this application cannot reach, and the
/// screens turn it into "add a URL in Settings" rather than into a connection attempt.
/// </summary>
public static class NodeEndpoint
{
    public static string? Resolve(INodeSettingsStore store, string network)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(network);

        if (store.Read().TryGetValue(network, out string? chosen) && !string.IsNullOrWhiteSpace(chosen))
        {
            return chosen.Trim();
        }

        IReadOnlyList<string> known = Endpoints.For(network);
        return known.Count == 0 ? null : known[0];
    }
}
