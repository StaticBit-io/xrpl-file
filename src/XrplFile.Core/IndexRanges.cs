using System.Collections;

namespace XrplFile;

/// <summary>
/// Ascending, disjoint runs of chunk indices, addressed by position (spec S19: "missing indices are
/// an int[] or ranges"). A fresh run of 17 million chunks is one range, not 68 MB of indices.
/// </summary>
internal sealed class IndexRanges : IReadOnlyList<long>
{
    private readonly long[] _starts;
    private readonly int[] _before;

    private IndexRanges(long[] starts, int[] before, int count)
    {
        _starts = starts;
        _before = before;
        Count = count;
    }

    public int Count { get; }

    public static IndexRanges Of(IReadOnlyList<(long Start, int Length)> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        long[] starts = new long[ranges.Count];
        int[] before = new int[ranges.Count];
        int count = 0;
        long end = long.MinValue;

        for (int r = 0; r < ranges.Count; r++)
        {
            (long start, int length) = ranges[r];

            if (length <= 0 || start < end)
            {
                throw new ArgumentException("ranges are ascending, disjoint and not empty", nameof(ranges));
            }

            starts[r] = start;
            before[r] = count;
            count = checked(count + length);
            end = start + length;
        }

        return new IndexRanges(starts, before, count);
    }

    public long this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index), index, $"there are {Count} indices");

            int range = Array.BinarySearch(_before, index);
            if (range < 0) range = ~range - 1;

            return _starts[range] + (index - _before[range]);
        }
    }

    /// <summary>The position of <paramref name="value"/>, or −1 when it is not in any range.</summary>
    public int IndexOf(long value)
    {
        int range = Array.BinarySearch(_starts, value);
        if (range < 0) range = ~range - 1;
        if (range < 0) return -1;

        int length = (range + 1 < _before.Length ? _before[range + 1] : Count) - _before[range];
        long offset = value - _starts[range];

        return offset < length ? _before[range] + (int)offset : -1;
    }

    public IEnumerator<long> GetEnumerator()
    {
        for (int index = 0; index < Count; index++) yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
