using XrplFile.Keys;

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
    string? Unreadable)
{
    /// <summary>The Merkle root the run recorded at its start, which a resumed file must reproduce.</summary>
    public string? Root { get; init; }

    /// <summary>The absolute path the file was read from when the run started (spec B17), or null.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The Batch payer's classic address, or null for a one-account run.</summary>
    public string? Payer { get; init; }

    /// <summary>The vault reference of the Batch payer's key, or null for a one-account run.</summary>
    public string? PayerKeyReference { get; init; }

    /// <summary>True for a Batch run (a version-3 state file).</summary>
    public bool IsBatch { get; init; }

    /// <summary>The strategy the state recorded, or null for a version-2 file written before strategies were.</summary>
    public string? Strategy { get; init; }

    /// <summary>"Batch, paid by &lt;label&gt;", "Tickets" or "Sequence" (spec 6.4).</summary>
    public string? Method { get; init; }
}

/// <summary>
/// The unfinished-runs list (design 4.4) and resuming one (design 11).
/// </summary>
public sealed class UnfinishedModel(RunPaths paths, PublicationRun run, IFilePickerService picker, IKeyVault? vault = null)
{
    /// <summary>What the list adds when a run in it uses tickets (spec 6.2).</summary>
    public const string TicketNote =
        "Deleting a run leaves the tickets it reserved on this account, each holding an owner reserve, until a "
        + "later publication from this account spends them or the account is closed.";

    /// <summary>The vault labels the last <see cref="RefreshAsync"/> read, so a synchronous <see cref="Refresh"/> keeps naming payers by them.</summary>
    private Dictionary<string, string> _labels = new(StringComparer.Ordinal);

    public IReadOnlyList<UnfinishedRow> Rows { get; private set; } = [];

    public string? Message { get; private set; }

    /// <summary>The run waiting for a payer because its payer key left the vault (spec B18), or null.</summary>
    public UnfinishedRow? NeedsPayer { get; private set; }

    /// <summary>The keys on that run's network, the writer's own account left out.</summary>
    public IReadOnlyList<VaultEntryInfo> PayerChoices { get; private set; } = [];

    /// <summary>Whether <see cref="TicketNote"/> belongs under the current rows.</summary>
    public bool ShowsTicketNote => ShowsTicketNoteFor(Rows);

    /// <summary>
    /// True when any readable row is a ticketed run: decided by the recorded strategy when there is one
    /// (a Batch row is never ticketed), and otherwise by the threshold <see cref="PublishPolicy"/>
    /// applies rather than a copy of it. An unreadable row knows no size and says nothing either way.
    /// </summary>
    public static bool ShowsTicketNoteFor(IEnumerable<UnfinishedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return rows.Any(row => row.Unreadable is null && !row.IsBatch
            && (row.Strategy is { } strategy ? strategy == PublicationState.TicketStrategyName : PublishPolicy.UsesTickets(row.Size)));
    }

    /// <summary>
    /// Re-reads the state files. A run waiting for a payer keeps waiting while its state file is still
    /// listed, with the reason it is waiting, so the chooser is there when the person reaches this
    /// list from the failed-run screen; anything else clears the message.
    /// </summary>
    public void Refresh()
    {
        UnfinishedRow? waiting = NeedsPayer;
        string? waitingMessage = Message;

        Message = null;
        NeedsPayer = null;

        Rows =
        [
            .. PublicationSession.Unfinished(paths.StateDirectory).Select(unfinished =>
            {
                if (unfinished.State is not { } state)
                {
                    return new UnfinishedRow(
                        unfinished.Path, null, null, null, null, null, 0, null, 0,
                        unfinished.Unreadable ?? "This run's state file could not be read.");
                }

                UnfinishedRow row = new(
                    unfinished.Path, state.Network, state.Writer, state.KeyReference, state.Type,
                    state.Name, state.Size, RunModel.StageNameOf(state.Phase), ChunksOf(state.Size, state.Chunk), Unreadable: null)
                {
                    Root = state.Root,
                    SourcePath = state.SourcePath,
                    Payer = state.Payer,
                    PayerKeyReference = state.PayerKeyReference,
                    IsBatch = state.Version == PublicationState.BatchVersion,
                    Strategy = state.Strategy,
                };

                // Addresses stand in for labels until RefreshAsync has read the vault.
                return row with { Method = MethodOf(row, _labels) };
            }),
        ];

        if (waiting is not null && Rows.FirstOrDefault(row => string.Equals(row.Path, waiting.Path, StringComparison.OrdinalIgnoreCase)) is { } still)
        {
            NeedsPayer = still;
            Message = waitingMessage;
        }
        else
        {
            PayerChoices = [];
        }
    }

    /// <summary>Refresh, then names Batch payers by their vault labels when the vault can be read.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Refresh();

        IReadOnlyList<VaultEntryInfo> keys = await KeysAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> labels = new(StringComparer.Ordinal);
        foreach (VaultEntryInfo key in keys) labels[key.Reference] = key.Label;

        _labels = labels;
        Rows = [.. Rows.Select(row => row.Unreadable is null ? row with { Method = MethodOf(row, _labels) } : row)];
        if (NeedsPayer is { } waiting) NeedsPayer = Rows.FirstOrDefault(row => string.Equals(row.Path, waiting.Path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// "Batch, paid by &lt;label&gt;" for a Batch row (the payer's address when the key's label is not
    /// known), otherwise "Tickets" or "Sequence" by the recorded strategy, or by the size when the state
    /// predates strategies. Null for an unreadable row.
    /// </summary>
    public static string? MethodOf(UnfinishedRow row, IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(labels);

        if (row.Unreadable is not null) return null;

        if (row.IsBatch)
        {
            string payer = row.PayerKeyReference is { } reference && labels.TryGetValue(reference, out string? label) ? label : row.Payer ?? "an unknown account";
            return $"Batch, paid by {payer}";
        }

        bool tickets = row.Strategy is { } strategy ? strategy == PublicationState.TicketStrategyName : PublishPolicy.UsesTickets(row.Size);
        return tickets ? "Tickets" : "Sequence";
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
    /// Resume from the failed-run screen (spec B19): the same path as the list, found by the run's
    /// state file. <see langword="false"/> with <see cref="Message"/> set when the run is no longer listed.
    /// </summary>
    public async Task<bool> ResumeByPathAsync(string statePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(statePath);

        Refresh();

        UnfinishedRow? row = Rows.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.Path), Path.GetFullPath(statePath), StringComparison.OrdinalIgnoreCase));

        if (row is null)
        {
            Message = "This run is no longer listed under unfinished runs, so there is nothing to resume.";
            return false;
        }

        return await ResumeAsync(row, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a run (spec 5.2): from the file it recorded, without a picker, unless that fails; a
    /// Batch run whose payer key left the vault first asks for another payer. <see langword="false"/>
    /// when nothing was started, with <see cref="Message"/> saying why -- except when the person
    /// cancelled the picker, which only leaves the reason the picker was opened for.
    /// </summary>
    public async Task<bool> ResumeAsync(UnfinishedRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        Message = null;
        NeedsPayer = null;
        PayerChoices = [];

        if (row.Unreadable is not null)
        {
            Message = "This run's state file cannot be read, so there is nothing to resume from. It can be deleted.";
            return false;
        }

        if (row.IsBatch && vault is not null)
        {
            IReadOnlyList<VaultEntryInfo> keys = await KeysAsync(cancellationToken).ConfigureAwait(false);

            if (!keys.Any(key => string.Equals(key.Reference, row.PayerKeyReference, StringComparison.Ordinal)))
            {
                XrplFileLink.TryNormalizeNetwork(row.Network ?? string.Empty, out string? network);

                NeedsPayer = row;
                PayerChoices = [.. keys.Where(key =>
                    !string.Equals(key.Address, row.Writer, StringComparison.Ordinal)
                    && XrplFileLink.TryNormalizeNetwork(key.Network, out string? other)
                    && string.Equals(other, network, StringComparison.Ordinal))];
                Message = "The key that paid for this run is no longer in the vault. Choose another payer on the same network to resume it.";
                return false;
            }
        }

        return await ResumeWithAsync(row, row.PayerKeyReference, row.Payer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resumes a Batch run whose payer key left the vault, with the key the person chose (spec 5.2 step 5).</summary>
    public async Task<bool> ResumeWithPayerAsync(UnfinishedRow row, string payerReference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrEmpty(payerReference);

        Message = null;
        VaultEntryInfo? payer = PayerChoices.FirstOrDefault(key => string.Equals(key.Reference, payerReference, StringComparison.Ordinal));

        if (payer is null)
        {
            Message = "Choose a payer on this run's network.";
            return false;
        }

        NeedsPayer = null;
        PayerChoices = [];

        return await ResumeWithAsync(row, payer.Reference, payer.Address, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<VaultEntryInfo>> KeysAsync(CancellationToken cancellationToken)
    {
        if (vault is null) return [];

        try
        {
            return await vault.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (VaultException)
        {
            return [];
        }
    }

    /// <summary>
    /// Why the recorded file cannot be used (spec B17), or null when it can be tried: it has gone, it
    /// cannot be opened, its size changed, or the last resume found its bytes changed. Nothing is
    /// read: the run opens the file and checks its root as it hashes it (spec S7).
    /// </summary>
    private string? RecordedSourceProblem(UnfinishedRow row, string source)
    {
        if (!File.Exists(source)) return $"The file this run was publishing is no longer at {source}. Choose it again.";

        if (run.HasRejectedSource(row.Path)) return $"The file at {source} has changed since the run started. Choose the original file.";

        try
        {
            if (new FileInfo(source).Length != row.Size) return $"The file at {source} has changed since the run started. Choose the original file.";

            using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The file at {source} could not be read. Choose it again.";
        }

        return null;
    }

    private async Task<bool> ResumeWithAsync(UnfinishedRow row, string? payerReference, string? payerAccount, CancellationToken cancellationToken)
    {
        string? sourcePath = row.SourcePath;
        bool recorded = sourcePath is not null && (Message = RecordedSourceProblem(row, sourcePath)) is null;

        if (!recorded)
        {
            string? path = await picker.PickExistingFilePathAsync(cancellationToken).ConfigureAwait(false);
            if (path is null) return false;

            long size;

            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Message = "That file could not be read.";
                return false;
            }

            // Checked here so the person hears "that is not the same file" from the screen rather than
            // from a run that starts and refuses. The run checks the root, which is what binds.
            if (size != row.Size)
            {
                Message =
                    $"That file is {size:N0} bytes and this run was publishing {row.Size:N0}. "
                    + "Resuming needs the same file the run started with.";
                return false;
            }

            sourcePath = path;
        }

        Message = null;

        // The key, the type, the writer and the network come from the state file, as the session takes them.
        PublishRequest request = new(
            row.Size,
            row.Type ?? "application/octet-stream",
            row.Name ?? Path.GetFileName(sourcePath ?? row.Path),
            row.KeyReference!,
            row.Writer!,
            row.Network!,
            row.IsBatch ? payerReference : null,
            row.IsBatch ? payerAccount : null,
            sourcePath)
        {
            SourceRecorded = recorded,
            WriterLabel = _labels.GetValueOrDefault(row.KeyReference ?? string.Empty),
            PayerLabel = payerReference is null ? null : _labels.GetValueOrDefault(payerReference),
        };

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
