using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// A minimal <see cref="IKeyVault"/> whose every member throws <see cref="NotSupportedException"/>.
/// Safe wherever a test builds a <see cref="PublicationRun"/> but never actually starts or resumes
/// one — nothing reaches the vault unless <see cref="PublicationRun.Start"/> or
/// <see cref="PublicationRun.Resume"/> is called. Shared rather than duplicated per test class:
/// originally a private nested type of <c>RunModelTests</c>, promoted here once a second test class
/// (<c>UnfinishedModelTests</c>) needed the exact same double.
/// </summary>
public sealed class NeverVault : IKeyVault
{
    public bool IsUnlocked => throw new NotSupportedException();

    public IReadOnlyCollection<string> References => throw new NotSupportedException();

    public IReadOnlyList<string> DamagedReferences => throw new NotSupportedException();

    public bool WritesAreRefused => throw new NotSupportedException();

    public Task AcknowledgeDamageAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> ExistsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> HasDeviceWrapAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task LockAsync() => throw new NotSupportedException();

    public Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<byte[]> ExportAsync(
        string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task UpdateAsync(string reference, VaultEntryEdit edit, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string reference, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<VaultPasswordChange> ChangePasswordAsync(
        ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DisableDeviceUnlockAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}
