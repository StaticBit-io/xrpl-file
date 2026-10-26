using System.Diagnostics.CodeAnalysis;

namespace XrplFile;

/// <summary>
/// What a strategy hands back for one call (spec S23): which memos landed, and the facts it kept
/// about them. Each <c>Try</c> method is false when the strategy did not keep that fact. Hashes are
/// kept for every list and manifest memo, and for every memo on the ticket and sequence paths, all
/// of them bounded; a Batch chunk pass keeps none (spec S19) and reports each file block's ledger
/// range instead.
/// </summary>
public sealed class SubmittedMemos
{
    private readonly IReadOnlyDictionary<int, string> _hashes;
    private readonly IReadOnlyDictionary<int, long> _ledgers;

    internal SubmittedMemos(
        LandedSet landed, IReadOnlyDictionary<int, string> hashes, IReadOnlyDictionary<int, long> ledgers,
        IReadOnlyDictionary<int, LedgerRange> blockLedgers)
    {
        Landed = landed;
        _hashes = hashes;
        _ledgers = ledgers;
        BlockLedgers = blockLedgers;
    }

    /// <summary>How many memos the call carried.</summary>
    public int Count => Landed.Capacity;

    public LandedSet Landed { get; }

    /// <summary>Per file block, the ledgers of the chunk-marked memos of this call whose ledger the strategy knew.</summary>
    public IReadOnlyDictionary<int, LedgerRange> BlockLedgers { get; }

    public bool TryLedgerOf(int index, out long ledgerIndex) => _ledgers.TryGetValue(index, out ledgerIndex);

    public bool TryHashOf(int index, [NotNullWhen(true)] out string? hash) => _hashes.TryGetValue(index, out hash);
}

/// <summary>How a strategy assembles its <see cref="SubmittedMemos"/>: the first record it confirms for a memo wins.</summary>
internal sealed class SubmittedMemosBuilder(int count, bool keepChunkFacts)
{
    private readonly Dictionary<int, string> _hashes = [];
    private readonly Dictionary<int, long> _ledgers = [];
    private readonly Dictionary<int, LedgerRange> _blocks = [];

    public LandedSet Landed { get; } = new(count);

    public void Record(int index, string memoTypeHex, string hash, long? ledgerIndex)
    {
        if (!Landed.Add(index)) return;

        long? chunk = MemoLayout.ChunkIndexOf(memoTypeHex);

        if (chunk is { } c && ledgerIndex is { } ledger)
        {
            int block = FileTree.BlockOf(c);
            _blocks[block] = _blocks.TryGetValue(block, out LedgerRange range) ? range.Including(ledger) : LedgerRange.Of(ledger);
        }

        if (chunk is not null && !keepChunkFacts) return;

        _hashes[index] = hash;
        if (ledgerIndex is { } known) _ledgers[index] = known;
    }

    public SubmittedMemos Build() => new(Landed, _hashes, _ledgers, _blocks);
}
