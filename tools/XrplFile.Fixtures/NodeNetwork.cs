using Xrpl.Client;
using Xrpl.Models.Methods;

namespace XrplFile;

/// <summary>
/// Checks that the node a run connected to serves the network the command line named. The network decides
/// what is signed and how the prompts behave (seeds from a pipe, the confirmation phrase), so it must come from
/// the node and not from the owner's word alone: a mainnet <c>--node</c> with <c>--network testnet</c> would
/// otherwise spend real fees without the phrase.
/// </summary>
internal static class NodeNetwork
{
    /// <summary>
    /// The network name for the <c>network_id</c> of a node's <c>server_info</c>: absent or 0 is mainnet, 1 and 2 are the
    /// public testnet and devnet, anything else is its number. Same rule as the site price tool.
    /// </summary>
    public static string NameOf(uint? networkId) => networkId switch
    {
        null or 0 => "mainnet",
        1 => "testnet",
        2 => "devnet",
        uint other => other.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <exception cref="InvalidOperationException">The node serves another network than <paramref name="expected"/>.</exception>
    public static void Require(uint? networkId, string expected, string nodeUrl)
    {
        string actual = NameOf(networkId);

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"--network is {expected} but the node {nodeUrl} serves {actual}; nothing was signed or submitted");
        }
    }

    public static async Task RequireAsync(XrplClient client, string expected, string nodeUrl, CancellationToken cancellationToken)
    {
        XrplResponse<ServerInfo> info = await client.ServerInfo(new ServerInfoRequest(), cancellationToken).ConfigureAwait(false);
        Require(info.Result?.Info?.NetworkID, expected, nodeUrl);
    }
}
