using System.Text;
using Xrpl.Client;
using XrplFile;
using XrplFile.SitePrice;

// Writes site/data/price.json from the publisher's own estimate at the network's current fees. It only
// reads: no key, no signature, no submission, no account is created or funded.
// Usage: dotnet run --project tools/XrplFile.SitePrice -- [--node wss://xrplcluster.com] [--out site/data/price.json]

const string DefaultNode = "wss://xrplcluster.com";

long[] sizes = [10_240, 51_200, 184_320, 1_048_576];

string node = DefaultNode;
string output = Path.Combine("site", "data", "price.json");

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--node" when i + 1 < args.Length:
            node = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            output = args[++i];
            break;
        default:
            Console.Error.WriteLine("Usage: XrplFile.SitePrice [--node <wss url>] [--out <path>]");
            return 2;
    }
}

using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));

XrplClient client = ClientKeeper.CreateClient(node);

try
{
    await client.Connect(timeout.Token);

    PriceTable table = await SitePrice.ComputeAsync(
        new XrplClientLedgerSource(client), sizes, DateTimeOffset.UtcNow, timeout.Token);

    table = table with { Node = node };

    string? directory = Path.GetDirectoryName(Path.GetFullPath(output));
    if (directory is not null) Directory.CreateDirectory(directory);

    await File.WriteAllTextAsync(output, SitePrice.ToJson(table) + "\n", new UTF8Encoding(false), timeout.Token);

    Console.WriteLine($"Wrote {output} from {node}: {table.Rows.Count} rows.");

    return 0;
}
catch (Exception ex) when (ex is XrplFileException or OperationCanceledException or IOException)
{
    Console.Error.WriteLine($"Could not take the price from {node}: {ex.Message}");

    return 1;
}
finally
{
    try
    {
        ClientKeeper.Retire(client);
        await client.Disconnect();
    }
    catch (Exception)
    {
        // The table is already written or the run already failed; a close error changes neither.
    }
}
