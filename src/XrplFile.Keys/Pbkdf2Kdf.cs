using System.Security.Cryptography;

namespace XrplFile.Keys;

/// <summary>
/// Password stretching for the export container, which is the Wallet project's format and fixes
/// PBKDF2-HMAC-SHA256 as <c>kdfId</c> 1.
/// </summary>
/// <remarks>
/// Deliberately not an <see cref="IPasswordKdf"/>. That interface carries a
/// <see cref="KdfParameters"/> whose memory and parallelism PBKDF2 has no use for and which now
/// validates them, so an implementation here would have to invent numbers that pass a validator.
/// The container's <c>kdfId</c> already chooses the algorithm; there is nothing to dispatch on.
///
/// The output must stay byte-identical to the Wallet's, or a container written by one product does
/// not open in the other. The published vector is what holds that.
/// </remarks>
internal static class Pbkdf2Kdf
{
    internal const int KdfId = 1;

    /// <summary>The lowest count the Wallet accepts in a container header.</summary>
    internal const int IterationsFloor = 600_000;

    /// <summary>The highest, so a container written on a fast machine cannot hang a slow import.</summary>
    internal const int IterationsCeiling = 1_000_000;

    /// <summary>
    /// Measured on a desktop machine against the slowest import a person will accept: 122-126 ms at
    /// this count, with every point from 600,000 upward far under budget. The ceiling bounds this
    /// value, not the clock.
    /// </summary>
    internal static int DefaultIterations => IterationsCeiling;

    internal static byte[] Derive(
        ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int outputLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputLength, 4);

        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, outputLength);
    }
}
