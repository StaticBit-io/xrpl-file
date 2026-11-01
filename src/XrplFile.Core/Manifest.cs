using System.Text;
using System.Text.Json;

namespace XrplFile;

/// <summary>
/// The one transaction an xrpl-file link points at: everything a reader needs to locate, order
/// and verify the bytes. Written last, because it names the ledger range the chunks occupy.
/// </summary>
/// <param name="Type">MIME type of the file.</param>
/// <param name="Size">File length in bytes.</param>
/// <param name="Chunk">Chunk payload size the writer used; this library writes MemoLayout.ChunkBytes and reads any value up to it.</param>
/// <param name="Root">Merkle root over the chunks, upper-case hex.</param>
/// <param name="FirstLedger">First validated ledger holding a chunk.</param>
/// <param name="LastLedger">Last validated ledger holding a chunk or a block-list memo.</param>
/// <param name="Blocks">Number of Merkle blocks.</param>
/// <param name="Lists">Number of block-list memos; zero when there is one block.</param>
/// <param name="Name">Optional file name.</param>
/// <param name="Cover">Optional hash of the manifest transaction of the file's cover picture, upper-case hex (format section 4.1); null when absent or malformed.</param>
public sealed record Manifest(
    string Type,
    long Size,
    int Chunk,
    string Root,
    long FirstLedger,
    long LastLedger,
    int Blocks,
    int Lists,
    string? Name,
    string? Cover = null)
{
    public const int Version = 1;

    public long ChunkCount => FileTree.ChunkCount(Size, Chunk);

    /// <summary>
    /// The manifest as JSON that fits its memo. Every field but the name is fixed in size; a
    /// name that does not fit is cut to the longest UTF-8 prefix that does, and dropped when
    /// not even one character fits. A file is never refused over its name.
    /// </summary>
    public string Encode()
    {
        if (Cover is not null && !IsHash(Cover))
        {
            throw new InvalidOperationException("the cover is not a 64-hex transaction hash");
        }

        string? name = Name;

        while (true)
        {
            string json = Serialize(name);

            if (Encoding.UTF8.GetByteCount(json) <= MemoLayout.ManifestBytes) return json;

            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidOperationException("the manifest does not fit its memo even without a name");
            }

            name = ShortenByOneCharacter(name);
            if (name.Length == 0) name = null;
        }
    }

    public static Manifest Decode(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("the manifest is not a JSON object");

            if (Int(root, "v") is not { } version) throw new FormatException("the manifest has no version");
            if (version != Version) throw new FormatException($"manifest version {version} is not one this library reads");

            string type = Text(root, "type") ?? throw new FormatException("the manifest has no type");
            long size = Long(root, "size") ?? throw new FormatException("the manifest has no size");
            int chunk = Int(root, "chunk") ?? throw new FormatException("the manifest has no chunk size");
            string rootHex = Text(root, "root") ?? throw new FormatException("the manifest has no root");
            int blocks = Int(root, "blocks") ?? throw new FormatException("the manifest has no block count");
            int lists = Int(root, "lists") ?? throw new FormatException("the manifest has no list count");

            if (rootHex.Length != MerkleTree.HashBytes * 2 || !rootHex.All(Uri.IsHexDigit))
            {
                throw new FormatException("the manifest root is not 64 hex characters");
            }

            if (!root.TryGetProperty("ledgers", out JsonElement ledgers)
                || ledgers.ValueKind != JsonValueKind.Array
                || ledgers.GetArrayLength() != 2)
            {
                throw new FormatException("the manifest ledgers are not a pair");
            }

            // TryGetInt64 throws on an element that is not a number at all, so the kind is checked
            // first: a "ledgers":["a","b"] has to leave here as the FormatException the reader
            // turns into CorruptManifest, never as an InvalidOperationException nothing catches.
            if (Number(ledgers[0]) is not { } first || Number(ledgers[1]) is not { } last || first <= 0 || last < first)
            {
                throw new FormatException("the manifest ledger range is not ascending");
            }

            return new Manifest(type, size, chunk, rootHex.ToUpperInvariant(), first, last, blocks, lists, Text(root, "name"), CoverHash(root));
        }
        catch (JsonException e)
        {
            throw new FormatException($"the manifest does not parse as JSON: {e.Message}", e);
        }
    }

    /// <summary>Why the counts do not agree with one another, or null when they do.</summary>
    public string? Inconsistency()
    {
        if (Size < 0) return $"the size {Size} is negative";
        if (Chunk <= 0 || Chunk > MemoLayout.ChunkBytes) return $"a chunk of {Chunk} bytes is outside 1..{MemoLayout.ChunkBytes}";

        // Beyond this the file has more chunks than a chunk index can name, and ChunkCount's own
        // arithmetic overflows on the way to saying so -- which is how a size near long.MaxValue
        // used to pass every count check below and reach the reader as a negative chunk count.
        long addressable = (long)Chunk * int.MaxValue;
        if (Size > addressable) return $"the size {Size} is past the {addressable} bytes {Chunk}-byte chunks can address";

        if (Lists < 0) return $"the list count {Lists} is negative";

        int expectedBlocks = FileTree.BlockCount(ChunkCount);
        if (Blocks != expectedBlocks) return $"the manifest says {Blocks} blocks, {ChunkCount} chunks make {expectedBlocks}";

        if ((Lists == 0) != (Blocks == 1)) return $"the manifest says {Blocks} blocks and {Lists} list memos, and a single block is the only file without a list";

        return null;
    }

    private string Serialize(string? name)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteString("type", Type);
            writer.WriteNumber("size", Size);
            writer.WriteNumber("chunk", Chunk);
            writer.WriteString("root", Root);
            writer.WriteStartArray("ledgers");
            writer.WriteNumberValue(FirstLedger);
            writer.WriteNumberValue(LastLedger);
            writer.WriteEndArray();
            writer.WriteNumber("blocks", Blocks);
            writer.WriteNumber("lists", Lists);
            if (Cover is not null) writer.WriteString("cover", Cover.ToUpperInvariant());
            if (name is not null) writer.WriteString("name", name);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>One character off the end — a whole one, so a surrogate pair is not cut in half.</summary>
    private static string ShortenByOneCharacter(string value)
    {
        int cut = value.Length - 1;
        if (cut > 0 && char.IsLowSurrogate(value[cut])) cut--;
        return value.Substring(0, cut);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // A cover is decoration, so a value that is not a 64-hex string reads as absent instead of
    // failing the manifest of the file it decorates (format section 4).
    private static string? CoverHash(JsonElement element)
    {
        string? text = Text(element, "cover");
        return text is not null && IsHash(text) ? text.ToUpperInvariant() : null;
    }

    internal static bool IsHash(string value) =>
        value.Length == MerkleTree.HashBytes * 2 && value.All(Uri.IsHexDigit);

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : null;

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? Number(value) : null;

    private static long? Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;
}
