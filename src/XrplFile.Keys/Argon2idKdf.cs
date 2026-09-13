using Konscious.Security.Cryptography;

namespace XrplFile.Keys;

/// <inheritdoc cref="IPasswordKdf"/>
public sealed class Argon2idKdf : IPasswordKdf
{
    public int KdfId => 2;

    public byte[] Derive(
        ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters, int outputLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.MemoryKib, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.Iterations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.Parallelism, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputLength, 4);

        using Argon2id argon = new(password.ToArray())
        {
            Salt = salt.ToArray(),
            MemorySize = parameters.MemoryKib,
            Iterations = parameters.Iterations,
            DegreeOfParallelism = parameters.Parallelism,
        };

        return argon.GetBytes(outputLength);
    }
}
