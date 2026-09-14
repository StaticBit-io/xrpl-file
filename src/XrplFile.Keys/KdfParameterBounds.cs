namespace XrplFile.Keys;

/// <summary>
/// What counts as a cost a file could legitimately be carrying. Both the vault record (5) and the
/// export container (8) write <see cref="KdfParameters"/> into a header an attacker can edit, and
/// both hand what they read there to Argon2id.
/// </summary>
/// <remarks>
/// <para>
/// The header is the associated data of the key wraps, so a tampered cost is caught — but only
/// <em>after</em> the derivation it describes has been paid for, which is too late for a cost
/// chosen to be unpayable. Below the floor, Argon2id answers with a raw
/// <see cref="ArgumentOutOfRangeException"/> out of methods whose failures are otherwise all
/// values a caller can switch on; above the ceiling it tries to allocate the machine out of
/// existence. Neither is a cost any build ever wrote, so both are damage, and both are refused
/// here before a single byte is derived.
/// </para>
/// <para>
/// The bounds are deliberately far wider than anything this software chooses, because the failure
/// they must never produce is refusing a real vault: that costs a user their keys, while an
/// over-wide bound costs only the time of a derivation that then fails its tag anyway. The floor
/// of each field is what Argon2id itself accepts (<see cref="Argon2idKdf.Derive"/>), which is also
/// what the unit suite writes. The memory ceiling is sixteen times the measured production figure
/// of 256 MiB (4.3) and still an allocation a real machine could attempt, unlike the two terabytes
/// a four-byte field can otherwise ask for. The iteration ceiling is three orders of magnitude
/// above the measured three passes. The parallelism ceiling is the format's own: the export
/// container carries lanes in a single byte (8), so a vault written above it could never be
/// exported from, and export is the only way back from a forgotten password (D10).
/// </para>
/// </remarks>
internal static class KdfParameterBounds
{
    /// <summary>Argon2id's own floor, and the cost the unit suite writes.</summary>
    internal const int MinimumMemoryKib = 8;

    /// <summary>4 GiB: sixteen times the measured production cost, and still an allocation a machine could attempt.</summary>
    internal const int MaximumMemoryKib = 4_194_304;

    internal const int MinimumIterations = 1;

    /// <summary>1024 passes: a thousand times the measured three, and still a finite wait.</summary>
    internal const int MaximumIterations = 1024;

    internal const int MinimumParallelism = 1;

    /// <summary>What the export container's one-byte lane field can carry.</summary>
    internal const int MaximumParallelism = byte.MaxValue;

    /// <summary>
    /// How much memory each lane must have to itself. It is the implementation's own rule — four
    /// blocks per lane, below which <see cref="Argon2idKdf.Derive"/> throws
    /// <see cref="InvalidOperationException"/> — rather than RFC 9106's stricter eight, precisely
    /// because the question here is what could have been <em>written</em>: a cost between the two
    /// derives today, so a vault could be carrying it, and refusing one would cost a user the
    /// file. It is pinned by <c>Argon2idKdfTests</c> against the library itself.
    /// </summary>
    internal const int MinimumMemoryKibPerLane = 4;

    /// <summary>
    /// Whether these parameters are ones a file this software wrote could be carrying. False means
    /// the header was damaged or edited, never that the cost is merely one this build disagrees
    /// with — nothing in that range is reachable by writing a vault or a container.
    /// </summary>
    internal static bool CouldHaveBeenWritten(KdfParameters parameters) =>
        parameters.MemoryKib is >= MinimumMemoryKib and <= MaximumMemoryKib &&
        parameters.Iterations is >= MinimumIterations and <= MaximumIterations &&
        parameters.Parallelism is >= MinimumParallelism and <= MaximumParallelism &&
        parameters.MemoryKib >= MinimumMemoryKibPerLane * parameters.Parallelism;
}
