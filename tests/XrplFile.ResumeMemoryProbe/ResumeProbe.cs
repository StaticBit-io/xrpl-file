using System.Diagnostics;
using Xrpl.Client;
using XrplFile.Keys;

namespace XrplFile.ResumeMemoryProbe;

/// <param name="FloorPrivateBytes">This process's private bytes before the vault is touched.</param>
/// <param name="AfterUnlockPrivateBytes">Its private bytes once the vault is unlocked again, before the resume starts.</param>
/// <param name="ResumePeakPrivateBytes">The highest private bytes sampled from the start of the resume to its approval.</param>
/// <param name="MissingChunks">The chunks the resume's history read did not find in the ledger.</param>
internal sealed record ResumeProbeResult(long FloorPrivateBytes, long AfterUnlockPrivateBytes, long ResumePeakPrivateBytes, long MissingChunks);

/// <summary>
/// A Batch resume as the desktop application runs one after Lock (D1): the vault is unlocked at its
/// default Argon2id cost, then the run is resumed over the real SDK client, which hashes the file and
/// reads the writer's whole history, and the quote is declined so nothing is sent.
/// </summary>
internal static class ResumeProbe
{
    private static readonly ReadOnlyMemory<byte> Password = "resume memory probe"u8.ToArray();

    /// <summary>Fine enough to see a heap that grows page by page; the history read takes seconds per thousand pages' worth.</summary>
    private static readonly TimeSpan SampleEvery = TimeSpan.FromMilliseconds(50);

    public static async Task<ResumeProbeResult> RunAsync(
        string endpoint, string statePath, string payer, string vaultPath, CancellationToken cancellationToken)
    {
        long floor = PrivateBytes();

        await using (KeyVault vault = new(vaultPath, new Argon2idKdf(), new NoDeviceSecretStore(), KdfParameters.Default))
        {
            await vault.CreateAsync(Password, cancellationToken).ConfigureAwait(false);
            await vault.LockAsync().ConfigureAwait(false);

            VaultUnlockResult unlocked = await vault.UnlockAsync(Password, cancellationToken).ConfigureAwait(false);
            if (unlocked != VaultUnlockResult.Success) throw new InvalidOperationException($"the probe's vault did not unlock: {unlocked}");
        }

        long afterUnlock = PrivateBytes();

        XrplClient client = ClientKeeper.CreateClient(endpoint);
        await client.Connect(cancellationToken).ConfigureAwait(false);

        try
        {
            XrplClientLedgerSource source = new(client);
            PublicationState state = PublicationState.Load(statePath);
            long missing = -1;

            SessionOptions options = new()
            {
                StateDirectory = Path.GetDirectoryName(statePath)!,
                FencePoll = TimeSpan.FromMilliseconds(500),
                ApproveResume = (quote, _) =>
                {
                    missing = quote.MissingChunks;
                    return Task.FromException<SpendingApproval>(new OperationCanceledException("declined: the probe measures up to the approval"));
                },
            };

            BatchStrategyFactory refuse = (_, _) => throw new InvalidOperationException("the probe declines before a strategy is built");
            using PeakSampler sampler = new(SampleEvery);

            try
            {
                await using FilePublicationSource file = FilePublicationSource.Open(
                    state.SourcePath ?? throw new InvalidOperationException($"{statePath} records no source path"));
                await PublicationSession.ResumeAsync(refuse, source, file, statePath, payer, "payer", options, null, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("the resume went on without its approval");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && missing >= 0)
            {
                // Declined at the quote, as intended: the history read is behind it.
            }

            return new ResumeProbeResult(floor, afterUnlock, sampler.Stop(), missing);
        }
        finally
        {
            await client.Disconnect().ConfigureAwait(false);
        }
    }

    internal static long PrivateBytes()
    {
        using Process self = Process.GetCurrentProcess();
        return self.PrivateMemorySize64;
    }

    /// <summary>
    /// Samples on its own thread: the process's peak figure would include the unlock's Argon2id
    /// working memory, which is the vault's cost and comes before the resume.
    /// </summary>
    private sealed class PeakSampler : IDisposable
    {
        private readonly Thread _thread;
        private readonly TimeSpan _every;
        private volatile bool _stopped;
        private long _peak;

        public PeakSampler(TimeSpan every)
        {
            _every = every;
            _peak = PrivateBytes();
            _thread = new Thread(Run) { IsBackground = true, Name = "resume memory sampler" };
            _thread.Start();
        }

        private void Run()
        {
            while (!_stopped)
            {
                _peak = Math.Max(_peak, PrivateBytes());
                Thread.Sleep(_every);
            }
        }

        public long Stop()
        {
            _stopped = true;
            _thread.Join();

            return Math.Max(_peak, PrivateBytes());
        }

        public void Dispose()
        {
            if (!_stopped) Stop();
        }
    }
}
