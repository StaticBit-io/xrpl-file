namespace XrplFile;

/// <summary>
/// A round's memos by marker, built from the round's pending memos only (at most
/// <see cref="BatchStrategyOptions.MaxRoundMemos"/>). Chunk and list markers carry their index, and a
/// pass's indices are distinct, so the pass-wide check of distinct markers is gone (spec 4.4); a
/// duplicate within a round is still refused, because the sweep could not tell the two apart.
/// </summary>
internal sealed class RoundMarkers
{
    private readonly IReadOnlyList<MemoSubmission> _memos;
    private readonly Dictionary<string, int> _byMarker;

    public RoundMarkers(IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending)
    {
        _memos = memos;
        _byMarker = new Dictionary<string, int>(pending.Count, StringComparer.OrdinalIgnoreCase);

        foreach (int index in pending)
        {
            string marker = MarkerHexAt(memos, index);

            if (!_byMarker.TryAdd(marker, index))
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"two memos carry the marker {marker}, which the sweep could not tell apart");
            }
        }
    }

    public bool TryIndexOf(string? memoTypeHex, out int callIndex)
    {
        callIndex = -1;
        return memoTypeHex is not null && _byMarker.TryGetValue(memoTypeHex, out callIndex);
    }

    /// <summary>Whether a record carries exactly the bytes this memo carries. The memo is built from the source to answer.</summary>
    public bool Matches(int callIndex, string? memoDataHex) =>
        string.Equals(memoDataHex, _memos[callIndex].MemoDataHex, StringComparison.OrdinalIgnoreCase);

    /// <summary>A memo's marker, read without the file when the memos are a chunk pass's lazy list.</summary>
    public static string MarkerHexAt(IReadOnlyList<MemoSubmission> memos, int index) =>
        memos is LazyMemoList lazy ? lazy.MarkerHexAt(index) : memos[index].MemoTypeHex;
}
