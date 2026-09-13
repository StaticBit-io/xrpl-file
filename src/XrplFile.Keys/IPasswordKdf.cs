namespace XrplFile.Keys;

/// <summary>
/// Argon2id cost. Stored in the vault record so that a future recalibration still opens vaults
/// written by installed builds.
/// </summary>
/// <param name="MemoryKib">Memory in kibibytes. This is the parameter GPUs cannot cheaply ignore.</param>
/// <param name="Iterations">Passes over that memory.</param>
/// <param name="Parallelism">Lanes.</param>
public sealed record KdfParameters(int MemoryKib, int Iterations, int Parallelism)
{
    /// <summary>
    /// Measured on a desktop machine against a budget of one unlock per session. Changing these
    /// changes nothing about vaults already written — every record carries the parameters it was
    /// written with — but it does change what new vaults cost to open.
    /// </summary>
    public static KdfParameters Default { get; } = new(MemoryKib: 262144, Iterations: 3, Parallelism: 4);
}

/// <summary>
/// Password stretching. With one factor protecting the vault, this is the whole distance between
/// a stolen file and the keys inside it.
/// </summary>
public interface IPasswordKdf
{
    /// <summary>2 = Argon2id. 1 is reserved for PBKDF2-HMAC-SHA256, for compatibility if it is ever wanted.</summary>
    int KdfId { get; }

    /// <summary>
    /// Derives <paramref name="outputLength"/> bytes. <paramref name="password"/> is the UTF-8
    /// encoding of what the user typed; the caller owns the buffer and this does not clear it.
    /// </summary>
    byte[] Derive(
        ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters, int outputLength);
}
