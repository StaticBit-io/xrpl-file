using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The model behind the "remember on this device" switch (design 6, "Settings"): turning device
/// unlock on and off for the vault already unlocked in this session, and reporting whether it is
/// on right now.
/// </summary>
/// <remarks>
/// There is no field here tracking "on" or "off". An earlier version kept one, set only by
/// <see cref="EnableAsync"/> and <see cref="DisableAsync"/> — the same defect <see cref="VaultListModel"/>
/// had with an <c>_acknowledged</c> flag the vault could invalidate underneath it: the wrap this
/// model turns on can go away without this model touching it — a password change that cannot
/// re-seal the wrap under the new header drops it and reports
/// <see cref="VaultPasswordChange.DeviceUnlockDropped"/> — and a cached field has no way to learn
/// that happened. <see cref="IKeyVault.HasDeviceWrapAsync"/> was added to the library precisely to
/// answer "does the record carry a wrap right now" with no cryptography and no state change, and
/// <see cref="UnlockModel.DeviceUnlockOffered"/> already asks it for the same reason in its own
/// <c>RefreshAsync</c>. This model asks the same question rather than caching an answer that can go
/// stale.
/// </remarks>
/// <param name="vault">The unlocked vault the device wrap is enabled, disabled, and read against.</param>
public sealed class DeviceUnlockModel(IKeyVault vault)
{
    /// <summary>
    /// Whether "remember on this device" is on right now — a direct read of
    /// <see cref="IKeyVault.HasDeviceWrapAsync"/>, not a field this model caches and updates by
    /// hand. See the type remarks for why: the wrap can be dropped by something other than this
    /// instance's own <see cref="DisableAsync"/>, and only asking the vault stays correct through
    /// that.
    /// </summary>
    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) =>
        vault.HasDeviceWrapAsync(cancellationToken);

    /// <summary>
    /// Turns "remember on this device" on for the data key already resident in this vault. A thin
    /// pass-through to <see cref="IKeyVault.EnableDeviceUnlockAsync"/>: no password is asked here,
    /// because the vault needs none for this.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the device store could create or return a secret.
    /// <see langword="false"/> when the store refused — <see cref="IKeyVault.EnableDeviceUnlockAsync"/>
    /// writes nothing on that path.
    /// </returns>
    /// <exception cref="VaultException">
    /// Whatever <see cref="IKeyVault.EnableDeviceUnlockAsync"/> itself throws — a locked vault, a
    /// device store that failed rather than refused — propagates unchanged.
    /// </exception>
    public Task<bool> EnableAsync(CancellationToken cancellationToken) =>
        vault.EnableDeviceUnlockAsync(cancellationToken);

    /// <summary>
    /// Turns "remember on this device" off. Safe to call when it was never on:
    /// <see cref="IKeyVault.DisableDeviceUnlockAsync"/> itself succeeds silently in that case.
    /// </summary>
    /// <exception cref="VaultException">
    /// Whatever <see cref="IKeyVault.DisableDeviceUnlockAsync"/> itself throws propagates
    /// unchanged.
    /// </exception>
    public Task DisableAsync(CancellationToken cancellationToken) =>
        vault.DisableDeviceUnlockAsync(cancellationToken);
}
