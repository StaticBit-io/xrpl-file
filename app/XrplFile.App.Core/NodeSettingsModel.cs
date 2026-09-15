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
/// <b>Which rows to show.</b> <see cref="Endpoints.For"/> knows exactly three names --
/// <c>mainnet</c>, <c>testnet</c>, <c>devnet</c> -- and those are listed every time, in that fixed
/// order, whether or not a person has ever touched them, so the built-in networks are visible and
/// editable without already knowing they exist. Anything else in <see cref="INodeSettingsStore.Read"/>
/// -- a NetworkID typed in before, a private stand's name such as <c>"standalone"</c> -- is appended
/// after them, in the order <see cref="IReadOnlyDictionary{TKey,TValue}.Keys"/> happens to enumerate
/// it: nothing about that order is meaningful, so nothing here tries to make it one. A network that
/// has never been overridden and is not one of the three known names has no row at all -- there is
/// nothing to show a value for and nothing to edit -- which matches <see cref="PublishModel.Message"/>'s
/// own remedy for that case ("Add one in Settings"): the row appears the first time a person actually
/// types a URL for it and saves, at which point <see cref="Refresh"/> finds it in the file.
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
    private static readonly string[] KnownNetworks = ["mainnet", "testnet", "devnet"];

    public IReadOnlyList<NodeSettingRow> Rows { get; private set; } = [];

    /// <summary>Why the last <see cref="Save"/> did not take effect, or <see langword="null"/> when it did.</summary>
    public string? Message { get; private set; }

    public void Refresh()
    {
        Message = null;

        IReadOnlyDictionary<string, string> overrides = store.Read();

        List<string> networks = [.. KnownNetworks];
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
