namespace XrplFile.Keys;

/// <summary>
/// An <see cref="IKeyVault"/> that resets <paramref name="autoLock"/>'s idle window every time
/// something asks the vault to do work, and otherwise changes nothing about the vault it wraps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="VaultAutoLock"/> only shuts a vault that nothing has touched
/// for its whole window, and the one caller that ever touched it was <see cref="VaultSigner"/> —
/// which belongs to a later part and which the desktop application never constructs. Without
/// something else touching it, a person halfway through typing a label at minute fourteen has the
/// vault shut under them at minute fifteen. "Actually using it" has to be observed somewhere, and
/// this is that somewhere.
/// </para>
/// <para>
/// <b>Why here and not in a screen.</b> A component observing clicks would count a person staring
/// at a screen as activity and a person working through a file dialog as idleness, and it would
/// have to be repeated in every component that could ever be the one on screen. Every model in
/// this application reaches the vault and nothing else: "the person asked the application to do
/// something" and "something called an operation on the vault" are the same event, observed once,
/// in the one place every path already passes through.
/// </para>
/// <para>
/// <b>What deliberately does not count as use.</b> Reading a property —
/// <see cref="IsUnlocked"/>, <see cref="WritesAreRefused"/>, <see cref="References"/>,
/// <see cref="DamagedReferences"/> — and the two questions a screen asks about the file rather
/// than about its contents, <see cref="ExistsAsync"/> and <see cref="HasDeviceWrapAsync"/>. Those
/// are what a screen polls: <c>RevealSeed.razor</c> reads <c>RevealModel.Shown</c> — and so
/// <see cref="IsUnlocked"/> — once a second, and <c>Shell.razor</c> polls
/// <c>UnlockModel.NoticeLock</c> for the same reason. If a poll counted as use, a machine left
/// alone on either screen would hold its data key open forever, which is the precise opposite of
/// what the idle lock is for. <see cref="LockAsync"/> does not count either, for the obvious
/// reason: the auto-lock's own callback goes through the vault this instance wraps, but a caller
/// locking deliberately is ending a session, not continuing one.
/// </para>
/// <para>
/// <b>The window is reset before the call, not after it.</b> The person's action is what starts
/// the operation, and that is the moment of use; an operation that then takes time is covered
/// because the window is measured from its start. The longest operation here is an Argon2id
/// derivation at roughly half a second (design 5.2) against a window measured in minutes, so
/// which end it is measured from cannot matter in practice — it is stated so it is not mistaken
/// for an oversight.
/// </para>
/// <para>
/// <b>It wraps, it never replaces.</b> <see cref="VaultAutoLock"/> is constructed over the
/// undecorated vault, so its own <see cref="IKeyVault.LockAsync"/> never travels back through this
/// class; and every member below forwards unchanged, so nothing about the vault's own contract —
/// its gate, its errors, its cancellation — is altered by being wrapped.
/// </para>
/// </remarks>
/// <param name="inner">The vault every member forwards to, unchanged.</param>
/// <param name="autoLock">The idle window each operation below resets.</param>
public sealed class TouchingVault(IKeyVault inner, VaultAutoLock autoLock) : IKeyVault
{
    private readonly IKeyVault _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly VaultAutoLock _autoLock = autoLock ?? throw new ArgumentNullException(nameof(autoLock));

    /// <inheritdoc/>
    /// <remarks>A read, not use. See the type's remarks on why a poll must not hold the vault open.</remarks>
    public bool IsUnlocked => _inner.IsUnlocked;

    /// <inheritdoc/>
    /// <remarks>A read, not use.</remarks>
    public IReadOnlyCollection<string> References => _inner.References;

    /// <inheritdoc/>
    /// <remarks>A read, not use.</remarks>
    public IReadOnlyList<string> DamagedReferences => _inner.DamagedReferences;

    /// <inheritdoc/>
    /// <remarks>A read, not use.</remarks>
    public bool WritesAreRefused => _inner.WritesAreRefused;

    /// <inheritdoc/>
    /// <remarks>A question about the file, not about its contents. See the type's remarks.</remarks>
    public Task<bool> ExistsAsync(CancellationToken cancellationToken) => _inner.ExistsAsync(cancellationToken);

    /// <inheritdoc/>
    /// <remarks>A question about the file, not about its contents. See the type's remarks.</remarks>
    public Task<bool> HasDeviceWrapAsync(CancellationToken cancellationToken) =>
        _inner.HasDeviceWrapAsync(cancellationToken);

    /// <inheritdoc/>
    /// <remarks>Ending a session is not continuing one. See the type's remarks.</remarks>
    public Task LockAsync() => _inner.LockAsync();

    /// <inheritdoc/>
    public Task AcknowledgeDamageAsync(CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.AcknowledgeDamageAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.CreateAsync(password, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.UnlockAsync(password, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.UnlockWithDeviceAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.ListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.StoreAsync(entry, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.RevealAsync(reference, reason, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<byte[]> ExportAsync(
        string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.ExportAsync(reference, containerPassword, cancellationToken);
    }

    /// <inheritdoc/>
    public Task UpdateAsync(string reference, VaultEntryEdit edit, CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.UpdateAsync(reference, edit, cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.DeleteAsync(reference, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<VaultPasswordChange> ChangePasswordAsync(
        ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.ChangePasswordAsync(oldPassword, newPassword, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.EnableDeviceUnlockAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task DisableDeviceUnlockAsync(CancellationToken cancellationToken)
    {
        _autoLock.Touch();
        return _inner.DisableDeviceUnlockAsync(cancellationToken);
    }
}
