using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace XrplFile.App.Core;

/// <summary>
/// What <see cref="IPublicationHistory.Append"/> actually did, returned rather than read back off a
/// shared property afterward: <see cref="PublicationHistory"/> serialises its own calls (see its own
/// remarks), but a caller reading a property once the lock has been released again has no guarantee
/// it is still reading the outcome of its own call rather than a different one that ran in between.
/// </summary>
/// <param name="Recorded">
/// Whether the publication this call was about is actually in the history now. <see langword="false"/>
/// is the only case a caller should call a failure — the publication itself already succeeded and is
/// on the ledger regardless, so this is about the list, not the publication.
/// </param>
/// <param name="Note">
/// Something the person should know about their history after this call, or <see langword="null"/>
/// when there is nothing to say. Present both when <see cref="Recorded"/> is <see langword="false"/>
/// (why it could not be recorded) and, sometimes, when it is <see langword="true"/> (the publication
/// is recorded, but something about the rest of the list is worth mentioning — a corrupt file this
/// call replaced, for one). Never claims a file "has been left as it is" when this call replaced it.
/// </param>
public readonly record struct HistoryAppendResult(bool Recorded, string? Note);

/// <summary>
/// The one operation a caller needs back from a history at the moment it acts, rather than through a
/// shared property afterward. Extracted so a test can hold one call open (see
/// <c>PublicationRunTests.GatedHistory</c>) the same way <c>GatedSubmitSink</c> holds a submission
/// open, to exercise what happens when a second run starts while the first one's append is still in
/// flight.
/// </summary>
public interface IPublicationHistory
{
    /// <inheritdoc cref="PublicationHistory.Append"/>
    HistoryAppendResult Append(PublicationRecord record);
}

/// <summary>
/// The publications list: one JSON array on disk, appended after a run succeeds (design 12). A link
/// nobody recorded is a file nobody can find again, so this exists; it is the person's own history,
/// so <see cref="Forget"/> and <see cref="Clear"/> exist too.
/// </summary>
public sealed class PublicationHistory : IPublicationHistory
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Every public method's body runs entirely under this, never released partway through a
    /// read-then-write pair. Nothing about <see cref="_path"/> guarantees only one publication ever
    /// finishes at once: a screen can start a new run the moment a previous one reports
    /// <see cref="PublicationRunState.Succeeded"/>, and its own history append can then overlap a
    /// still-running one's. Without this, two overlapping calls race <see cref="Failure"/> (a plain
    /// auto-property, last write wins regardless of which call it belongs to) and race
    /// <see cref="SaveCore"/>'s own fixed <c>.tmp</c> path under <see cref="FileShare.None"/> (the loser
    /// finds the file already open and reports "could not be written" for a write that was never
    /// its own to make). Serialising here is simpler than pinning each write to the run that made
    /// it, and honest about what the actual hazard is: it is this type's own file access that is
    /// unsafe under overlap, not something a caller can fix by tagging its calls.
    ///
    /// This lock is per instance, and nothing here enforces one instance per path. Two
    /// <see cref="PublicationHistory"/> objects built over the same file are not protected from each
    /// other at all — whoever wires this into dependency injection has to make it a singleton, or
    /// every race this lock exists to close reopens across instances that each think they are the
    /// only writer.
    /// </summary>
    private readonly Lock _fileLock = new();

    private readonly string _path;

    public PublicationHistory(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Why the last <see cref="Read"/>, <see cref="Forget"/> or <see cref="Clear"/> did not do what
    /// it says, or <see langword="null"/> when it did. <see cref="Append"/> still writes this too —
    /// it calls <see cref="ReadCore"/> and <see cref="SaveCore"/> just like everything else, and
    /// cannot avoid touching it as a side effect of that — but a caller must not read it as
    /// <see cref="Append"/>'s own signal: by the time this property is readable again outside
    /// <see cref="_fileLock"/>, a second, overlapping <see cref="Append"/> already queued behind it
    /// could have changed it first. <see cref="Append"/> returns its own outcome instead (see
    /// <see cref="HistoryAppendResult"/>), computed under the same lock acquisition as the write
    /// itself, which is what makes it safe to trust as that call's own.
    /// </summary>
    public string? Failure { get; private set; }

    /// <summary>
    /// Set by <see cref="RecordFinishedRuns"/> when it held back a finished publication it could not
    /// add to the list, or <see langword="null"/> when it held back nothing. Unlike
    /// <see cref="Failure"/> it is not reset by later calls: the publications stay held back until
    /// a start-up that can read and write the list records them, however the file came to be
    /// readable meanwhile.
    /// </summary>
    public string? RecoveryNote { get; private set; }

    private const string HeldBackNote =
        "Some publications that finished earlier are not in this list yet, because it could not be updated. "
        + "Their links are kept and are added when the application starts with a list it can read and write.";

    /// <summary>Newest first. An unreadable file reads as empty and sets <see cref="Failure"/>.</summary>
    public IReadOnlyList<PublicationRecord> Read()
    {
        lock (_fileLock)
        {
            return ReadCore();
        }
    }

    /// <summary>
    /// Appending to a history that could not be read replaces it with a file holding just the new
    /// record. This is chosen on purpose: the alternative is refusing to record a publication that
    /// actually happened and losing its link. The person's publication is in the ledger; if the
    /// history file is corrupt, replacing it with a fresh start that includes this one is the right
    /// call — but the person still needs to be told their earlier entries are gone, which is what
    /// <see cref="HistoryAppendResult.Note"/> is for on an otherwise-successful append.
    /// </summary>
    public HistoryAppendResult Append(PublicationRecord record) => AppendCore(record, replaceUnreadable: true);

    private HistoryAppendResult AppendCore(PublicationRecord record, bool replaceUnreadable)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_fileLock)
        {
            IReadOnlyList<PublicationRecord> existing = ReadCore();
            bool replacedAnUnreadableFile = Failure is not null;

            if (replacedAnUnreadableFile && !replaceUnreadable)
            {
                // ReadCore's own sentence stays true: the file has been left as it is.
                return new HistoryAppendResult(Recorded: false, Failure);
            }

            SaveCore([.. existing, record]);

            if (Failure is not null)
            {
                // SaveCore's own outcome, not ReadCore's: the write itself failed, so nothing was
                // recorded at all.
                return new HistoryAppendResult(Recorded: false, Failure);
            }

            return new HistoryAppendResult(
                Recorded: true,
                Note: replacedAnUnreadableFile
                    ? "Your previous publications list could not be read, so it has been replaced. "
                      + "This publication is recorded, but earlier entries may be missing."
                    : null);
        }
    }

    /// <summary>
    /// Records every finished run whose state file still exists (spec 5.1): the application deletes a
    /// run's state once History holds its link, so a "done" state still on disk is a run whose History
    /// write never happened. Its link is recorded unless already listed, and the state file is deleted
    /// either way -- which is also why a link the person forgot never comes back (Spec question 22).
    /// Called once at start-up. A state that cannot be read or named as a link is left alone. Returns
    /// how many records were added.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Append"/>, this never replaces a history it cannot read. Append does so to
    /// save the link of a run that has just finished and exists nowhere else; here the state file
    /// still holds the link, so waiting costs nothing, while replacing would silently wipe every
    /// earlier entry at start-up with nobody watching. The states stay on disk for the next start,
    /// and <see cref="RecoveryNote"/> says so.
    /// </remarks>
    public int RecordFinishedRuns(string stateDirectory)
    {
        if (!Directory.Exists(stateDirectory)) return 0;

        HashSet<string> known = new(Read().Select(record => record.Link), StringComparer.Ordinal);
        bool historyUnreadable = Failure is not null;
        int recorded = 0;

        foreach (string path in Directory.EnumerateFiles(stateDirectory, "*.json"))
        {
            PublicationState state;
            string link;

            try
            {
                state = PublicationState.Load(path);

                if (!string.Equals(state.Phase, "done", StringComparison.Ordinal) || state.ManifestHash is not { } manifest) continue;

                link = new XrplFileLink(state.Network, manifest).ToString();
            }
            catch (Exception e) when (e is FormatException or JsonException or IOException
                or UnauthorizedAccessException or ArgumentException or XrplFileException)
            {
                continue;
            }

            if (!known.Contains(link))
            {
                if (historyUnreadable)
                {
                    RecoveryNote = HeldBackNote;
                    continue;
                }

                HistoryAppendResult appended = AppendCore(
                    new PublicationRecord(
                        link, state.Name ?? Path.GetFileName(path), state.Size, state.Type, state.Network, state.Writer,
                        0, 0, File.GetLastWriteTimeUtc(path), state.Payer, Recovered: true),
                    replaceUnreadable: false);

                if (!appended.Recorded)
                {
                    // Either the write failed or the file stopped being readable since the first
                    // read; in both cases the state is kept and retried at the next start-up.
                    RecoveryNote = HeldBackNote;
                    continue;
                }

                known.Add(link);
                recorded++;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Tried again at the next start-up, which finds the link already listed.
            }
        }

        return recorded;
    }

    public void Forget(string link)
    {
        ArgumentException.ThrowIfNullOrEmpty(link);

        lock (_fileLock)
        {
            IReadOnlyList<PublicationRecord> records = ReadCore();
            if (Failure is not null) return;

            SaveCore([.. records.Where(record => !string.Equals(record.Link, link, StringComparison.Ordinal))]);
        }
    }

    public void Clear()
    {
        lock (_fileLock)
        {
            // No need to reset Failure by hand here first: SaveCore clears it on its own success,
            // and its own failure sets it to a sentence about this write, which is the one that
            // matters once Clear has actually been asked for.
            SaveCore([]);
        }
    }

    /// <summary>Not locked itself — every caller already holds <see cref="_fileLock"/>.</summary>
    private IReadOnlyList<PublicationRecord> ReadCore()
    {
        Failure = null;

        try
        {
            if (!File.Exists(_path)) return [];

            List<PublicationRecord>? records = JsonSerializer.Deserialize<List<PublicationRecord>>(
                File.ReadAllBytes(_path), SerializerOptions);

            if (records is null) return [];

            records.Sort((left, right) => right.FinishedAt.CompareTo(left.FinishedAt));
            return records;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Failure = "The publications file could not be read, so this list is empty. The file has been left as it is.";
            return [];
        }
    }

    /// <summary>
    /// Written to a temporary file in the same directory, flushed to disk, and then moved over the
    /// real one, so a crash in the middle cannot leave a truncated array where a history used to be
    /// or a cached write that is lost on power loss. The same durability as the vault's own durable
    /// write. If the write fails, <see cref="Failure"/> is set and the exception is not thrown —
    /// <see cref="Append"/> calls this immediately after a publication succeeds, and an
    /// <see cref="IOException"/> escaping from here would be caught and reported as a failed
    /// publication when in fact the link is good and only the history could not be updated.
    ///
    /// <see cref="Directory.CreateDirectory(string)"/> lives inside the same try as the write, not
    /// before it: a directory this call cannot create (a permissions problem, a path a file already
    /// occupies) is exactly the kind of failure this method exists to degrade rather than throw, and
    /// a caller who only guards the write and not the directory creation would still see the
    /// publication it just paid for reported as failed.
    ///
    /// <see cref="Failure"/> is cleared at the start of the attempt, not left for a caller to reset:
    /// <see cref="Append"/> calls <see cref="ReadCore"/> first, which may set <see cref="Failure"/>
    /// for a corrupt file this write is about to replace — a write that then succeeds must not leave
    /// that stale "could not be read... left as it is" behind, since the file was not left as it is,
    /// it was overwritten. Only this attempt's own outcome decides <see cref="Failure"/> from here on.
    ///
    /// Not locked itself — every caller already holds <see cref="_fileLock"/>.
    /// </summary>
    private void SaveCore(IReadOnlyList<PublicationRecord> records)
    {
        string temporary = _path + ".tmp";

        try
        {
            Failure = null;

            string directoryPath = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directoryPath);

            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(records, SerializerOptions));

            FileStreamOptions options = new()
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };

            using (FileStream stream = new(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failure = "The publications file could not be written. Your file is in the ledger and your link is safe, but this history could not be updated. Try again later.";

            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // Best effort; if cleanup fails, leave it. The next write will overwrite it.
            }
        }
    }
}
