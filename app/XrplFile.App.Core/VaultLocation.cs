namespace XrplFile.App.Core;

/// <summary>
/// Where the vault lives when nobody has pointed the application at a different file (design
/// E5). The file must be findable for a backup to mean anything, so the path is derived once,
/// from the platform's own application-data directory, rather than assembled ad hoc wherever a
/// vault is opened.
/// </summary>
public static class VaultLocation
{
    private const string ApplicationDirectoryName = "XrplFile";

    /// <summary>The vault file's name, wherever it is opened from.</summary>
    public static string FileName => "vault.json";

    /// <summary>
    /// The full, normalised path of the default vault file, under the current user's application
    /// data directory. Normalised because the vault keys its device secret by this exact string
    /// (design 4.2): a path assembled one way and the same path assembled another must not look
    /// like two different vaults to the platform's secret store.
    /// </summary>
    public static string DefaultPath { get; } = Path.GetFullPath(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ApplicationDirectoryName,
            FileName));
}
