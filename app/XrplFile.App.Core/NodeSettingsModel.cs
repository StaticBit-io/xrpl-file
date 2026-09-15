namespace XrplFile.App.Core;

/// <summary>
/// One row of the node-settings section (design 5): a network this application can be told a node
/// for, and the URL currently in force for it -- a person's own override, or an empty string when
/// none has been recorded and <see cref="Endpoints.For"/>'s own answer is what publishing actually
/// uses.
/// </summary>
public sealed record NodeSettingRow(string Network, string Url);

/// <summary>
/// The node-URL section of Settings (design 5). Not part of the original task-13 plan, which had
/// this logic live directly in <c>Settings.razor</c> -- moved here because which networks to offer,
/// what an empty box means, and whether a save is valid are all decisions, and a Razor component in
/// this application holds none (see task 13's own correction to its brief, and C1's E2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Which rows to show.</b> <see cref="Endpoints.KnownNetworks"/> is listed every time, in its own
/// fixed order, whether or not a person has ever touched any of it, so the built-in networks are
/// visible and editable without already knowing they exist. Anything else in
/// <see cref="INodeSettingsStore.Read"/> -- a NetworkID typed in before, a private stand's name such
/// as <c>"standalone"</c> -- is appended after them, in the order
/// <see cref="IReadOnlyDictionary{TKey,TValue}.Keys"/> happens to enumerate it: nothing about that
/// order is meaningful, so nothing here tries to make it one.
/// </para>
/// <para>
/// <b>The row a person cannot already reach.</b> A network that has never been overridden and is not
/// a known name has no row at all -- there is nothing to show a value for and nothing to edit. That
/// used to mean a dead end: <see cref="PublishModel.Message"/>'s own remedy for exactly this case
/// ("Add one in Settings") pointed at a box that did not exist, for the one network this feature was
/// actually built to serve -- a locally run standalone node, whose network is a NetworkID like
/// <c>"1"</c>, in neither list on first use. <see cref="AddNetwork"/> is the way in: it takes a
/// network name and a URL together, validates the name the same way a link itself would refuse one
/// (<see cref="XrplFileLink.TryNormalizeNetwork"/> -- a name no link can carry is not worth storing),
/// and writes the pair straight to the store rather than only adding an empty row for a later
/// <see cref="Save"/> to fill in.
/// </para>
/// <para>
/// <b>What an empty box means.</b> <see cref="NodeEndpoint.Resolve"/> already treats a blank or
/// whitespace-only override as no override at all, so <see cref="Save"/> drops a blank box from what
/// it writes rather than persisting an empty string that would mean the same thing on the next read
/// and only clutter the file.
/// </para>
/// <para>
/// <b>What counts as a bad value.</b> Every non-blank box has to be something
/// <c>Xrpl.Client.XrplClient</c> can actually dial: an absolute URI whose scheme is <c>ws</c> or
/// <c>wss</c>, the only transport the rippled WebSocket API this application speaks understands.
/// <see cref="Save"/> refuses the whole write rather than saving the good rows and dropping the bad
/// one -- a partial save would leave a person unsure which of their edits actually took, and the one
/// this refuses would otherwise surface three screens later, inside a run that has already spent
/// money finding out its node is unreachable.
/// </para>
/// </remarks>
public sealed class NodeSettingsModel(INodeSettingsStore store)
{
    public IReadOnlyList<NodeSettingRow> Rows { get; private set; } = [];

    /// <summary>Why the last <see cref="Save"/> or <see cref="AddNetwork"/> did not take effect, or <see langword="null"/> when it did.</summary>
    public string? Message { get; private set; }

    public void Refresh()
    {
        Message = null;

        IReadOnlyDictionary<string, string> overrides = store.Read();

        List<string> networks = [.. Endpoints.KnownNetworks];
        foreach (string network in overrides.Keys)
        {
            if (!networks.Contains(network, StringComparer.Ordinal))
            {
                networks.Add(network);
            }
        }

        Rows = [.. networks.Select(network => new NodeSettingRow(
            network, overrides.TryGetValue(network, out string? url) ? url : string.Empty))];
    }

    /// <summary>
    /// Adds a network not already listed, with the URL that reaches it, in one step -- the row
    /// <see cref="Refresh"/> alone can never produce for a network nobody has configured yet. Refused,
    /// with <see cref="Message"/> saying why and nothing written, when <paramref name="network"/> is
    /// not one <see cref="XrplFileLink.TryNormalizeNetwork"/> accepts (the same rule a link itself
    /// enforces at construction) or when <paramref name="url"/> is blank or not a <c>ws</c>/<c>wss</c>
    /// URL -- there is nothing useful in a row with no address. On success, <see cref="Refresh"/> runs
    /// so <see cref="Rows"/> shows the new row at its canonical network name.
    /// </summary>
    public bool AddNetwork(string network, string url)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(url);

        Message = null;

        if (!XrplFileLink.TryNormalizeNetwork(network, out string? canonical))
        {
            Message = $"'{network}' is not a network an xrpl-file link can name.";
            return false;
        }

        string trimmedUrl = url.Trim();
        if (trimmedUrl.Length == 0)
        {
            Message = "Give the new network a URL to save it with.";
            return false;
        }

        if (!IsWebSocketUrl(trimmedUrl))
        {
            Message = $"'{url}' is not a ws:// or wss:// URL.";
            return false;
        }

        Dictionary<string, string> written = new(store.Read(), StringComparer.Ordinal)
        {
            [canonical] = trimmedUrl,
        };

        store.Write(written);
        Refresh();
        return true;
    }

    /// <summary>
    /// Saves every row at once. <see langword="false"/> when a non-blank box is not a <c>ws</c> or
    /// <c>wss</c> URL, with <see cref="Message"/> saying which one and why -- nothing is written in
    /// that case, not even the rows that were fine. On success, <see cref="Refresh"/> runs again so
    /// <see cref="Rows"/> reflects exactly what was written, including a blank box's override having
    /// disappeared rather than lingering as the value this call was passed.
    /// </summary>
    /// <param name="edits">Every row's network mapped to whatever is currently in its box.</param>
    public bool Save(IReadOnlyDictionary<string, string> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);

        Message = null;

        Dictionary<string, string> cleaned = new(StringComparer.Ordinal);
        foreach ((string network, string rawUrl) in edits)
        {
            string trimmed = rawUrl.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!IsWebSocketUrl(trimmed))
            {
                Message = $"'{rawUrl}' for {network} is not a ws:// or wss:// URL.";
                return false;
            }

            cleaned[network] = trimmed;
        }

        store.Write(cleaned);
        Refresh();
        return true;
    }

    private static bool IsWebSocketUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && (string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase));
}
