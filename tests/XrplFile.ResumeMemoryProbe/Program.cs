using System.Text.Json;
using XrplFile.ResumeMemoryProbe;

if (args.Length != 4)
{
    Console.Error.WriteLine("usage: XrplFile.ResumeMemoryProbe <node url> <state file> <payer account> <vault file>");
    return 2;
}

using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(30));
ResumeProbeResult result = await ResumeProbe.RunAsync(args[0], args[1], args[2], args[3], timeout.Token);
Console.WriteLine(JsonSerializer.Serialize(result));
return 0;
