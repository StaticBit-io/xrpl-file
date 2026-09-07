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
/// <param name="Phase">One of "chunks", "lists", "manifest", "done".</param>
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
    public const int CurrentVersion = 1;

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
        // file that parses as something it is not.
        string temporary = path + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options) + "\n");
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
