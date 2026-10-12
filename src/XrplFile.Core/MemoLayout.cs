using System.Text;

namespace XrplFile;

/// <summary>
/// How the three memo kinds are laid out and how much each may carry.
///
/// rippled refuses a transaction whose serialized Memos array exceeds 1024 bytes. A memo with
/// only MemoData costs 5 bytes of framing, which leaves 1019; a MemoType or MemoFormat costs 2
/// bytes of framing plus its own length. The budgets below follow from that and were confirmed
/// on a standalone node: each kind is accepted at its budget and refused one byte over.
/// </summary>
public static class MemoLayout
{
    public const int MaxMemoBytes = 1019;
    public const int MemoTypeFraming = 2;
    public const int MaxIndexDigits = 12;

    /// <summary>"xf/c/" or "xf/b/" plus up to twelve digits.</summary>
    public const int MaxMarkerBytes = 5 + MaxIndexDigits;

    public const string ManifestMarker = "xf/m";

    /// <summary>
    /// What every TicketCreate this library submits carries, and what tells its tickets from
    /// anybody else's (spec I2). Internal on purpose: no reader and no other component needs it.
    /// </summary>
    internal const string TicketMarker = "xf/t";
    public const string JsonFormat = "application/json";

    public const int ChunkBytes = MaxMemoBytes - MemoTypeFraming - MaxMarkerBytes;
    public const int ListBytes = ChunkBytes - MemoTypeFraming - 16;
    public const int ManifestBytes = MaxMemoBytes - MemoTypeFraming - 4 - MemoTypeFraming - 16;

    private const string ChunkPrefix = "xf/c/";
    private const string ListPrefix = "xf/b/";

    public static string ManifestMarkerHex { get; } = Utf8Hex(ManifestMarker);

    internal static string TicketMarkerHex { get; } = Utf8Hex(TicketMarker);

    public static string JsonFormatHex { get; } = Utf8Hex(JsonFormat);

    public static string ChunkMarkerHex(long index) => IndexedMarkerHex(ChunkPrefix, index);

    public static string ListMarkerHex(long index) => IndexedMarkerHex(ListPrefix, index);

    public static long? ChunkIndexOf(string? memoTypeHex) => IndexOf(memoTypeHex, ChunkPrefix);

    public static long? ListIndexOf(string? memoTypeHex) => IndexOf(memoTypeHex, ListPrefix);

    public static bool IsManifest(string? memoTypeHex) =>
        memoTypeHex is not null && string.Equals(TryDecode(memoTypeHex), ManifestMarker, StringComparison.Ordinal);

    public static string Utf8Hex(string text) => Convert.ToHexString(Encoding.UTF8.GetBytes(text));

    public static string FromHexUtf8(string hex) => Encoding.UTF8.GetString(Convert.FromHexString(hex));

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    public static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    private static string IndexedMarkerHex(string prefix, long index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "a memo index is never negative");
        }

        string marker = prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (marker.Length > MaxMarkerBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"an index past {MaxIndexDigits} digits does not fit its memo");
        }

        return Utf8Hex(marker);
    }

    /// <summary>
    /// The index a marker carries, or null when it is not one. Strict on purpose: a leading
    /// zero, a sign or a stray character is somebody else's memo, not a chunk with a quirk.
    /// </summary>
    private static long? IndexOf(string? memoTypeHex, string prefix)
    {
        if (string.IsNullOrEmpty(memoTypeHex)) return null;

        string? marker = TryDecode(memoTypeHex);
        if (marker is null || !marker.StartsWith(prefix, StringComparison.Ordinal)) return null;

        string digits = marker.Substring(prefix.Length);
        if (digits.Length == 0 || digits.Length > MaxIndexDigits) return null;
        if (digits.Length > 1 && digits[0] == '0') return null;

        foreach (char character in digits)
        {
            if (character is < '0' or > '9') return null;
        }

        return long.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? TryDecode(string hex)
    {
        try
        {
            return FromHexUtf8(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
