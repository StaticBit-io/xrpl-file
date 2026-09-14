namespace XrplFile.Keys;

/// <summary>
/// Argon2id cost. Stored in the vault record so that a future recalibration still opens vaults
/// written by installed builds.
/// </summary>
/// <remarks>
/// <para>
/// No instance of this type can hold a cost outside what a vault record or an export container
/// could carry (<see cref="KdfParameterBounds"/>), and that is a safety property rather than
/// tidiness. The cost travels into the file the key material is sealed under, and the file is
/// opened at the cost recorded inside it — so a vault written at a cost the reader refuses is a
/// vault that answers <see cref="VaultUnlockResult.VaultCorrupt"/> for ever, with its keys still
/// inside and no password able to reach them.
/// </para>
/// <para>
/// The properties have no setters, so <c>with</c> cannot produce an instance the constructor would
/// have refused: a second doorway to the same unopenable file. A cost read out of a file is a
/// different matter and never reaches this constructor unchecked — see
/// <see cref="KdfParameterBounds.CouldHaveBeenWritten(int, int, int)"/>, which both readers consult
/// first so that a header carrying such a cost is reported as damage rather than thrown as an
/// argument fault.
/// </para>
/// </remarks>
/// <param name="MemoryKib">Memory in kibibytes. This is the parameter GPUs cannot cheaply ignore.</param>
/// <param name="Iterations">Passes over that memory.</param>
/// <param name="Parallelism">Lanes.</param>
/// <exception cref="ArgumentOutOfRangeException">
/// The cost is outside what this format can carry, and a vault written at it could never be
/// reopened.
/// </exception>
public sealed record KdfParameters(int MemoryKib, int Iterations, int Parallelism)
{
    /// <summary>Memory in kibibytes. This is the parameter GPUs cannot cheaply ignore.</summary>
    public int MemoryKib { get; } = ValidMemory(MemoryKib, Parallelism);

    /// <summary>Passes over that memory.</summary>
    public int Iterations { get; } = ValidIterations(Iterations);

    /// <summary>Lanes.</summary>
    public int Parallelism { get; } = ValidParallelism(Parallelism);

    /// <summary>
    /// Measured on a desktop machine against a budget of one unlock per session. Changing these
    /// changes nothing about vaults already written — every record carries the parameters it was
    /// written with — but it does change what new vaults cost to open.
    /// </summary>
    public static KdfParameters Default { get; } = new(MemoryKib: 262144, Iterations: 3, Parallelism: 4);

    private static int ValidMemory(int memoryKib, int parallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryKib, KdfParameterBounds.MinimumMemoryKib);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(memoryKib, KdfParameterBounds.MaximumMemoryKib);

        // The one rule that is not a field's own range: Argon2id refuses a cost whose lanes have
        // less than four blocks each, so a vault written at one could not be opened at it either.
        if (parallelism >= KdfParameterBounds.MinimumParallelism &&
            parallelism <= KdfParameterBounds.MaximumParallelism &&
            memoryKib < KdfParameterBounds.MinimumMemoryKibPerLane * parallelism)
        {
            throw new ArgumentOutOfRangeException(
                nameof(memoryKib),
                memoryKib,
                $"Argon2id needs at least {KdfParameterBounds.MinimumMemoryKibPerLane} KiB for each of the " +
                $"{parallelism} lanes asked for, so a vault written at this cost could not be opened at it.");
        }

        return memoryKib;
    }

    private static int ValidIterations(int iterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, KdfParameterBounds.MinimumIterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, KdfParameterBounds.MaximumIterations);
        return iterations;
    }

    private static int ValidParallelism(int parallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, KdfParameterBounds.MinimumParallelism);

        // Not a format limit -- nothing in either container constrains lanes to a byte any more
        // (see KdfParameterBounds). Argon2id still needs an upper bound so a vault cannot ask for
        // an unbounded number of lanes; 255 is chosen generously, far past any cost a legitimate
        // vault would carry, rather than derived from anything that could be written.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parallelism, KdfParameterBounds.MaximumParallelism);
        return parallelism;
    }
}

/// <summary>
/// Password stretching. With one factor protecting the vault, this is the whole distance between
/// a stolen file and the keys inside it.
/// </summary>
public interface IPasswordKdf
{
    /// <summary>
    /// 2 = Argon2id. 1 is reserved for PBKDF2-HMAC-SHA256, for compatibility if it is ever wanted.
    /// There is no passwordless <c>kdfId</c> 0: every container this library writes, vault record or
    /// export container alike, has its own password (D1, D10). A container with no password would
    /// be a seed sitting in plaintext, which is the opposite of D1's whole point — fewer things that
    /// open everything, not a second one that opens itself.
    /// </summary>
    int KdfId { get; }

    /// <summary>
    /// Derives <paramref name="outputLength"/> bytes. <paramref name="password"/> is the UTF-8
    /// encoding of what the user typed; the caller owns the buffer and this does not clear it.
    /// </summary>
    byte[] Derive(
        ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters, int outputLength);
}
