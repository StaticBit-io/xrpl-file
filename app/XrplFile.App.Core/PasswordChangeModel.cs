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
public sealed record PasswordChangeReport(bool Changed, IReadOnlyList<string> ThingsToTell, string? SurvivingCopyPath);

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

        List<string> thingsToTell = ["The master password was changed."];

        if (outcome.HasFlag(VaultPasswordChange.DeviceUnlockDropped))
        {
            thingsToTell.Add(
                "\"Remember on this device\" could not be carried over to the new password and has " +
                "been turned off. Turn it back on if you still want it.");
        }

        string? survivingCopyPath = null;
        if (outcome.HasFlag(VaultPasswordChange.TheOldCopyRemains))
        {
            survivingCopyPath = vaultPath + BackupSuffix;
            thingsToTell.Add(
                $"A copy of the vault from before this change is still at {survivingCopyPath}, and " +
                "the old password still opens it. Delete it once the new file is confirmed working, " +
                "and treat the old password as compromised until then.");
        }

        if (outcome.HasFlag(VaultPasswordChange.TheRecordsIntegrityCodeDidNotMatch))
        {
            thingsToTell.Add(
                "The vault's record did not match its own integrity code before this change. It has " +
                "been repaired as part of the change, but this was the only chance to learn that — " +
                "check your keys for anything missing or unexpected.");
        }

        return new PasswordChangeReport(Changed: true, thingsToTell, survivingCopyPath);
    }
}
