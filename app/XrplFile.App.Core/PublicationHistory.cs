using System.Text;
using System.Text.Json;

namespace XrplFile.App.Core;

/// <summary>
/// The publications list: one JSON array on disk, appended after a run succeeds (design 12). A link
/// nobody recorded is a file nobody can find again, so this exists; it is the person's own history,
/// so <see cref="Forget"/> and <see cref="Clear"/> exist too.
/// </summary>
public sealed class PublicationHistory
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

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
                File.ReadAllBytes(_path));

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

    public void Append(PublicationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        List<PublicationRecord> records = [.. Read(), record];
        Save(records);
    }

    public void Forget(string link)
    {
        ArgumentException.ThrowIfNullOrEmpty(link);

        Save([.. Read().Where(record => !string.Equals(record.Link, link, StringComparison.Ordinal))]);
    }

    public void Clear() => Save([]);

    /// <summary>
    /// Written to a temporary file in the same directory and then moved over the real one, so a
    /// crash in the middle cannot leave a truncated array where a history used to be. The same
    /// shape as the vault's own durable write, for the same reason.
    /// </summary>
    private void Save(IReadOnlyList<PublicationRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        string temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(records, SerializerOptions)));
        File.Move(temporary, _path, overwrite: true);
    }
}
