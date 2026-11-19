using Xrpl.AddressCodec;

namespace XrplFile;

internal enum FixtureNetwork
{
    Testnet,
    Mainnet,
}

/// <summary>
/// What the command line asked for. Three positional arguments alone mean the testnet throwaway mode;
/// <c>--writer</c> and <c>--payer</c> switch to publishing from the owner's own accounts, whose seeds
/// are never accepted here: they are typed at a hidden prompt.
/// </summary>
internal sealed record RunOptions(
    string PlanPath, string MediaDirectory, string OutDirectory, FixtureNetwork Network, string NodeUrl, string? WriterAddress, string? PayerAddress)
{
    public const string TestnetNode = "wss://s.altnet.rippletest.net:51233";
    public const string MainnetNode = "wss://xrplcluster.com";

    public bool OwnAccounts => WriterAddress is not null;

    public string NetworkName => Network == FixtureNetwork.Mainnet ? "mainnet" : "testnet";

    /// <summary>Parses the arguments and reports every problem at once through a <see cref="PlanException"/>.</summary>
    public static RunOptions Parse(IReadOnlyList<string> args)
    {
        List<string> problems = [];
        List<string> positionals = [];
        string? network = null, writer = null, payer = null, node = null;

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            if (argument is "--network" or "--writer" or "--payer" or "--node")
            {
                if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    problems.Add($"{argument} needs a value");
                    continue;
                }

                string value = args[++index];
                bool alreadyGiven = argument switch
                {
                    "--network" => network is not null,
                    "--writer" => writer is not null,
                    "--payer" => payer is not null,
                    _ => node is not null,
                };

                // Taking the last one silently would let a typo in a long command line pick the account that pays.
                if (alreadyGiven)
                {
                    problems.Add($"{argument} is given twice");
                    continue;
                }

                if (argument == "--network") network = value;
                else if (argument == "--writer") writer = value;
                else if (argument == "--payer") payer = value;
                else node = value;
            }
            else if (argument == "--seed")
            {
                problems.Add("unknown option '--seed': seeds are only typed at the prompt, never passed as arguments");
            }
            else
            {
                problems.Add($"unknown option '{argument}'");
            }
        }

        if (positionals.Count != 3) problems.Add("expected <plan.json> <media-dir> <out-dir>");

        FixtureNetwork parsedNetwork = FixtureNetwork.Testnet;
        if (network is not null)
        {
            if (string.Equals(network, "mainnet", StringComparison.Ordinal)) parsedNetwork = FixtureNetwork.Mainnet;
            else if (!string.Equals(network, "testnet", StringComparison.Ordinal)) problems.Add($"--network is '{network}', expected 'testnet' or 'mainnet'");
        }

        bool own = writer is not null || payer is not null || parsedNetwork == FixtureNetwork.Mainnet;

        if (own)
        {
            if (writer is null) problems.Add("--writer is required when publishing from your own accounts");
            else if (!XrplCodec.IsValidClassicAddress(writer)) problems.Add($"--writer '{writer}' is not a classic address");

            if (payer is null) problems.Add("--payer is required when publishing from your own accounts");
            else if (!XrplCodec.IsValidClassicAddress(payer)) problems.Add($"--payer '{payer}' is not a classic address");

            if (writer is not null && string.Equals(writer, payer, StringComparison.Ordinal))
            {
                problems.Add("--writer and --payer must be different accounts");
            }
        }

        if (node is not null && !node.StartsWith("wss://", StringComparison.Ordinal)) problems.Add($"--node '{node}' must be a wss:// URL");

        if (problems.Count > 0) throw new PlanException(problems);

        return new RunOptions(
            positionals[0], positionals[1], positionals[2], parsedNetwork,
            node ?? (parsedNetwork == FixtureNetwork.Mainnet ? MainnetNode : TestnetNode),
            own ? writer : null, own ? payer : null);
    }
}
