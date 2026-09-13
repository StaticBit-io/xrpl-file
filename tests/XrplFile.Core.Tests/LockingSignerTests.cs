using XrplFile.Keys;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// <see cref="LockingSigner"/> is a shared test double: <c>SequenceStrategy</c> drives it
/// sequentially, but <c>TicketStrategy</c> submits in parallel, so a caller under this signer may
/// call <see cref="LockingSigner.SignAsync"/> from more than one thread at once. What decides
/// whether a given call locks the vault must be that call's own place in the count, not a second,
/// separate read of a counter every other concurrent call is also mutating.
/// </summary>
public class LockingSignerTests
{
    /// <summary>
    /// Interlocked.Increment hands each call a distinct value: no two calls ever see the same one.
    /// Exactly the calls whose own value exceeds <c>lockAfter</c> may lock — <c>total - lockAfter</c>
    /// of them, never more and never fewer. A version that re-reads the shared counter instead of
    /// trusting the value its own increment returned can let two calls agree on one
    /// already-advanced value and both lock, or let a call below the threshold see a neighbour's
    /// higher value and lock when it should not have.
    ///
    /// The race needs real thread parallelism to show, not merely an <c>await</c> interleaving, so
    /// this measured empirically rather than reasoned about: against the unfixed class, 8 calls
    /// racing to a barrier with <c>lockAfter = 4</c> over-locked in roughly 1 trial in 20 (22 of
    /// 500 in one measurement — 17 trials locking 5 times, 5 locking 6 times, against an expected
    /// 4 every time). A single trial is therefore not a reliable witness either way; running many
    /// is — and, once fixed, every single one of them must land on exactly the expected count,
    /// because there is no longer a shared value left to race over.
    /// </summary>
    [Fact]
    public async Task Exactly_the_calls_past_the_threshold_lock_the_vault()
    {
        const int total = 8;
        const int lockAfter = 4;
        const int trials = 300;

        // Pre-warmed so the pool has enough real OS threads on hand for the barrier to release
        // every call into genuine parallel execution; a pool still growing from its default
        // minimum serialises much of the fan-out and starves the race of the window it needs.
        ThreadPool.SetMinThreads(total, total);

        for (int trial = 0; trial < trials; trial++)
        {
            CountingLockVault vault = new();
            LockingSigner signer = new(new InstantSigner(), vault, lockAfter);

            // A barrier rather than a plain Task.Run fan-out, so every call reaches its own
            // increment at roughly the same instant — the window a check-then-act race needs to
            // be exercised rather than merely possible.
            using Barrier barrier = new(total);

            Task[] calls = [.. Enumerable.Range(0, total).Select(_ => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await signer.SignAsync(new Dictionary<string, object>(), TestContext.Current.CancellationToken);
            }))];

            await Task.WhenAll(calls);

            Assert.Equal(total, signer.Signatures);
            Assert.Equal(total - lockAfter, vault.Locks);
        }
    }

    /// <summary>A signer that answers immediately, so nothing about timing comes from here.</summary>
    private sealed class InstantSigner : ITransactionSigner
    {
        public string Account => "rInstantSigner";

        public string PublicKey => "ED0000000000000000000000000000000000000000000000000000000000000000";

        public Task<SignedTransaction> SignAsync(IDictionary<string, object> transaction, CancellationToken cancellationToken) =>
            Task.FromResult(new SignedTransaction("BLOB", "HASH"));
    }

    /// <summary>A vault whose only observable behaviour is counting how often it was locked.</summary>
    private sealed class CountingLockVault : IKeyVault
    {
        private int _locks;

        public int Locks => Volatile.Read(ref _locks);

        public bool IsUnlocked => true;

        public IReadOnlyCollection<string> References => [];

        public IReadOnlyList<string> DamagedReferences => [];

        public Task<bool> ExistsAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            Task.FromResult(VaultUnlockResult.Success);

        public Task LockAsync()
        {
            Interlocked.Increment(ref _locks);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VaultEntryInfo>>([]);

        public Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpdateAsync(string reference, VaultEntryEdit edit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(VaultUnlockResult.VaultMissing);

        public Task<VaultPasswordChange> ChangePasswordAsync(
            ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken) =>
            Task.FromResult(VaultPasswordChange.WrongPassword);

        public Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task DisableDeviceUnlockAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
