using System.Buffers;
using System.Collections;

namespace XrplFile;

/// <summary>
/// A chunk pass's memos, built from the source when one is read and never cached (spec S4). A
/// chunk read from a local disk costs microseconds against a submit round trip of hundreds of
/// milliseconds, so nothing is gained by holding the pass's memos, which at 4 KB of UTF-16 hex per
/// chunk were 7 GB for a 1.7 GB file. Consumers read it by index or in round-sized slices; no code
/// materializes it.
/// </summary>
internal sealed class LazyMemoList(IPublicationSource source, IndexRanges chunks) : IReadOnlyList<MemoSubmission>
{
    public int Count => chunks.Count;

    public MemoSubmission this[int index]
    {
        get
        {
            long chunk = chunks[index];
            byte[] buffer = ArrayPool<byte>.Shared.Rent(MemoLayout.ChunkBytes);

            try
            {
                int length = source.ReadChunk(chunk, buffer);
                return new MemoSubmission(MemoLayout.ChunkMarkerHex(chunk), MemoLayout.Hex(buffer.AsSpan(0, length)), null);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>The file chunk the memo at <paramref name="index"/> carries.</summary>
    public long ChunkAt(int index) => chunks[index];

    /// <summary>The memo's marker, without reading the file.</summary>
    public string MarkerHexAt(int index) => MemoLayout.ChunkMarkerHex(chunks[index]);

    public IEnumerator<MemoSubmission> GetEnumerator()
    {
        for (int index = 0; index < Count; index++) yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
