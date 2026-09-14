namespace XrplFile.App.Core;

/// <summary>
/// Where the chosen-vault-location setting is kept: the path a person pointed the application at
/// with <see cref="VaultLocationModel.ChoosePath"/>, if any (design E5's correction). Behind an
/// interface, separate from <see cref="VaultLocation"/> itself, so a test can substitute a store
/// that never touches this machine's real application-data directory.
/// </summary>
public interface IVaultLocationStore
{
    /// <summary>
    /// The path last written with <see cref="WriteChosenPath"/>, or <see langword="null"/> when
    /// nothing has ever been chosen, or when the setting cannot be read right now. A store that
    /// cannot answer is treated the same as one that has never been written to: it is not this
    /// setting's job to turn a storage hiccup into losing the vault the application opens on
    /// every other day, and <see cref="VaultLocation.Resolve"/> already falls back to
    /// <see cref="VaultLocation.DefaultPath"/> for exactly that reason.
    /// </summary>
    string? ReadChosenPath();

    /// <summary>
    /// Records <paramref name="path"/> as the vault's location from the next start onward. Takes
    /// effect only then — see <see cref="VaultLocation.Resolve"/> and
    /// <see cref="VaultLocationModel"/> for why a running process never repoints itself.
    /// </summary>
    void WriteChosenPath(string path);

    /// <summary>
    /// Forgets whatever path <see cref="WriteChosenPath"/> last recorded. <see cref="ReadChosenPath"/>
    /// then answers <see langword="null"/> again, the same as before anything was ever chosen, and
    /// <see cref="VaultLocation.Resolve"/> falls back to <see cref="VaultLocation.DefaultPath"/> from
    /// the next start onward. A no-op when nothing was chosen. Never touches a vault file — only the
    /// setting that names one.
    /// </summary>
    void ClearChosenPath();
}
