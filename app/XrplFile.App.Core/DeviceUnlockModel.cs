using XrplFile.App.Core.Localization;
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
    /// What went wrong with the most recent <see cref="EnableAsync"/> or <see cref="DisableAsync"/>
    /// call, in words a screen shows as-is — or <see langword="null"/> after one that worked.
    /// </summary>
    /// <remarks>
    /// Both failures a screen can see arrive here as sentences, and neither is composed in markup.
    /// <see cref="IKeyVault.EnableDeviceUnlockAsync"/> reports a device store that <i>refused</i> as
    /// a bare <see langword="false"/> and one that <i>failed</i> as a <see cref="VaultException"/>,
    /// and the settings screen used to compose words for the first itself while routing the second
    /// through <see cref="VaultErrorMessages"/> two lines above — the same decision made in two
    /// places, one of them markup. A <see langword="bool"/> is not a sentence, and turning it into
    /// one is this model's job.
    /// </remarks>
    public UiText? Problem { get; private set; }

    /// <summary>
    /// Turns "remember on this device" on for the data key already resident in this vault.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the device store could create or return a secret.
    /// <see langword="false"/> when it refused or failed, with <see cref="Problem"/> set to what
    /// the screen should say — <see cref="IKeyVault.EnableDeviceUnlockAsync"/> writes nothing on
    /// either path.
    /// </returns>
    public async Task<bool> EnableAsync(CancellationToken cancellationToken)
    {
        bool created;
        try
        {
            created = await vault.EnableDeviceUnlockAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (VaultException exception)
        {
            Problem = VaultErrorMessages.For(exception.Error);
            return false;
        }

        Problem = created
            ? null
            : UiText.Of(VaultText.DeviceUnlockNotCreated);

        return created;
    }

    /// <summary>
    /// Turns "remember on this device" off. Safe to call when it was never on:
    /// <see cref="IKeyVault.DisableDeviceUnlockAsync"/> itself succeeds silently in that case.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when it is off afterwards; <see langword="false"/> with
    /// <see cref="Problem"/> set when the vault refused.
    /// </returns>
    public async Task<bool> DisableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await vault.DisableDeviceUnlockAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (VaultException exception)
        {
            Problem = VaultErrorMessages.For(exception.Error);
            return false;
        }

        Problem = null;
        return true;
    }
}
