using System.Globalization;
using System.Text.Json;
using XrplFile.MemoryProbe;

if (args.Length != 1 || !long.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) || bytes <= 0)
{
    Console.Error.WriteLine("usage: XrplFile.MemoryProbe <bytes>");
    return 2;
}

// The 800 MB probe signs and decodes about 130,000 outers twice over; on a slow host that takes most
// of an hour, so the probe's own limit is the test's.
using CancellationTokenSource timeout = new(TimeSpan.FromHours(3));
ProbeResult result = await Probe.RunAsync(bytes, timeout.Token);
Console.WriteLine(JsonSerializer.Serialize(result));
return 0;
