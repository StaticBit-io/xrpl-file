using System.Diagnostics;
using System.Globalization;
using Xrpl.Wallet;
using XrplFile.Tests;

namespace XrplFile.MemoryProbe;

internal sealed record ProbeResult(long Bytes, long Chunks, long PeakPrivateBytes, long FakeBytes, double Seconds);

/// <summary>One Batch publication, stopped halfway through its chunks and resumed, against <see cref="CompactLedger"/> (spec S27).</summary>
internal static class Probe
{
    private const uint WriterStart = 100;
    private const uint PayerStart = 500;

    /// <summary>The Batch tests' fast options: the fake closes a ledger on every read, so deadlines are in ledgers, not seconds.</summary>
    private static readonly BatchStrategyOptions Options = new()
    {
        ChainCheck = TimeSpan.FromMilliseconds(5),
        ProgressSweep = TimeSpan.FromMilliseconds(250),
        HistoryLagLimit = TimeSpan.FromSeconds(2),
        FeeWait = TimeSpan.FromMilliseconds(20),
        TransportRetryStep = TimeSpan.FromMilliseconds(1),
        DeadlineLedgers = 200,
    };

    public static async Task<ProbeResult> RunAsync(long bytes, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        ITransactionSigner writer = new WalletSigner(XrplWallet.Generate());
        ITransactionSigner payer = new WalletSigner(XrplWallet.Generate());
        await using VectorPublicationSource file = new("memory-probe", bytes);
        long chunks = FileTree.ChunkCount(bytes, MemoLayout.ChunkBytes);
        CompactLedger ledger = new(file, writer.Account, WriterStart, payer.Account, PayerStart, failOnce: Every(10_007, chunks));
        string stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-memory-probe-").FullName;
        List<BatchStrategy> built = [];

        BatchStrategyFactory factory = (budget, fence) =>
        {
            BatchStrategy strategy = new(ledger, ledger, writer, payer, budget, fence, Options);
            built.Add(strategy);
            return strategy;
        };

        try
        {
            SessionOptions session = new() { StateDirectory = stateDirectory, FencePoll = TimeSpan.FromMilliseconds(5) };
            PublicationSession first = await PublicationSession.StartAsync(
                factory, ledger, file, "application/octet-stream", "probe.bin", writer.Account, "writer", payer.Account, "payer", "1",
                session, null, cancellationToken).ConfigureAwait(false);

            using (CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    await first.RunAsync(new StopAt(stop, chunks / 2), stop.Token).ConfigureAwait(false);
                    throw new InvalidOperationException("the run finished before it could be stopped halfway");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Stopped halfway, as intended: the resume below is half of what is measured.
                }
            }

            PublicationSession resumed = await PublicationSession.ResumeAsync(
                factory, ledger, file, first.StatePath, payer.Account, "payer", session, null, cancellationToken).ConfigureAwait(false);
            PublicationReport report = await resumed.RunAsync(null, cancellationToken).ConfigureAwait(false);

            if (report.ChunkCount != chunks) throw new InvalidOperationException($"the report counts {report.ChunkCount} chunks of {chunks}");

            return new ProbeResult(bytes, report.ChunkCount, PeakPrivateBytes(), ledger.ReservedBytes, clock.Elapsed.TotalSeconds);
        }
        finally
        {
            foreach (BatchStrategy strategy in built) await strategy.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    private static IEnumerable<long> Every(long step, long chunks)
    {
        for (long index = 5; index < chunks; index += step) yield return index;
    }

    /// <summary>The peak of this process's private memory.</summary>
    private static long PeakPrivateBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            using Process self = Process.GetCurrentProcess();
            self.Refresh();

            // PeakPagefileUsage: the peak of the private commit charge, which is what "private bytes" plots.
            return self.PeakPagedMemorySize64;
        }

        // Linux keeps no peak of private memory; the peak resident set is the nearest figure it keeps.
        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (!line.StartsWith("VmHWM:", StringComparison.Ordinal)) continue;

            string kilobytes = line["VmHWM:".Length..].Trim().Split(' ')[0];
            return long.Parse(kilobytes, NumberStyles.None, CultureInfo.InvariantCulture) * 1024;
        }

        throw new PlatformNotSupportedException("this platform reports no peak memory figure");
    }

    /// <summary>Stops the run once its chunk pass reports <paramref name="at"/> chunks.</summary>
    private sealed class StopAt(CancellationTokenSource stop, long at) : IProgress<PublishProgress>
    {
        public void Report(PublishProgress value)
        {
            // Off the reporting thread: Cancel runs the token's callbacks synchronously, and a report
            // comes from inside the strategy's own locks.
            if (value.Stage == "chunks" && value.Done >= at) _ = Task.Run(stop.Cancel);
        }
    }
}
