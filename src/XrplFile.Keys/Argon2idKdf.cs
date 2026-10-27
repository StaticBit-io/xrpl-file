using Konscious.Security.Cryptography;

namespace XrplFile.Keys;

/// <inheritdoc cref="IPasswordKdf"/>
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
        // reclaims. Nothing an unlocked application does next allocates enough to bring one soon,
        // so the process kept the whole 256 MiB committed into the next run (measured on a resume
        // after Lock: 341-395 MiB private while it hashed the file, against 100-205 MiB through the
        // first pass). Only an aggressive collection returns it at once; a forced one leaves the
        // freed regions committed. It runs off the dispatcher, while the person waits for the
        // unlock anyway.
        if (parameters.MemoryKib >= ReleaseFromMemoryKib)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }

        return key;
    }
}
