namespace XrplFile.App.Core;

/// <summary>
/// Where a publication's state files and the publications list live: always in the application's
/// own data directory, never beside the vault file (design F12). The vault is portable by design
/// (C1's E5) and may sit on removable media or in a synchronised folder; a half-finished run's
/// state is neither portable nor something to synchronise.
/// </summary>
public sealed class RunPaths
{
    /// <param name="baseDirectory">
    /// Where to put them. Defaults to the application data directory, which is what the running
    /// application uses; tests pass a temporary directory.
    /// </param>
    public RunPaths(string? baseDirectory = null)
    {
        // The directory name itself comes from VaultLocation, not a second copy of the same literal
        // here: this directory and the vault's default one are both named relative to the same
        // application-data root, and the two must agree for design F12 to mean anything.
        string root = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), VaultLocation.ApplicationDirectoryName);

        StateDirectory = Path.GetFullPath(Path.Combine(root, "runs"));
        HistoryPath = Path.GetFullPath(Path.Combine(root, "publications.json"));
    }

    /// <summary>What <see cref="SessionOptions.StateDirectory"/> is set to.</summary>
    public string StateDirectory { get; }

    public string HistoryPath { get; }

    /// <summary>
    /// Creates the state directory. <see cref="PublicationSession.StartAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, string, string, string, string, SessionOptions, CancellationToken)"/> writes its state
    /// file there on the run's first save, and <see cref="PublicationSession.Unfinished"/> treats a
    /// missing directory as "no unfinished runs" — so this is called before a run starts, not
    /// before the list is read.
    /// </summary>
    public void EnsureCreated() => Directory.CreateDirectory(StateDirectory);
}
