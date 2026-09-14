namespace XrplFile.App.Core;

/// <summary>
/// Where the vault lives: <see cref="DefaultPath"/> until somebody points the application at a
/// different file, and <see cref="Resolve"/> from then on (design E5). The file must be findable
/// for a backup to mean anything, so the default path is derived once, from the platform's own
/// application-data directory, rather than assembled ad hoc wherever a vault is opened.
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

    /// <summary>
    /// The vault a process should actually open (design E5's correction): whatever
    /// <paramref name="store"/> last had chosen with <see cref="VaultLocationModel.ChooseAsync"/>,
    /// normalised the same way <see cref="DefaultPath"/> is, or <see cref="DefaultPath"/> itself
    /// when nothing has ever been chosen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method taking the store as a parameter, not an eagerly-computed static property the way
    /// <see cref="DefaultPath"/> is. <see cref="DefaultPath"/> touches nothing but
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> and is safe to evaluate
    /// the moment this type is first touched, including from a test that only ever asks about it.
    /// This method reads a file through <paramref name="store"/>, and doing that as a side effect
    /// of touching an unrelated static member of this class — which is what an eager property
    /// initializer here would do, since a type's static field initializers all run together — would
    /// make every test in this project quietly depend on this machine's real application-data
    /// directory. Called explicitly instead, exactly once, from <c>MauiProgram.RegisterVault</c>.
    /// </para>
    /// <para>
    /// Resolved once, at that one call, deliberately — not re-read on every access. A person who
    /// points the application at a different vault is told a restart is needed
    /// (<see cref="VaultLocationModel"/>) precisely so this value never has to change under a
    /// <c>KeyVault</c> already constructed over the old path: <c>IKeyVault</c> is a process-wide
    /// singleton (<c>MauiProgram.RegisterVault</c>, design 3.1), and every model in the process
    /// shares that one instance. Repointing this value mid-process would leave the singleton, and
    /// every model built over it, still bound to the file that used to be here.
    /// </para>
    /// </remarks>
    public static string Resolve(IVaultLocationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        string? chosen = store.ReadChosenPath();
        return chosen is null ? DefaultPath : Path.GetFullPath(chosen);
    }

    /// <summary>
    /// Whether <paramref name="path"/>, once normalised the same way <see cref="DefaultPath"/> and
    /// <see cref="Resolve"/> already are, is <see cref="DefaultPath"/> itself.
    /// </summary>
    /// <remarks>
    /// Compared with <see cref="StringComparison.OrdinalIgnoreCase"/>, not
    /// <see cref="StringComparison.Ordinal"/>: <see cref="Path.GetFullPath(string)"/> resolves
    /// <c>.</c>/<c>..</c> segments and drive-relative paths but does not normalise letter case, and
    /// this application's one target is Windows, whose filesystem treats a path differing only in
    /// case as the same file. An ordinal comparison here would tell "the default location, typed
    /// with different casing" apart from itself. This is a comparison only — it does not change
    /// what gets written anywhere; the string a caller keys a device secret or a store entry by
    /// (<see cref="DefaultPath"/>, <see cref="Resolve"/>'s own return value) stays exactly as
    /// <see cref="Path.GetFullPath(string)"/> produced it, case included.
    /// </remarks>
    public static bool IsDefaultPath(string path) =>
        string.Equals(Path.GetFullPath(path), DefaultPath, StringComparison.OrdinalIgnoreCase);
}
