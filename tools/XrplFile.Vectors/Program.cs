using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XrplFile;

// Writes tests/vectors/v1.json from the library's own code. The JavaScript suite checks the same
// file with an independent implementation, which is what makes the vectors a cross-check rather
// than a tautology. Usage: dotnet run --project tools/XrplFile.Vectors -- tests/vectors/v1.json

string output = args.Length > 0 ? args[0] : Path.Combine("tests", "vectors", "v1.json");

long[] sizes = [1, 999, 1000, 1001, 1024000, 1024001, 2500500];

static byte[] Leaf(string text) => MerkleTree.Leaf(Encoding.UTF8.GetBytes(text));

Dictionary<string, string> leafRoots = new();
foreach (int count in new[] { 1, 3, 5, 1025 })
{
    List<byte[]> leaves = [.. Enumerable.Range(0, count).Select(i => Leaf($"leaf-{i}"))];
    leafRoots[count.ToString()] = Convert.ToHexString(MerkleTree.Root(leaves));
}

List<byte[]> fiveChunks = [.. Enumerable.Range(0, 5).Select(i => Encoding.UTF8.GetBytes($"chunk-{i}".PadRight(MemoLayout.ChunkBytes, 'x')))];
List<BlockEntry> threeEntries = [.. Enumerable.Range(0, 3).Select(i => new BlockEntry(i, Convert.ToHexString(MerkleTree.Leaf([(byte)i])), 1000 + i * 40))];

Merkle merkle = new(leafRoots, Convert.ToHexString(FileTree.BlockHash(fiveChunks)), FileTree.RootHex(threeEntries));

List<FileVector> files = [];

foreach (long size in sizes)
{
    string seed = $"xrpl-file/v1/{size}";
    byte[] bytes = VectorStream.Bytes(seed, size);
    IReadOnlyList<byte[]> chunks = FileTree.Split(bytes, MemoLayout.ChunkBytes);
    int blocks = FileTree.BlockCount(chunks.Count);

    const long first = 1000;
    long lastChunkLedger = first + (chunks.Count - 1) / 100;
    long? listLedger = blocks > 1 ? lastChunkLedger + 1 : null;
    long last = listLedger ?? lastChunkLedger;
    long manifestLedger = last + 1;

    List<long> blockLedgers = [];
    List<BlockEntry> entries = [];
    List<string> blockHashes = [];

    for (int block = 0; block < blocks; block++)
    {
        List<byte[]> members = [.. chunks.Skip(block * FileTree.BlockChunks).Take(FileTree.BlockChunks)];
        string hash = Convert.ToHexString(FileTree.BlockHash(members));
        long ledger = first + (1024L * block) / 100;

        blockHashes.Add(hash);
        blockLedgers.Add(ledger);
        entries.Add(new BlockEntry(block, hash, ledger));
    }

    string root = FileTree.RootHex(entries);
    IReadOnlyList<string> lists = blocks > 1 ? FileTree.EncodeEntries(entries) : [];

    Manifest manifest = new(
        "application/octet-stream", size, MemoLayout.ChunkBytes, root, first, last, blocks, lists.Count, $"vector-{size}.bin");

    files.Add(new FileVector(
        seed, size, Convert.ToHexString(SHA256.HashData(bytes)), MemoLayout.ChunkBytes, chunks.Count, blocks,
        blockHashes, root, new Ledger(first, last, blockLedgers, listLedger, manifestLedger), lists, manifest.Encode()));
}

JsonSerializerOptions options = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
};

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(new VectorSet(1, merkle, files), options) + "\n");
Console.WriteLine($"wrote {files.Count} file vectors to {output}");

sealed record VectorSet(int Version, Merkle Merkle, IReadOnlyList<FileVector> Files);
sealed record Merkle(IReadOnlyDictionary<string, string> LeafRoots, string BlockHashOfFiveChunks, string RootOverThreeEntries);
sealed record Ledger(long First, long Last, IReadOnlyList<long> BlockLedgers, long? ListLedger, long ManifestLedger);
sealed record FileVector(
    string Seed, long Size, string Sha256, int Chunk, long Chunks, int Blocks,
    IReadOnlyList<string> BlockHashes, string Root, Ledger Ledger, IReadOnlyList<string> Lists, string Manifest);
