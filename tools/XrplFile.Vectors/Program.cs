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

// Variants of one file's manifest that a reader must decode to the same file: one carrying a
// cover pointer, one carrying a member this version does not define, those written before the
// format member existed or with a format of the wrong type, and one whose long name the writer
// shortened to fit beside the format and the cover. Each names the name it decodes to.
FileVector variantBase = files.Single(file => file.Size == 1001);
Manifest variantManifest = Manifest.Decode(variantBase.Manifest);
string coverHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cover")));
string formatMember = $"\"format\":\"{Manifest.Format}\",";

static string Replaced(string manifest, string from, string to)
{
    string replaced = manifest.Replace(from, to, StringComparison.Ordinal);
    if (replaced == manifest) throw new InvalidOperationException($"'{from}' was not found in the manifest");
    return replaced;
}

const string unknownMember = ",\"future\":{\"tags\":[\"a\",1],\"on\":true}";
string withUnknownMember = Replaced(variantBase.Manifest, ",\"name\":", unknownMember + ",\"name\":");
string withoutFormat = Replaced(variantBase.Manifest, formatMember, string.Empty);
string formatAsNumber = Replaced(variantBase.Manifest, formatMember, "\"format\":7,");
string formatAsObject = Replaced(variantBase.Manifest, formatMember, "\"format\":{\"url\":\"" + Manifest.Format + "\"},");

string longName = string.Concat(Enumerable.Repeat("vector-long-name-", 100));
string shortened = (variantManifest with { Cover = coverHash, Name = longName }).Encode();
string shortenedName = Manifest.Decode(shortened).Name ?? throw new InvalidOperationException("the long name was dropped");
if (Encoding.UTF8.GetByteCount(shortened) != MemoLayout.ManifestBytes || shortenedName.Length >= longName.Length)
{
    throw new InvalidOperationException("the long-name variant does not fill the manifest budget exactly");
}

string baseName = variantManifest.Name ?? throw new InvalidOperationException("the base vector has no name");
List<ManifestVariant> manifestVariants =
[
    new(variantBase.Size, "cover", coverHash, baseName, (variantManifest with { Cover = coverHash }).Encode()),
    new(variantBase.Size, "unknown member", null, baseName, withUnknownMember),
    new(variantBase.Size, "no format", null, baseName, withoutFormat),
    new(variantBase.Size, "format is a number", null, baseName, formatAsNumber),
    new(variantBase.Size, "format is an object", null, baseName, formatAsObject),
    new(variantBase.Size, "format, cover and a long name", coverHash, shortenedName, shortened),
];

JsonSerializerOptions options = new()
{
    WriteIndented = true,
    // WriteIndented breaks lines with Environment.NewLine, so a regeneration on Windows would
    // rewrite the whole file with CRLF and CI's `diff` against the committed vectors would only
    // survive on .gitattributes normalising it back. The vectors are LF wherever they are made.
    NewLine = "\n",
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
};

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(new VectorSet(1, merkle, files, manifestVariants), options) + "\n");
Console.WriteLine($"wrote {files.Count} file vectors to {output}");

sealed record VectorSet(int Version, Merkle Merkle, IReadOnlyList<FileVector> Files, IReadOnlyList<ManifestVariant> ManifestVariants);
sealed record ManifestVariant(long Of, string Note, string? Cover, string Name, string Manifest);
sealed record Merkle(IReadOnlyDictionary<string, string> LeafRoots, string BlockHashOfFiveChunks, string RootOverThreeEntries);
sealed record Ledger(long First, long Last, IReadOnlyList<long> BlockLedgers, long? ListLedger, long ManifestLedger);
sealed record FileVector(
    string Seed, long Size, string Sha256, int Chunk, long Chunks, int Blocks,
    IReadOnlyList<string> BlockHashes, string Root, Ledger Ledger, IReadOnlyList<string> Lists, string Manifest);
