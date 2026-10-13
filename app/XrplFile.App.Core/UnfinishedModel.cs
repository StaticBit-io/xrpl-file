namespace XrplFile.App.Core;

/// <summary>
/// One run that did not finish. <see cref="Unreadable"/> is set exactly when the state file could
/// not be read, and then every other field except <see cref="Path"/> is empty: a file that cannot be
/// parsed still names, by its path, a run that spent money.
/// </summary>
public sealed record UnfinishedRow(
    string Path,
    string? Network,
    string? Writer,
    string? KeyReference,
    string? Type,
    string? Name,
    long Size,
    string? Stage,
    int Chunks,
    string? Unreadable);

/// <summary>
/// The unfinished-runs list (design 4.4) and resuming one (design 11).
/// </summary>
public sealed class UnfinishedModel(RunPaths paths, PublicationRun run, IFilePickerService picker)
{
    /// <summary>What the list adds when a run in it uses tickets (spec 6.2).</summary>
    public const string TicketNote =
        "Deleting a run leaves the tickets it reserved on this account, each holding an owner reserve, until a "
        + "later publication from this account spends them or the account is closed.";

    public IReadOnlyList<UnfinishedRow> Rows { get; private set; } = [];

    public string? Message { get; private set; }

    /// <summary>Whether <see cref="TicketNote"/> belongs under the current rows.</summary>
    public bool ShowsTicketNote => ShowsTicketNoteFor(Rows);

    /// <summary>
    /// True when any readable row is a ticketed run, decided by the threshold <see cref="PublishPolicy"/>
    /// applies rather than a copy of it. An unreadable row knows no size and says nothing either way.
    /// </summary>
    public static bool ShowsTicketNoteFor(IEnumerable<UnfinishedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return rows.Any(row => row.Unreadable is null && PublishPolicy.UsesTickets(row.Size));
    }

    public void Refresh()
    {
        Message = null;

        Rows =
        [
            .. PublicationSession.Unfinished(paths.StateDirectory).Select(unfinished => unfinished.State is { } state
                ? new UnfinishedRow(
                    unfinished.Path, state.Network, state.Writer, state.KeyReference, state.Type,
                    state.Name, state.Size, RunModel.StageNameOf(state.Phase), ChunksOf(state.Size, state.Chunk), Unreadable: null)
                : new UnfinishedRow(
                    unfinished.Path, null, null, null, null, null, 0, null, 0,
                    unfinished.Unreadable ?? "This run's state file could not be read.")),
        ];
    }

    /// <summary>
    /// How many chunks the run's file splits into. The state file records the chunk size, not a
    /// count, and its per-chunk hashes are written only once the run is done, so the file's own
    /// chunk count is what an unfinished row can honestly show. Zero for a chunk size that cannot
    /// split anything.
    /// </summary>
    public static int ChunksOf(long size, int chunk) =>
        chunk <= 0 || size <= 0 ? 0 : (int)((size + chunk - 1) / chunk);

    /// <summary>
    /// Asks for the file again and hands the run back to <see cref="PublicationRun"/>.
    /// <see langword="false"/> when nothing was started, with <see cref="Message"/> saying why —
    /// except when the person simply cancelled the file picker, which is not a failure and says
    /// nothing.
    /// </summary>
    public async Task<bool> ResumeAsync(UnfinishedRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        Message = null;

        if (row.Unreadable is not null)
        {
            Message = "This run's state file cannot be read, so there is nothing to resume from. It can be deleted.";
            return false;
        }

        string? path = await picker.PickExistingFilePathAsync(cancellationToken).ConfigureAwait(false);
        if (path is null) return false;

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "That file could not be read.";
            return false;
        }

        // Checked here so the person hears "that is not the same file" from the screen rather than
        // from a run that starts, connects and then refuses. The library checks it again against the
        // recorded Merkle root, which is the check that actually binds -- this one only makes the
        // common mistake cheap.
        if (bytes.LongLength != row.Size)
        {
            Message =
                $"That file is {bytes.LongLength:N0} bytes and this run was publishing {row.Size:N0}. "
                + "Resuming needs the same file the run started with.";
            return false;
        }

        // The key, the type, the writer and the network all come from the state file rather than
        // from this screen: PublicationSession.ResumeAsync takes them from there too, and a resume
        // that disagreed with the state would be refused with ResumeMismatch after connecting.
        // KeyReference above all -- PublicationRun builds its VaultSigner from it, and an empty one
        // fails with NoSuchEntry at the worst possible moment.
        PublishRequest request = new(
            bytes,
            row.Type ?? "application/octet-stream",
            row.Name ?? Path.GetFileName(path),
            row.KeyReference!,
            row.Writer!,
            row.Network!);

        if (!run.Resume(row.Path, request))
        {
            Message = "A publication is already running.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Deletes a state file. What this costs is stated on the screen, not here: the money that run
    /// spent is spent either way, and what is lost is the ability to finish it without paying again.
    /// </summary>
    public void Delete(UnfinishedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        string? failure = null;

        try
        {
            File.Delete(row.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = "That state file could not be deleted.";
        }

        // Refresh() clears Message on its own first line -- it has to run either way, since a
        // successful delete needs the row gone and a failed one needs the row (still on disk) to
        // stay listed. The failure, if there is one, is applied after, or Refresh() would erase it
        // and this destructive action would fail with nothing on screen to say so.
        Refresh();

        if (failure is not null) Message = failure;
    }
}
