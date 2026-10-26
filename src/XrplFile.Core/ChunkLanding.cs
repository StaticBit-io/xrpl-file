namespace XrplFile;

/// <summary>
/// What has landed of a file's chunks, compactly (spec S19): a bit per chunk, and per block its
/// first ledger, its last ledger and a landed count — about 1 bit per chunk plus 20 bytes per
/// block. No transaction hash and no ledger is kept per chunk: the manifest needs only each block's
/// first ledger and the window's ends, and nothing else in the library reads a chunk's hash.
/// </summary>
public sealed class ChunkLanding
{
    private readonly ulong[] _landed;
    private readonly long[] _first;
    private readonly long[] _last;
    private readonly int[] _count;

    public ChunkLanding(long chunkCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkCount);

        // Every chunk index stays an int (spec S1): 16 GiB is about 17 million chunks.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkCount, (long)int.MaxValue);

        ChunkCount = chunkCount;
        BlockCount = FileTree.BlockCount(chunkCount);
        _landed = new ulong[(chunkCount + 63) / 64];
        _first = new long[BlockCount];
        _last = new long[BlockCount];
        _count = new int[BlockCount];
        Array.Fill(_first, long.MaxValue);
        Array.Fill(_last, long.MinValue);
    }

    public long ChunkCount { get; }

    public int BlockCount { get; }

    /// <summary>How many chunks have landed.</summary>
    public long Landed { get; private set; }

    public bool IsComplete => Landed == ChunkCount;

    public bool IsLanded(long chunk)
    {
        Check(chunk);
        return (_landed[chunk >> 6] & (1UL << (int)(chunk & 63))) != 0;
    }

    /// <summary>How many of the block's chunks have landed.</summary>
    public int LandedIn(int block) => _count[block];

    /// <summary>The block's first and last ledger, when any of its chunks' ledgers is known.</summary>
    public bool TryBlockLedgers(int block, out LedgerRange range)
    {
        range = new LedgerRange(_first[block], _last[block]);
        return _first[block] != long.MaxValue;
    }

    /// <summary>Each block's first ledger, in block order: what the block list names.</summary>
    /// <exception cref="InvalidOperationException">A block has no known ledger.</exception>
    public IReadOnlyList<long> BlockFirstLedgers()
    {
        long[] firsts = new long[BlockCount];

        for (int block = 0; block < BlockCount; block++)
        {
            if (!TryBlockLedgers(block, out LedgerRange range))
            {
                throw new InvalidOperationException($"no ledger is known for block {block}, so the block list cannot name where it starts");
            }

            firsts[block] = range.First;
        }

        return firsts;
    }

    /// <summary>The first ledger of any chunk and the last ledger of any chunk.</summary>
    public LedgerRange Window()
    {
        IReadOnlyList<long> firsts = BlockFirstLedgers();
        return new LedgerRange(firsts.Min(), _last.Max());
    }

    /// <summary>Records a chunk's first copy in canonical order; false, and nothing changed, when it had already landed.</summary>
    internal bool Land(long chunk, long ledgerIndex)
    {
        if (!MarkLanded(chunk)) return false;

        Widen(FileTree.BlockOf(chunk), LedgerRange.Of(ledgerIndex));
        return true;
    }

    /// <summary>Records that a chunk landed without its ledger; the strategy reports its block's range instead.</summary>
    internal bool MarkLanded(long chunk)
    {
        Check(chunk);

        ulong bit = 1UL << (int)(chunk & 63);
        ref ulong word = ref _landed[chunk >> 6];
        if ((word & bit) != 0) return false;

        word |= bit;
        _count[FileTree.BlockOf(chunk)]++;
        Landed++;
        return true;
    }

    internal void Widen(int block, LedgerRange range)
    {
        _first[block] = Math.Min(_first[block], range.First);
        _last[block] = Math.Max(_last[block], range.Last);
    }

    private void Check(long chunk)
    {
        if (chunk < 0 || chunk >= ChunkCount) throw new ArgumentOutOfRangeException(nameof(chunk), chunk, $"the file has {ChunkCount} chunks");
    }
}
