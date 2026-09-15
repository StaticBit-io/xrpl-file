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

    private readonly string _path;

    public PublicationHistory(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Why the last <see cref="Read"/> came back empty, when it came back empty because the file
    /// could not be read rather than because there is nothing in it. A screen shows this instead of
    /// pretending the person has never published anything.
    /// </summary>
    public string? Failure { get; private set; }

    /// <summary>Newest first. An unreadable file reads as empty and sets <see cref="Failure"/>.</summary>
    public IReadOnlyList<PublicationRecord> Read()
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
    /// Appending to a history that could not be read replaces it with a file holding just the new
    /// record. This is chosen on purpose: the alternative is refusing to record a publication that
    /// actually happened and losing its link. The person's publication is in the ledger; if the
    /// history file is corrupt, replacing it with a fresh start that includes this one is the right
    /// call.
    /// </summary>
    public void Append(PublicationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        List<PublicationRecord> records = [.. Read(), record];
        Save(records);
    }

    public void Forget(string link)
    {
        ArgumentException.ThrowIfNullOrEmpty(link);

        Read();
        if (Failure is not null) return;

        Save([.. Read().Where(record => !string.Equals(record.Link, link, StringComparison.Ordinal))]);
    }

    public void Clear() => Save([]);

    /// <summary>
    /// Written to a temporary file in the same directory, flushed to disk, and then moved over the
    /// real one, so a crash in the middle cannot leave a truncated array where a history used to be
    /// or a cached write that is lost on power loss. The same durability as the vault's own durable
    /// write. If the write fails, <see cref="Failure"/> is set and the exception is not thrown —
    /// a later task calls this immediately after a publication succeeds, and an <see cref="IOException"/>
    /// escaping from here would be caught and reported as a failed publication when in fact the
    /// link is good and only the history could not be updated.
    /// </summary>
    private void Save(IReadOnlyList<PublicationRecord> records)
    {
        string directoryPath = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directoryPath);

        string temporary = _path + ".tmp";

        try
        {
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
