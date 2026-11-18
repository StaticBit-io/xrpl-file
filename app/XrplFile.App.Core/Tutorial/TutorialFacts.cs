using XrplFile.Keys;

namespace XrplFile.App.Core.Tutorial;

/// <summary>
/// What the tutorial needs to know about the application at one instant. Gathering is all this does:
/// every decision made from these facts lives in <see cref="TutorialModel"/>, which tests drive with
/// plain values and no vault or network.
/// </summary>
/// <param name="KeyCount">Entries in the vault, damaged ones included: a vault with damage was used.</param>
/// <param name="FirstAccountReference">The first row that still has actions, throwaway rows before imported ones.</param>
/// <param name="NotExportedReference">The first such row that exists only in this vault.</param>
/// <param name="PublicationRunning">Whether a publication is running.</param>
/// <param name="ReadState">State of the Read tab's read.</param>
/// <param name="ReadTabEmpty">No link typed, no lookup shown, no lookup running.</param>
/// <param name="LookupSucceeded">The lookup found a file that can be read.</param>
/// <param name="LookupFailed">The lookup ended with a message.</param>
public sealed record TutorialFacts(
    int KeyCount,
    string? FirstAccountReference,
    string? NotExportedReference,
    bool PublicationRunning,
    ReadState ReadState,
    bool ReadTabEmpty,
    bool LookupSucceeded,
    bool LookupFailed)
{
    /// <summary>Reads the facts from the live models.</summary>
    public static TutorialFacts Capture(IKeyVault vault, AccountsModel accounts, RunModel run, ReadModel read, ReadRun readRun)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(readRun);

        List<AccountRow> rows =
        [
            .. accounts.ThrowawayRows.Where(row => row.HasActions),
            .. accounts.ImportedRows.Where(row => row.HasActions),
        ];
        ReadLookupSnapshot lookup = read.Snapshot;

        return new TutorialFacts(
            vault.References.Count,
            rows.Count > 0 ? rows[0].Reference : null,
            rows.FirstOrDefault(row => row.NeedsExporting)?.Reference,
            run.IsRunning,
            readRun.Snapshot.State,
            read.Link.Length == 0 && lookup.Manifest is null && lookup.Message is null && !read.LookingUp,
            lookup.CanRead,
            lookup.Message is not null);
    }
}
