namespace XrplFile.App.Core;

/// <summary>One entry of a network drop-down: the canonical value a key is stored under, and what the person reads.</summary>
public sealed record NetworkChoice(string Value, string Label);

/// <summary>
/// The networks a key can be put on, as the add-key and new-account screens offer them -- a closed
/// list rather than a text box, because the network a key is filed under has to be one an xrpl-file
/// link can name (<see cref="XrplFileLink.TryNormalizeNetwork"/>) and a typed value that is not was
/// stored raw before this list existed.
/// </summary>
/// <remarks>
/// <see cref="Endpoints.KnownNetworks"/> come first in their own fixed order, then every other
/// network the node-settings store knows -- a NetworkID of a private stand, say -- so a network a
/// person configured under Settings is one they can then pick. Each is normalised, a repeat is
/// dropped, and a stored name no link can carry (the store accepts any string a hand-edited file
/// gives it) is skipped rather than offered as something the model would refuse a moment later. The
/// list is read from the store on <see cref="Refresh"/>, not on every access: a screen refreshes it
/// when it opens, the same way <see cref="NodeSettingsModel"/> is refreshed.
/// </remarks>
public sealed class NetworkChoices(INodeSettingsStore store)
{
    public IReadOnlyList<NetworkChoice> Choices { get; private set; } = [];

    public void Refresh()
    {
        List<string> values = [];
        foreach (string candidate in Endpoints.KnownNetworks.Concat(store.Read().Keys))
        {
            if (XrplFileLink.TryNormalizeNetwork(candidate, out string? canonical)
                && !values.Contains(canonical, StringComparer.Ordinal))
            {
                values.Add(canonical);
            }
        }

        Choices = [.. values.Select(value => new NetworkChoice(value, LabelFor(value)))];
    }

    /// <summary>
    /// The canonical form of <paramref name="network"/> when it names one of <see cref="Choices"/>,
    /// otherwise <see langword="null"/> -- what a screen uses to preselect a value it was handed (a
    /// backup container's own network) only when the list can actually show it.
    /// </summary>
    public string? Pick(string? network) =>
        XrplFileLink.TryNormalizeNetwork(network?.Trim(), out string? canonical)
        && Choices.Any(choice => string.Equals(choice.Value, canonical, StringComparison.Ordinal))
            ? canonical
            : null;

    /// <summary>The sentence for a network no xrpl-file link can name, shared by every screen that refuses one.</summary>
    public static string NotANetwork(string network) =>
        $"'{network}' is not a network an xrpl-file link can name. Use mainnet, testnet, devnet, "
        + "or a network ID in digits.";

    private static string LabelFor(string value) =>
        Endpoints.KnownNetworks.Contains(value, StringComparer.Ordinal) ? value : $"NetworkID {value}";
}
