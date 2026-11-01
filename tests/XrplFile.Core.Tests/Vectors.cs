using System.Text.Json;
using System.Text.Json.Serialization;

namespace XrplFile.Tests;

public sealed record VectorFile(
    string Seed, long Size, string Sha256, int Chunk, long Chunks, int Blocks,
    IReadOnlyList<string> BlockHashes, string Root, VectorLedger Ledger,
    IReadOnlyList<string> Lists, string Manifest);

public sealed record VectorLedger(long First, long Last, IReadOnlyList<long> BlockLedgers, long? ListLedger, long ManifestLedger);

public sealed record VectorMerkle(IReadOnlyDictionary<string, string> LeafRoots, string BlockHashOfFiveChunks, string RootOverThreeEntries);

public sealed record VectorManifestVariant(long Of, string Note, string? Cover, string Name, string Manifest);

public sealed record VectorSet(int Version, VectorMerkle Merkle, IReadOnlyList<VectorFile> Files, IReadOnlyList<VectorManifestVariant> ManifestVariants);

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

    public const string Writer = "rVectorWriter";

    public static string Hash(string label) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(label)));

    public static string ManifestHash => Hash("manifest");

    /// <summary>The vector's transactions laid out in the synthetic ledger the vector file describes.</summary>
    public static List<LedgerTransaction> Ledger(VectorFile vector)
    {
        byte[] bytes = VectorStream.Bytes(vector.Seed, vector.Size);
        IReadOnlyList<byte[]> chunks = FileTree.Split(bytes, vector.Chunk);
        List<LedgerTransaction> ledger = [];

        for (int i = 0; i < chunks.Count; i++)
        {
            ledger.Add(new LedgerTransaction(
                Hash($"chunk:{i}"), vector.Ledger.First + i / 100, Writer, Succeeded: true, Validated: true, Result: "tesSUCCESS",
                MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunks[i]), MemoFormatHex: null));
        }

        for (int j = 0; j < vector.Lists.Count; j++)
        {
            ledger.Add(new LedgerTransaction(
                Hash($"list:{j}"), vector.Ledger.ListLedger!.Value, Writer, Succeeded: true, Validated: true, Result: "tesSUCCESS",
                MemoLayout.ListMarkerHex(j), MemoLayout.Utf8Hex(vector.Lists[j]), MemoLayout.JsonFormatHex));
        }

        ledger.Add(new LedgerTransaction(
            ManifestHash, vector.Ledger.ManifestLedger, Writer, Succeeded: true, Validated: true, Result: "tesSUCCESS",
            MemoLayout.ManifestMarkerHex, MemoLayout.Utf8Hex(vector.Manifest), MemoLayout.JsonFormatHex));

        return ledger;
    }

    public static XrplFileLink Link => new("testnet", ManifestHash);
}
