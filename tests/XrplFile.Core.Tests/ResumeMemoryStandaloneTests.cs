using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// D1: a Batch resume after Lock, measured as the desktop application runs it — the vault unlocked
/// at its default Argon2id cost, the real SDK client, the application's collector settings — in a
/// child process, against the stand. S27's own probe runs on a compact fake and so cannot see what
/// the client allocates per history record.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Slow")]
[Collection("Standalone")]
public sealed class ResumeMemoryStandaloneTests : IDisposable
{
    private const int FileBytes = 40_000_000;

    /// <summary>About 32,000 chunks in the ledger: enough history that dead pages piled up past the bound before the fix.</summary>
    private const int StopAfterOuters = 4_000;

    private static readonly BatchStrategyOptions Options = new()
    {
        ChainCheck = TimeSpan.FromMilliseconds(500),
        ProgressSweep = TimeSpan.FromSeconds(1),
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-resume-memory-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    private static string ProbePath =>
        typeof(ResumeMemoryStandaloneTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ResumeMemoryProbePath").Value!;

    private sealed record ProbeResult(long FloorPrivateBytes, long AfterUnlockPrivateBytes, long ResumePeakPrivateBytes, long MissingChunks);

    [Fact]
    public async Task A_resume_after_an_unlock_reads_the_history_within_the_memory_bound_and_keeps_no_kdf_memory()
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(60));
        CancellationToken token = timeout.Token;

        // Closes ledgers while the probe runs too: its resume waits for the fence before it reads.
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        string statePath;
        string payer;

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 1_000_000_000, token);
            XrplWallet payerWallet = await StandaloneFixture.FundedAccountAsync(client, sink, 1_000_000_000, token);
            payer = payerWallet.ClassicAddress;

            string path = Path.Combine(_directory, "resume-memory.bin");
            await File.WriteAllBytesAsync(path, VectorStream.Bytes("resume-memory.bin", FileBytes), token);

            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            StoppingSink stopping = new(sink, stop, StopAfterOuters);
            List<BatchStrategy> built = [];
            BatchStrategyFactory factory = (budget, fence) =>
            {
                BatchStrategy strategy = new(stopping, source, new WalletSigner(writer), new WalletSigner(payerWallet), budget, fence, Options);
                built.Add(strategy);
                return strategy;
            };

            SessionOptions session = new()
            {
                StateDirectory = Path.Combine(_directory, "state"),
                FencePoll = TimeSpan.FromMilliseconds(500),
                SourcePath = path,
            };

            try
            {
                await using FilePublicationSource file = FilePublicationSource.Open(path);
                PublicationSession first = await PublicationSession.StartAsync(
                    factory, source, file, "application/octet-stream", "resume-memory.bin",
                    writer.ClassicAddress, "writer", payer, "payer", "1", session, null, stop.Token);
                statePath = first.StatePath;

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(null, stop.Token));
            }
            finally
            {
                foreach (BatchStrategy strategy in built) await strategy.DisposeAsync();
            }
        }
        finally
        {
            await client.Disconnect();
        }

        ProbeResult result = await ProbeAsync(statePath, payer, Path.Combine(_directory, "vault.json"), token);
        long chunks = FileTree.ChunkCount(FileBytes, MemoLayout.ChunkBytes);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"floor {result.FloorPrivateBytes:N0}, after unlock {result.AfterUnlockPrivateBytes:N0}, resume peak {result.ResumePeakPrivateBytes:N0}; "
            + $"{chunks - result.MissingChunks:N0} of {chunks:N0} chunks found in the history");

        // The history read had real work to do: most of what was sent is in the ledger.
        Assert.InRange(chunks - result.MissingChunks, StopAfterOuters * 6L, chunks - 1);

        // S27's bound, for the resume as the application runs it.
        Assert.True(result.ResumePeakPrivateBytes < 300_000_000, $"the resume peaked at {result.ResumePeakPrivateBytes:N0} bytes");

        // The vault's 256 MiB of Argon2id lanes are garbage once the key is derived; they must not stay committed.
        Assert.True(result.AfterUnlockPrivateBytes - result.FloorPrivateBytes < 64L * 1024 * 1024,
            $"after the unlock the process held {result.AfterUnlockPrivateBytes - result.FloorPrivateBytes:N0} bytes more than before it");
    }

    private static async Task<ProbeResult> ProbeAsync(string statePath, string payer, string vaultPath, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ProbePath);
        start.ArgumentList.Add(Endpoint);
        start.ArgumentList.Add(statePath);
        start.ArgumentList.Add(payer);
        start.ArgumentList.Add(vaultPath);

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
        Assert.True(process.ExitCode == 0, $"the probe exited with {process.ExitCode}: {await errors}");

        return JsonSerializer.Deserialize<ProbeResult>(printed.Trim().Split('\n')[^1])
            ?? throw new InvalidOperationException($"the probe printed no result: {printed}");
    }
}
