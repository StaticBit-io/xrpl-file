namespace XrplFile;

/// <summary>
/// The sequences an account's records took, as sorted, merged half-open ranges (spec S20, B30). A
/// run's own sequences are contiguous, so a resume of a large run holds a handful of ranges where a
/// set of every sequence held one entry per transaction.
/// </summary>
internal sealed class SequenceRanges
{
    private readonly List<(uint Start, uint End)> _ranges = [];

    /// <summary>The lowest sequence any record took, or null when none took one.</summary>
    public uint? Lowest => _ranges.Count == 0 ? null : _ranges[0].Start;

    /// <summary>A record with a non-zero sequence takes it; a TicketCreate also takes the sequences of its tickets; a ticketed record takes none.</summary>
    public void Add(LedgerTransaction tx)
    {
        ArgumentNullException.ThrowIfNull(tx);

        if (tx.Sequence == 0) return;

        uint tickets = string.Equals(tx.TransactionType, "TicketCreate", StringComparison.Ordinal) && tx.TicketCount is { } count ? count : 0;

        // A garbled record near the top of the range must not throw out of a reconciliation: its
        // end is clamped, which at worst leaves a sequence uncovered, and coverage then fails safe.
        Add(tx.Sequence, (uint)Math.Min((ulong)tx.Sequence + 1 + tickets, uint.MaxValue));
    }

    /// <summary>Adds [<paramref name="start"/>, <paramref name="end"/>), merging with every range it touches.</summary>
    public void Add(uint start, uint end)
    {
        if (end <= start) return;

        int at = _ranges.FindIndex(range => range.End >= start);
        if (at < 0)
        {
            _ranges.Add((start, end));
            return;
        }

        uint mergedStart = Math.Min(start, _ranges[at].Start);
        uint mergedEnd = end;
        int last = at;

        while (last < _ranges.Count && _ranges[last].Start <= end)
        {
            mergedEnd = Math.Max(mergedEnd, _ranges[last].End);
            last++;
        }

        if (last == at)
        {
            _ranges.Insert(at, (start, end));
            return;
        }

        _ranges.RemoveRange(at, last - at);
        _ranges.Insert(at, (mergedStart, mergedEnd));
    }

    /// <summary>Whether every sequence in [<paramref name="from"/>, <paramref name="to"/>) was taken.</summary>
    public bool Covers(uint from, uint to)
    {
        if (to <= from) return true;

        foreach ((uint start, uint end) in _ranges)
        {
            if (start <= from && end >= to) return true;
        }

        return false;
    }
}
