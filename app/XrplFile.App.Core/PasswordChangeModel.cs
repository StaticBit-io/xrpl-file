using XrplFile.App.Core.Localization;
using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// What a master-password change is reported as (design 6, "Changing the password"): whether the
/// password actually changed, and, for every flag <see cref="VaultPasswordChange"/> can set, a
/// sentence naming it. Each of those flags exists because silence about it misled somebody during
/// that flag's own review, and a screen that says only "done" puts the silence back.
/// </summary>
/// <param name="Changed">
/// Whether the new password reached disk. <see langword="false"/> only for
/// <see cref="VaultPasswordChange.WrongPassword"/>, the flag set's empty value: nothing was
/// written, and neither <see cref="ThingsToTell"/> nor <see cref="SurvivingCopyPath"/> has
/// anything to say.
/// </param>
/// <param name="ThingsToTell">
/// One sentence per flag that was set beyond <see cref="VaultPasswordChange.Changed"/> itself,
/// plus one for the change — in a fixed order: the change, device unlock dropped, the old copy
/// surviving, the record's integrity code. Empty when <see cref="Changed"/> is
/// <see langword="false"/>.
/// </param>
/// <param name="SurvivingCopyPath">
/// The path of the file the old password still opens, set only when
/// <see cref="VaultPasswordChange.TheOldCopyRemains"/> was reported; otherwise
/// <see langword="null"/>.
/// </param>
/// <param name="Refusal">
/// Why the change was not attempted at all, or <see langword="null"/> when it was. Set only by
/// <see cref="PasswordChangeModel.ChangeAsync"/>, and only for a new password shorter than
/// <see cref="PasswordPolicy.MinimumLength"/>: the vault was never called, so this is not the
/// wrong-password outcome, which <see cref="Changed"/> being <see langword="false"/> otherwise
/// means. A screen has to tell the two apart — "the password you typed is wrong" and "the password
/// you chose is too short" send a person to opposite fields — and a component that decided which
/// of them a bare <see langword="false"/> meant would be deciding exactly what this model exists
/// to decide. Trailing and optional so every existing construction site keeps compiling.
/// </param>
public sealed record PasswordChangeReport(
    bool Changed, IReadOnlyList<UiText> ThingsToTell, string? SurvivingCopyPath, UiText? Refusal = null);

/// <summary>
/// The model behind changing the vault's master password (design 6). Every flag
/// <see cref="VaultPasswordChange"/> can set reaches the screen as its own sentence, through
/// <see cref="Describe"/>.
/// </summary>
/// <remarks>
/// <see cref="Describe"/> is <see langword="static"/> and does the actual work; <see cref="ChangeAsync"/>
/// is a thin caller of it. The mapping from flags to words is the part worth pinning under test,
/// and a test that needs a vault to check a sentence is a slower test that proves less — so the
/// mapping is kept reachable with no <see cref="IKeyVault"/> in the loop at all.
/// </remarks>
/// <param name="vault">The vault whose password is changed.</param>
/// <param name="vaultPath">
/// The vault's own file path, used only to build <see cref="PasswordChangeReport.SurvivingCopyPath"/>.
/// <see cref="IKeyVault"/> reports only that a copy remains, not where — the durable write's
/// backup is always the vault's own path with <c>.bak</c> appended (see <c>DurableFile</c> and
/// <see cref="IKeyVault.ChangePasswordAsync"/>'s remarks), so this model reconstructs the path
/// rather than asking the vault for one it has no reason to expose.
/// </param>
public sealed class PasswordChangeModel(IKeyVault vault, string vaultPath)
{
    private const string BackupSuffix = ".bak";

    /// <summary>
    /// Changes the master password and reports every flag <see cref="IKeyVault.ChangePasswordAsync"/>
    /// sets, through <see cref="Describe"/>.
    /// </summary>
    /// <remarks>
    /// Whatever <see cref="IKeyVault.ChangePasswordAsync"/> itself throws for — a record that
    /// cannot be read at all, a refused disk, a device store that cannot answer — propagates
    /// unchanged. Those are genuine failures with nothing written or nothing known, reported
    /// honestly as failures rather than folded into a report that would claim a change, or the
    /// absence of one, it cannot back up.
    /// </remarks>
    public async Task<PasswordChangeReport> ChangeAsync(
        ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken)
    {
        // The new password is chosen here, so PasswordPolicy applies to it; the old one is merely
        // entered, so it does not -- a vault created before this rule existed must still open, and
        // measuring the current password against the rule would lock its owner out of their own
        // keys to enforce it retroactively. See PasswordPolicy's own remarks.
        if (!PasswordPolicy.IsLongEnough(newPassword.Span))
        {
            return new PasswordChangeReport(
                Changed: false,
                ThingsToTell: [],
                SurvivingCopyPath: null,
                Refusal: UiText.Of(VaultText.ChangeTooShort, ("minimum", PasswordPolicy.MinimumLength)));
        }

        VaultPasswordChange outcome = await vault
            .ChangePasswordAsync(oldPassword, newPassword, cancellationToken)
            .ConfigureAwait(false);
        return Describe(outcome, vaultPath);
    }

    /// <summary>
    /// Maps a <see cref="VaultPasswordChange"/> flag set to the sentences a screen shows. Reachable
    /// with no vault at all, which is what makes the mapping itself testable rather than only the
    /// wiring around it — see the type remarks.
    /// </summary>
    /// <param name="outcome">What <see cref="IKeyVault.ChangePasswordAsync"/> returned.</param>
    /// <param name="vaultPath">
    /// The vault's own path, used only to build <see cref="PasswordChangeReport.SurvivingCopyPath"/>
    /// when <see cref="VaultPasswordChange.TheOldCopyRemains"/> is set.
    /// </param>
    public static PasswordChangeReport Describe(VaultPasswordChange outcome, string vaultPath)
    {
        if (outcome == VaultPasswordChange.WrongPassword)
        {
            // The empty flag set: nothing was written, so there is nothing to tell and nowhere a
            // surviving copy could be. A caller must not read this as a change that happened
            // quietly — see VaultPasswordChange's own remarks on why this is the default.
            return new PasswordChangeReport(Changed: false, ThingsToTell: [], SurvivingCopyPath: null);
        }

        List<UiText> thingsToTell = [VaultText.ChangeDone];

        if (outcome.HasFlag(VaultPasswordChange.DeviceUnlockDropped))
        {
            thingsToTell.Add(VaultText.ChangeDeviceUnlockDropped);
        }

        string? survivingCopyPath = null;
        if (outcome.HasFlag(VaultPasswordChange.TheOldCopyRemains))
        {
            survivingCopyPath = vaultPath + BackupSuffix;
            thingsToTell.Add(UiText.Of(VaultText.ChangeOldCopyRemains, ("path", survivingCopyPath)));
        }

        if (outcome.HasFlag(VaultPasswordChange.TheRecordsIntegrityCodeDidNotMatch))
        {
            thingsToTell.Add(VaultText.ChangeIntegrityRepaired);
        }

        return new PasswordChangeReport(Changed: true, thingsToTell, survivingCopyPath);
    }
}
