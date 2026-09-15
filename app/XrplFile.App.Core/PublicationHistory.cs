using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace XrplFile.App.Core;

/// <summary>
/// The publications list: one JSON array on disk, appended after a run succeeds (design 12). A link
/// nobody recorded is a file nobody can find again, so this exists; it is the person's own history,
/// so <see cref="Forget"/> and <see cref="Clear"/> exist too.
/// </summary>
public sealed class PublicationHistory
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
    /// </summary>
    private readonly Lock _fileLock = new();

    private readonly string _path;

    public PublicationHistory(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Why the last <see cref="Read"/>, <see cref="Append"/>, <see cref="Forget"/> or
    /// <see cref="Clear"/> did not do what it says, or <see langword="null"/> when it did.
    /// A screen shows this instead of pretending the person has never published anything, or that a
    /// publication which actually succeeded was safely recorded when it was not.
    /// </summary>
    public string? Failure { get; private set; }

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
    /// call.
    /// </summary>
    public void Append(PublicationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_fileLock)
        {
            List<PublicationRecord> records = [.. ReadCore(), record];
            SaveCore(records);
        }
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
    /// a later task calls this immediately after a publication succeeds, and an <see cref="IOException"/>
    /// escaping from here would be caught and reported as a failed publication when in fact the
    /// link is good and only the history could not be updated.
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
