namespace XrplFile.Keys;

/// <summary>
/// The machine-local secret behind "remember on this device": 32 random bytes that never leave
/// the machine and never reach the vault file. The vault derives a second wrapping key from them
/// (design 4.2) so that a device can open a vault without the password, while the password wrap
/// stays in place and stays the only thing that survives moving the file elsewhere.
/// </summary>
/// <remarks>
/// It is a seam rather than a class because every platform holds such a secret differently, and
/// because the unit suite must be able to run with no platform underneath it at all.
/// </remarks>
public interface IDeviceSecretStore
{
    /// <summary>
    /// The secret this machine already holds, or <see langword="null"/> when there is none.
    /// A store that cannot answer throws; absence is not a failure.
    /// </summary>
    Task<byte[]?> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates and persists a new 32-byte secret, replacing any the machine already held.
    /// </summary>
    Task<byte[]> CreateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Drops the machine's secret. Succeeds when there was none to drop: the caller is asking for
    /// an end state, not for an event.
    /// </summary>
    Task DeleteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The store for a machine that has none. It reports no secret, refuses to make one, and has
/// nothing to delete — so a vault constructed without a platform store works, with device unlock
/// simply unavailable.
/// </summary>
public sealed class NoDeviceSecretStore : IDeviceSecretStore
{
    /// <inheritdoc/>
    public Task<byte[]?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    /// <inheritdoc/>
    public Task<byte[]> CreateAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("This build has no device secret store, so device unlock cannot be enabled.");

    /// <inheritdoc/>
    public Task DeleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
