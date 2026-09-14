namespace XrplFile.Keys;

/// <summary>
/// The machine-local secrets behind "remember on this device": 32 random bytes per vault that
/// never leave the machine and never reach the vault file. The vault derives a second wrapping key
/// from its own (design 4.2) so that a device can open a vault without the password, while the
/// password wrap stays in place and stays the only thing that survives moving the file elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes the identity of the vault asking. A store holds one secret per identity, and
/// a vault must never be handed, seal under, or delete another's: two vaults on one machine — a
/// throwaway vault beside the real one is the ordinary case — would otherwise share a single
/// machine-wide secret, and turning the setting off on either would quietly take device unlock
/// away from both.
/// </para>
/// <para>
/// The identity is the vault file's full path, and a store must treat it as an opaque key: compare
/// it ordinally, and hash it if the platform needs a tamer string than a path for an item name.
/// The consequence is deliberate and small — a vault file that is moved or renamed asks for a
/// secret nobody stored, so device unlock on that machine is gone and the user turns it back on.
/// The password wrap is untouched, which is the wrap that was always the one to survive a move.
/// </para>
/// <para>
/// It is a seam rather than a class because every platform holds such a secret differently, and
/// because the unit suite must be able to run with no platform underneath it at all.
/// </para>
/// </remarks>
public interface IDeviceSecretStore
{
    /// <summary>
    /// The secret this machine already holds for <paramref name="vaultId"/>, or
    /// <see langword="null"/> when there is none. A store that cannot answer throws; absence is
    /// not a failure. Another vault's secret is never an answer to this.
    /// </summary>
    Task<byte[]?> GetAsync(string vaultId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates and persists a new 32-byte secret for <paramref name="vaultId"/>, replacing any
    /// that identity already had. No other identity's secret is touched.
    /// </summary>
    Task<byte[]> CreateAsync(string vaultId, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the secret held for <paramref name="vaultId"/>, and only that one. Succeeds when
    /// there was none to drop: the caller is asking for an end state, not for an event.
    /// </summary>
    Task DeleteAsync(string vaultId, CancellationToken cancellationToken);
}

/// <summary>
/// The store for a machine that has none. It reports no secret, refuses to make one, and has
/// nothing to delete — so a vault constructed without a platform store works, with device unlock
/// simply unavailable.
/// </summary>
public sealed class NoDeviceSecretStore : IDeviceSecretStore
{
    /// <inheritdoc/>
    public Task<byte[]?> GetAsync(string vaultId, CancellationToken cancellationToken) =>
        Task.FromResult<byte[]?>(null);

    /// <inheritdoc/>
    public Task<byte[]> CreateAsync(string vaultId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This build has no device secret store, so device unlock cannot be enabled.");

    /// <inheritdoc/>
    public Task DeleteAsync(string vaultId, CancellationToken cancellationToken) => Task.CompletedTask;
}
