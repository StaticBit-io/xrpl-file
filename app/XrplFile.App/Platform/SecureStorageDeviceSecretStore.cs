using System.Security.Cryptography;
using System.Text;

using XrplFile.Keys;

namespace XrplFile.App.Platform;

/// <summary>
/// The platform's secret store, holding one 32-byte secret per vault, over MAUI
/// <see cref="SecureStorage"/> — DPAPI under the hood on Windows, the Keychain or the Android
/// keystore on other platforms.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by the vault file's full path, which is what <see cref="IKeyVault"/> passes as
/// <c>vaultId</c> (see that interface's remarks, design section 4.2). A renamed or moved vault
/// therefore looks like a different vault and asks for its password once — the accepted cost of
/// not putting an identifier inside the record.
/// </para>
/// <para>
/// The path itself is not used as the storage item's name: <see cref="IDeviceSecretStore"/>'s own
/// remarks say a store must "hash it if the platform needs a tamer string than a path for an item
/// name", and a full path carries characters (<c>:</c>, <c>\</c>) that are not safe as an arbitrary
/// item identifier on every backing store <see cref="SecureStorage"/> can sit on. <see cref="ItemNameFor"/>
/// hashes it instead, ordinally and case-sensitively — the same comparison the interface requires
/// of a store that compares the identity at all.
/// </para>
/// <para>
/// <b>Failures are not translated.</b> <see cref="SecureStorage"/> throws its own platform
/// exceptions (for example <see cref="System.Security.Cryptography.CryptographicException"/> when
/// Windows DPAPI cannot unprotect a stored value), and this type lets every one of them propagate
/// unchanged rather than wrapping or swallowing them. <see cref="KeyVault"/>'s own callers already
/// catch broadly around every call into an <see cref="IDeviceSecretStore"/>
/// (<c>catch (Exception ex) when (ex is not OperationCanceledException)</c>) and turn the failure
/// into <see cref="VaultUnlockResult.VaultUnavailable"/> or a <see cref="VaultException"/> with
/// <see cref="VaultError.StorageFailed"/> — the exact same shape
/// <c>VaultHarness.InMemoryDeviceSecretStore</c> exercises in the test suite, since that double
/// never wraps anything either. Letting this store do the same is what makes the vault's handling
/// of a failing store exercised by both the in-memory double and the real platform store, rather
/// than by two different contracts that happen to look alike.
/// </para>
/// </remarks>
public sealed class SecureStorageDeviceSecretStore : IDeviceSecretStore
{
    private const int SecretLength = 32;
    private const string ItemNamePrefix = "xrplfile.device-secret.";

    /// <inheritdoc/>
    public async Task<byte[]?> GetAsync(string vaultId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultId);
        cancellationToken.ThrowIfCancellationRequested();

        string? stored = await SecureStorage.Default.GetAsync(ItemNameFor(vaultId)).ConfigureAwait(false);
        return stored is null ? null : Convert.FromBase64String(stored);
    }

    /// <inheritdoc/>
    public async Task<byte[]> CreateAsync(string vaultId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultId);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] secret = RandomNumberGenerator.GetBytes(SecretLength);
        await SecureStorage.Default.SetAsync(ItemNameFor(vaultId), Convert.ToBase64String(secret)).ConfigureAwait(false);
        return secret;
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string vaultId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultId);
        cancellationToken.ThrowIfCancellationRequested();

        SecureStorage.Default.Remove(ItemNameFor(vaultId));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Turns an opaque vault identity into a storage item name <see cref="SecureStorage"/> is
    /// guaranteed to accept, without ever storing or logging the path it was hashed from.
    /// </summary>
    private static string ItemNameFor(string vaultId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(vaultId));
        return ItemNamePrefix + Convert.ToHexStringLower(hash);
    }
}
