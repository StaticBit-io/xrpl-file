namespace XrplFile.App.Core;

/// <summary>
/// The production <see cref="IVaultLocationStore"/>: one plain text file, beside the default
/// vault, holding the chosen path and nothing else. No MAUI API is needed to read or write a
/// small text file, so this lives in <c>XrplFile.App.Core</c> rather than behind a
/// platform-specific implementation the way <see cref="IClipboardService"/> and
/// <see cref="IFilePickerService"/> do — there is no platform difference here to abstract over.
/// </summary>
/// <remarks>
/// Every failure here is swallowed rather than thrown. A setting that cannot be read behaves as
/// "nothing chosen yet" (<see cref="VaultLocation.Resolve"/> then falls back to
/// <see cref="VaultLocation.DefaultPath"/>), and a setting that cannot be written is reported to
/// the caller of <see cref="WriteChosenPath"/> through a normal <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/> — this type does not decide what a screen does about
/// that, the same way <c>KeyVault</c> itself does not decide what a screen does about
/// <see cref="XrplFile.Keys.VaultError.StorageFailed"/>.
/// </remarks>
public sealed class FileVaultLocationStore : IVaultLocationStore
{
    private const string SettingFileName = "vault-location.txt";

    private readonly string _settingPath;

    /// <param name="settingPath">
    /// Where the setting is kept. Defaults to <see cref="SettingFileName"/> beside
    /// <see cref="VaultLocation.DefaultPath"/> — the same directory the vault itself lives in when
    /// nothing has been chosen, so this store needs no directory of its own to be created ahead of
    /// it. A test passes an explicit path under its own temporary directory instead.
    /// </param>
    public FileVaultLocationStore(string? settingPath = null) =>
        _settingPath = settingPath ?? Path.Combine(
            Path.GetDirectoryName(VaultLocation.DefaultPath)!, SettingFileName);

    /// <summary>
    /// Where the setting is actually kept. Internal, for a test to pin the default constructor's
    /// own computed path against — the same purpose <c>internal</c> members serve in
    /// <c>XrplFile.Keys</c> (<c>InternalsVisibleTo</c>) — without a public surface nobody else
    /// needs.
    /// </summary>
    internal string SettingPath => _settingPath;

    /// <inheritdoc/>
    public string? ReadChosenPath()
    {
        try
        {
            if (!File.Exists(_settingPath))
            {
                return null;
            }

            string raw = File.ReadAllText(_settingPath).Trim();
            return raw.Length == 0 ? null : Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // Path.GetFullPath's own refusals, for contents this method cannot treat as a path at
            // all: NUL bytes or other invalid characters (ArgumentException), a colon where a
            // drive letter cannot be (NotSupportedException). A setting file left half-written by
            // a power cut is the ordinary way there -- NTFS can expose the tail of an extended file
            // as zeroes -- and until this catch existed the throw travelled out of
            // VaultLocation.Resolve, out of MauiProgram.RegisterVault, and out of CreateMauiApp:
            // the application never started again, with no message and no way back, over a
            // one-line setting whose fallback was already correct.
            //
            // Treated exactly as "nothing has ever been chosen", which is what this method's own
            // contract already says a store that cannot answer must report, so
            // VaultLocation.Resolve falls back to VaultLocation.DefaultPath and the application
            // opens. The unreadable setting is left on disk rather than deleted: forgetting where
            // somebody's vault is kept is not this method's decision to make, and the settings
            // screen can still overwrite it.
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Normalises with <see cref="Path.GetFullPath(string)"/> before writing — the same
    /// normalisation <see cref="VaultLocation.DefaultPath"/> and <see cref="VaultLocation.Resolve"/>
    /// apply, and for the same reason: the vault's device secret is keyed by this exact string
    /// (<c>KeyVault.DeviceSecretId</c>), so a path written one way and read back assembled another
    /// must never look like a different vault to the platform's secret store.
    /// </remarks>
    public void WriteChosenPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(_settingPath);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_settingPath, full);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Lets a genuine <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
    /// propagate, the same as <see cref="WriteChosenPath"/> — this is the write side of the same
    /// setting, and a caller that asked to forget a path deserves to know when that did not actually
    /// happen, rather than being told "forgotten" over a setting file still holding the old choice.
    /// <see cref="File.Delete(string)"/> is already a no-op when nothing is there, which is exactly
    /// "nothing was chosen" already answering correctly with no special case here.
    /// </remarks>
    public void ClearChosenPath() => File.Delete(_settingPath);
}
