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

    public SignedTransaction Signed { get; } = signed;

    public string Hash => Signed.Hash;

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
}
