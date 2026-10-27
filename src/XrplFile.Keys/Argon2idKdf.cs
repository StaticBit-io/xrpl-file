using Konscious.Security.Cryptography;

namespace XrplFile.Keys;

/// <inheritdoc cref="IPasswordKdf"/>
/// <remarks>
/// A derivation with 16 MiB of working memory or more ends with a blocking, compacting, aggressive
/// full collection (<see cref="GCCollectionMode.Aggressive"/>), so its lanes do not stay committed
/// after the key exists. That collection pauses every thread of the process for as long as a full
/// collection of the process's heap takes. Call <see cref="Derive"/> off any UI thread;
/// <c>KeyVault</c> already does, inside <c>Task.Run</c>.
/// </remarks>
public sealed class Argon2idKdf : IPasswordKdf
{
    /// <summary>
    /// The working memory from which a derivation hands its lanes back at once. Below it they are
    /// small next to any process's own heap, and a full collection would cost more than it returns.
    /// </summary>
    private const int ReleaseFromMemoryKib = 16 * 1024;

    public int KdfId => 2;

    public byte[] Derive(
        ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters, int outputLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.MemoryKib, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.Iterations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.Parallelism, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputLength, 4);

        byte[] key;

        using (Argon2id argon = new(password.ToArray())
        {
            Salt = salt.ToArray(),
            MemorySize = parameters.MemoryKib,
            Iterations = parameters.Iterations,
            DegreeOfParallelism = parameters.Parallelism,
        })
        {
            key = argon.GetBytes(outputLength);
        }

        // Konscious allocates one managed array per lane, 64 MiB each at the vault's default cost:
        // large-object-heap garbage the moment the key exists, which only a gen2 collection
        // reclaims. Only an aggressive collection returns it at once; a forced one leaves the
        // freed regions committed. It runs off the dispatcher, while the person waits for the
        // unlock anyway.
        if (parameters.MemoryKib >= ReleaseFromMemoryKib)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }

        return key;
    }
}
