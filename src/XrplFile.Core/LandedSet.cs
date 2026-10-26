using System.Numerics;

namespace XrplFile;

/// <summary>
/// One bit per memo of a call (spec S19, S23): whether it landed. It has one owner, who adds to it
/// from one thread at a time; it is not safe for concurrent writers.
/// </summary>
public sealed class LandedSet
{
    private readonly ulong[] _words;
    private int _count;

    public LandedSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        Capacity = capacity;
        _words = new ulong[(capacity + 63) / 64];
    }

    /// <summary>How many memos the set is about.</summary>
    public int Capacity { get; }

    /// <summary>How many have landed.</summary>
    public int Count => Volatile.Read(ref _count);

    public bool Contains(int index)
    {
        Check(index);
        return (_words[index >> 6] & (1UL << (index & 63))) != 0;
    }

    /// <summary>The first index at or after <paramref name="from"/> that has not landed, or −1.</summary>
    public int NextMissing(int from)
    {
        if (from < 0) from = 0;

        for (int word = from >> 6; word < _words.Length; word++)
        {
            ulong missing = ~_words[word];
            if (word == from >> 6) missing &= ulong.MaxValue << (from & 63);
            if (missing == 0) continue;

            int index = (word << 6) + BitOperations.TrailingZeroCount(missing);
            return index < Capacity ? index : -1;
        }

        return -1;
    }

    /// <summary>Marks a memo landed; false when it already was.</summary>
    internal bool Add(int index)
    {
        Check(index);

        ulong bit = 1UL << (index & 63);
        ref ulong word = ref _words[index >> 6];
        if ((word & bit) != 0) return false;

        word |= bit;
        Interlocked.Increment(ref _count);
        return true;
    }

    private void Check(int index)
    {
        if ((uint)index >= (uint)Capacity) throw new ArgumentOutOfRangeException(nameof(index), index, $"the set is about {Capacity} memos");
    }
}
