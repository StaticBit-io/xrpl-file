using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XrplFile;

/// <summary>
/// What a publication needs to survive the process dying.
///
/// It holds no seed. The key it names is a reference the caller understands — a row in a key
/// store, a file name, whatever the application uses — and resolving it back to a signer is
/// somebody else's job. That keeps the whole secret surface in one place.
/// </summary>
/// <param name="StartedInLedger">
/// Where reconciliation begins on a resume. Without it a resumed run would sweep the account's
/// entire history rather than the few hundred ledgers it wrote in.
/// </param>
/// <param name="Phase">
/// The last stage the run was seen in: one of "chunks", "lists", "read-back", "manifest", "done".
/// It is written when the stage changes, so a run interrupted half-way names the stage it died in
/// rather than the one it started in.
/// </param>
/// <param name="ChunkHashes">
/// What has landed so far, by index. A resume does not trust this — the ledger decides — but it
/// carries the run's report through an interruption.
/// </param>
public sealed record PublicationState(
    int Version,
    string Network,
    string Writer,
    string KeyReference,
    string Type,
    string? Name,
    long Size,
    int Chunk,
    string? Root,
    long StartedInLedger,
    string Phase,
    IReadOnlyDictionary<int, string> ChunkHashes)
{
    /// <summary>
    /// 2 since <see cref="Root"/> became something a run records at its start rather than at its
    /// end. A version 1 file names a started run with no root in it, and a resume that read one
    /// would have nothing to check the bytes it was handed against — so those files are refused
    /// by <see cref="Load"/> and listed as unreadable by <see cref="PublicationSession.Unfinished"/>,
    /// which is where a person can still see the account and the ledger they name.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>The one this file ends with, on every platform, so two hosts write the same bytes.</summary>
    private const string LineEnding = "\n";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Written beside and moved into place: a crash during the write must not leave a state
        // file that parses as something it is not. The flush is what makes that true of a machine
        // losing power rather than only of a process dying: without it the move can reach the disk
        // ahead of the bytes it moves, and the run's record comes back empty or truncated.
        string temporary = path + ".writing";

        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Options) + LineEnding);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    public static PublicationState Load(string path)
    {
        PublicationState? state = JsonSerializer.Deserialize<PublicationState>(File.ReadAllText(path), Options);

        if (state is null) throw new FormatException($"{path} did not deserialize as a publication state");
        if (state.Version != CurrentVersion) throw new FormatException($"state version {state.Version} is not one this library reads");

        return state;
    }
}
