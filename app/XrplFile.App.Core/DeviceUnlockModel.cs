using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The model behind the "remember on this device" switch (design 6, "Settings"): turning device
/// unlock on and off for the vault already unlocked in this session.
/// </summary>
/// <remarks>
/// <see cref="Enabled"/> reflects only the outcome of <see cref="EnableAsync"/> and
/// <see cref="DisableAsync"/> calls made through this instance — it is not a re-read of the
/// vault's record. <see cref="IKeyVault.HasDeviceWrapAsync"/> already answers "does the record
/// carry a wrap right now" for the locked screen (see <c>UnlockModel.DeviceUnlockOffered</c>), and
/// a caller that needs that true state to seed the switch before the first press — the settings
/// screen, opening on a vault that may already have device unlock on from an earlier session — is
/// expected to ask <see cref="IKeyVault.HasDeviceWrapAsync"/> directly rather than have this model
/// ask the same question a second time for no different an answer.
/// </remarks>
/// <param name="vault">The unlocked vault the device wrap is enabled or disabled against.</param>
public sealed class DeviceUnlockModel(IKeyVault vault)
{
    /// <summary>
    /// Whether this instance has turned "remember on this device" on. Starts
    /// <see langword="false"/>; becomes <see langword="true"/> only after a successful
    /// <see cref="EnableAsync"/>, and <see langword="false"/> again after <see cref="DisableAsync"/>
    /// — see the type remarks for why this does not read the vault's own record.
    /// </summary>
    public bool Enabled { get; private set; }

    /// <summary>
    /// Turns "remember on this device" on for the data key already resident in this vault. A thin
    /// pass-through to <see cref="IKeyVault.EnableDeviceUnlockAsync"/>: no password is asked here,
    /// because the vault needs none for this.
    /// </summary>
    /// <returns>
    /// <see langword="true"/>, with <see cref="Enabled"/> set, when the device store could create
    /// or return a secret. <see langword="false"/>, with <see cref="Enabled"/> left exactly as it
    /// was, when the store refused — <see cref="IKeyVault.EnableDeviceUnlockAsync"/> writes nothing
    /// on that path, so there is nothing for <see cref="Enabled"/> to reflect either.
    /// </returns>
    /// <exception cref="VaultException">
    /// Whatever <see cref="IKeyVault.EnableDeviceUnlockAsync"/> itself throws — a locked vault, a
    /// device store that failed rather than refused — propagates unchanged.
    /// </exception>
    public async Task<bool> EnableAsync(CancellationToken cancellationToken)
    {
        bool enabled = await vault.EnableDeviceUnlockAsync(cancellationToken).ConfigureAwait(false);
        if (enabled)
        {
            Enabled = true;
        }

        return enabled;
    }

    /// <summary>
    /// Turns "remember on this device" off. Safe to call when it was never on:
    /// <see cref="IKeyVault.DisableDeviceUnlockAsync"/> itself succeeds silently in that case, and
    /// <see cref="Enabled"/> is simply set to <see langword="false"/>, which it already was.
    /// </summary>
    /// <exception cref="VaultException">
    /// Whatever <see cref="IKeyVault.DisableDeviceUnlockAsync"/> itself throws propagates
    /// unchanged.
    /// </exception>
    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        await vault.DisableDeviceUnlockAsync(cancellationToken).ConfigureAwait(false);
        Enabled = false;
    }
}
