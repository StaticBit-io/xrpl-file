using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace XrplFile.Tests;

/// <summary>Spec S27: a run's memory is bounded by a round, not by the file.</summary>
[Trait("Category", "Slow")]
public sealed class PublicationMemoryTests
{
    private sealed record Peak(long Bytes, long Chunks, long PeakPrivateBytes, long FakeBytes, double Seconds)
    {
        public long RunBytes => PeakPrivateBytes - FakeBytes;
    }

    private static string ProbePath =>
        typeof(PublicationMemoryTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "MemoryProbePath").Value!;

    private static async Task<Peak> ProbeAsync(long bytes, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ProbePath);
        start.ArgumentList.Add(bytes.ToString(CultureInfo.InvariantCulture));

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("the probe did not start");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        string printed = await output;
        Assert.True(process.ExitCode == 0, $"the probe for {bytes:N0} bytes exited with {process.ExitCode}: {await errors}");

        using JsonDocument result = JsonDocument.Parse(printed.Trim().Split('\n')[^1]);
        JsonElement root = result.RootElement;

        return new Peak(
            root.GetProperty("Bytes").GetInt64(), root.GetProperty("Chunks").GetInt64(), root.GetProperty("PeakPrivateBytes").GetInt64(),
            root.GetProperty("FakeBytes").GetInt64(), root.GetProperty("Seconds").GetDouble());
    }

    [Fact]
    public async Task A_runs_peak_memory_is_bounded_and_grows_little_with_the_file()
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(3));

        Peak small = await ProbeAsync(50_000_000, timeout.Token);
        Peak middle = await ProbeAsync(200_000_000, timeout.Token);
        Peak large = await ProbeAsync(800_000_000, timeout.Token);

        foreach (Peak peak in new[] { small, middle, large })
        {
            Assert.Equal(FileTree.ChunkCount(peak.Bytes, MemoLayout.ChunkBytes), peak.Chunks);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{peak.Bytes:N0} bytes: peak private {peak.PeakPrivateBytes:N0}, the fake's records {peak.FakeBytes:N0}, the run {peak.RunBytes:N0}; {peak.Seconds:F0} s");
        }

        // Absolute: the raw peak, the fake's records included, as the spec states it.
        Assert.True(large.PeakPrivateBytes < 300_000_000, $"the 800 MB run peaked at {large.PeakPrivateBytes:N0} bytes");

        // Slope: net of the fake's preallocated records, which grow with the file by design.
        Assert.True(large.RunBytes - small.RunBytes < 40_000_000, $"from 50 MB to 800 MB the peak grew by {large.RunBytes - small.RunBytes:N0} bytes");
    }
}
