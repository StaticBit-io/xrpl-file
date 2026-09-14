namespace XrplFile.App.Core;

/// <summary>
/// The model behind "A different vault" (design 6, "Settings"): letting a person point the
/// application at a vault file kept somewhere else, and reporting where this process is opening
/// its vault from right now.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the owner reversed the earlier "not available in this build" ruling.</b> The vault trades
/// two authentication factors for one — a master password alone, no device key — deliberately, so
/// that the vault file is portable and a backup is a copy of it (design E5). Opening a vault from
/// somewhere else is the only mechanism by which that trade is usable: without it, a vault carried
/// to a new machine cannot be opened by this application at all, and the file that was supposed to
/// travel does not.
/// </para>
/// <para>
/// <b>Why this only takes effect on the next start.</b> <c>IKeyVault</c> is wired as a process-wide
/// singleton over one path, for the whole process (<c>MauiProgram.RegisterVault</c>, design 3.1) —
/// see that method's own remarks on why singleton is the only lifetime under which "the vault is
/// unlocked" means the same thing to every model that asks. Repointing it while the process runs
/// would mean either constructing a second, independent <c>KeyVault</c> instance that every
/// existing model keeps ignoring, or tearing down and rebuilding the one every model was
/// constructed with — neither of which this plan's models can do to each other. So this model does
/// neither: it records the choice through <see cref="IVaultLocationStore"/> and lets
/// <see cref="VaultLocation.Resolve"/>, read once at the next start, be the only place the running
/// vault's path ever changes.
/// </para>
/// <para>
/// <b>Why nothing here checks that the chosen file is actually a vault.</b> The application already
/// answers that question the moment it tries to open one: <c>IKeyVault.UnlockAsync</c>'s own
/// outcomes (design section 7) already tell a file that will not parse as a vault record
/// (<c>VaultUnlockResult.VaultCorrupt</c>) apart from a wrong password
/// (<c>VaultUnlockResult.WrongPassword</c>) — that is the whole reason
/// <see cref="XrplFile.Keys.VaultUnlockResult"/> has seven values instead of a bare
/// <see langword="bool"/>. A second, ad hoc check in this model, run against a file this process
/// has not restarted over yet, would either duplicate that judgment or risk contradicting it — a
/// second taxonomy for the same question. This model reads none of the chosen file's bytes; it only
/// remembers where it is.
/// </para>
/// <para>
/// <b>Why this model does not call the picker itself.</b> <c>AddKeyModel</c> sets the precedent:
/// asking the OS for a file is a plain OS action, the same way <c>Settings.razor</c>'s own
/// <c>RevealInFileManager</c> calls <c>Process.Start</c> directly rather than through a seam. Its
/// component (<c>AddKey.razor</c>) calls <c>IFilePickerService.PickFileAsync</c> itself and hands
/// the model only what came back; this model follows the same split — <see cref="ChoosePath"/>
/// takes the path the component already has, and the one decision that belongs here is what to do
/// with it.
/// </para>
/// </remarks>
/// <param name="store">Where the choice is recorded for the next start.</param>
/// <param name="currentPath">
/// The path this process is actually opening the vault from right now — <see cref="VaultLocation.Resolve"/>'s
/// result at startup, handed in rather than recomputed here so that this model reports the same
/// path <c>MauiProgram.RegisterVault</c> actually built the vault singleton over, even if the
/// setting were somehow rewritten by something else in the meantime.
/// </param>
public sealed class VaultLocationModel(IVaultLocationStore store, string currentPath)
{
    /// <summary>Where this process is opening the vault from right now.</summary>
    public string CurrentPath { get; } = currentPath;

    /// <summary>
    /// Whether the vault open right now is the one at <see cref="VaultLocation.DefaultPath"/>, as
    /// opposed to one chosen with <see cref="ChoosePath"/>. See <see cref="VaultLocation.IsDefaultPath"/>
    /// for the comparison itself — case-insensitive, because this is a Windows path, while the
    /// string every path here is actually keyed by (a device secret, a stored setting) stays exactly
    /// as <see cref="Path.GetFullPath(string)"/> produced it, case included.
    /// </summary>
    public bool IsDefault { get; } = VaultLocation.IsDefaultPath(currentPath);

    /// <summary>
    /// Records <paramref name="path"/> as the vault file to open from the next start onward.
    /// Nothing about the vault this process already has open changes: see the type remarks for why
    /// a running process never repoints itself.
    /// </summary>
    public void ChoosePath(string path) => store.WriteChosenPath(path);

    /// <summary>
    /// Forgets whatever path <see cref="ChoosePath"/> last recorded, so <see cref="VaultLocation.Resolve"/>
    /// falls back to <see cref="VaultLocation.DefaultPath"/> from the next start onward — the
    /// deliberate "start fresh" a person can reach for when the vault they last chose is not
    /// answering (<c>VaultUnreachable.razor</c>).
    /// </summary>
    /// <remarks>
    /// Never touches the vault file itself, at the remembered path or anywhere else — forgetting
    /// where this application last looked is not a claim that the file stopped existing there, only
    /// that this application will stop looking for it on its own. Nothing about the vault this
    /// process already has open changes either, for the same reason <see cref="ChoosePath"/> does
    /// not: a running process never repoints itself.
    /// </remarks>
    public void ForgetChosenPath() => store.ClearChosenPath();
}
