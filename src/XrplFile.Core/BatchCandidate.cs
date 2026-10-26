using System.Buffers;

namespace XrplFile;

/// <summary>
/// A signed transaction that carries memos (spec B13): an outer batch, or the writer's solo. However
/// often its bytes are sent, it is one candidate, and it can charge one fee.
/// </summary>
internal sealed class BatchCandidate(
    int ordinal, bool isOuter, string account, uint sequence, uint lastLedger, long feeDrops,
    IReadOnlyList<int> memos, SignedTransaction signed)
{
    /// <summary>The ordinal of a round's solo memo; batches count from zero.</summary>
    public const int SoloOrdinal = -1;

    private static readonly SearchValues<char> UpperHex = SearchValues.Create("0123456789ABCDEF");

    private volatile bool _resolved;
    private int _exhausted;

    public int Ordinal { get; } = ordinal;

    public bool IsOuter { get; } = isOuter;

    /// <summary>Whose sequence it rides: the payer for an outer, the writer for a solo.</summary>
    public string Account { get; } = account;

    public uint Sequence { get; } = sequence;

    public uint LastLedger { get; } = lastLedger;

    public long FeeDrops { get; } = feeDrops;

    /// <summary>The call's memo indices it carries.</summary>
    public IReadOnlyList<int> Memos { get; } = memos;

    /// <summary>
    /// The signed blob as bytes: a round holds every blob it signed until it settles, and as UTF-16
    /// hex an eight-memo blob takes 34 KB, four times its bytes; two thousand of them were most of a
    /// round's memory (spec S27). A blob that would not come back as the same text
    /// stays text, so what is sent is exactly what the signer returned.
    /// </summary>
    private readonly object _blob = Compact(signed.TxBlob);

    public string Hash { get; } = signed.Hash;

    /// <summary>The blob to send, as the signer returned it.</summary>
    public string TxBlob => _blob as string ?? Convert.ToHexString((byte[])_blob);

    /// <summary>Set by settlement: nothing this candidate is can apply any more, so it is not sent again.</summary>
    public bool IsResolved => _resolved;

    public void MarkResolved() => _resolved = true;

    /// <summary>
    /// Every transmission of its last series came back busy or unanswered, so nothing is sending it
    /// any more (spec submit-answer table). For a Batch that is usually a full open ledger: a Batch is
    /// never queued, and the node answers <c>telCAN_NOT_QUEUE</c>.
    /// </summary>
    public bool IsExhausted => Volatile.Read(ref _exhausted) == 1;

    public void MarkExhausted() => Volatile.Write(ref _exhausted, 1);

    /// <summary>Claims an exhausted candidate for a new series of transmissions; false when one is already sending it.</summary>
    public bool TryResume() => Interlocked.CompareExchange(ref _exhausted, 0, 1) == 1;

    /// <summary>
    /// Resolved once the snapshot's ledger is past its deadline, or its account's sequence is past its
    /// own (spec B9). Either way it can no longer apply; whether its memos landed is the sweep's call.
    /// </summary>
    public bool IsResolvedBy(LedgerSnapshot snapshot) =>
        snapshot.LedgerIndex > LastLedger || snapshot.SequenceOf(Account) > Sequence;

    /// <summary>Upper-case hex, the only text <see cref="Convert.ToHexString(byte[])"/> gives back, as bytes; anything else as it is.</summary>
    private static object Compact(string blob) =>
        blob.Length % 2 != 0 || blob.AsSpan().ContainsAnyExcept(UpperHex) ? blob : Convert.FromHexString(blob);
}
