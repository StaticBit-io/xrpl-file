using XrplFile.Keys;

namespace XrplFile.Tests;

/// <summary>
/// A signer that locks the vault once it has given out a set number of signatures, and then hands
/// the run on to a signer that can no longer sign.
///
/// The lock is driven by the number of signatures taken rather than by a clock or by a progress
/// report. A clock is a race against the node, and the writer reports its per-chunk progress
/// through <see cref="Progress{T}"/>, which lands on the thread pool — so a lock hung off either
/// one may fire after the last chunk, where it proves nothing. A count fires exactly once, between
/// two known chunks.
///
/// It is a decorator rather than something <see cref="VaultSigner"/> knows how to do, because what
/// is under test is how a real signer behaves when the vault shuts under it. A signer that knew
/// about the lock would be rehearsing the event rather than meeting it.
/// </summary>
internal sealed class LockingSigner(ITransactionSigner inner, IKeyVault vault, int lockAfter) : ITransactionSigner
{
    private int _signatures;

    /// <summary>How many times a signature was asked for, the refused one included.</summary>
    public int Signatures => Volatile.Read(ref _signatures);

    public string Account => inner.Account;

    public string PublicKey => inner.PublicKey;

    public async Task<SignedTransaction> SignAsync(
        IDictionary<string, object> transaction, CancellationToken cancellationToken)
    {
        // The value this call's own increment returns, not a second read of the shared field:
        // Interlocked.Increment hands out a distinct number to every caller, so this decision can
        // never be affected by what a concurrent caller does to the same field. A re-read here
        // raced against SequenceStrategy's sequential use of this class only by accident — nothing
        // stopped two callers under TicketStrategy's parallel submission from both seeing the same
        // already-advanced value and both locking, or from a call below the threshold locking
        // because a neighbour's higher value was what it happened to read.
        int signatures = Interlocked.Increment(ref _signatures);

        // Before the signature, not after: on the call that trips the threshold the vault is
        // already shut, so the refusal comes out of the vault itself rather than out of a count
        // this class keeps. That is the behaviour under test.
        if (signatures > lockAfter) await vault.LockAsync().ConfigureAwait(false);

        return await inner.SignAsync(transaction, cancellationToken).ConfigureAwait(false);
    }
}
