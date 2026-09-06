using System.Text.Json;
using System.Text.Json.Serialization;

namespace XrplFile.Tests;

public sealed record VectorFile(
    string Seed, long Size, string Sha256, int Chunk, long Chunks, int Blocks,
    IReadOnlyList<string> BlockHashes, string Root, VectorLedger Ledger,
    IReadOnlyList<string> Lists, string Manifest);

public sealed record VectorLedger(long First, long Last, IReadOnlyList<long> BlockLedgers, long? ListLedger, long ManifestLedger);

public sealed record VectorMerkle(IReadOnlyDictionary<string, string> LeafRoots, string BlockHashOfFiveChunks, string RootOverThreeEntries);

public sealed record VectorSet(int Version, VectorMerkle Merkle, IReadOnlyList<VectorFile> Files);

public static class Vectors
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static VectorSet Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "vectors", "v1.json");
        return JsonSerializer.Deserialize<VectorSet>(System.IO.File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException("tests/vectors/v1.json did not deserialize");
    }

    public static VectorFile File(long size) => Load().Files.Single(file => file.Size == size);
}
