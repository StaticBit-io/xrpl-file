namespace XrplFile;

/// <summary>Everything a round shares with the strategy that runs it, for the strategy's lifetime.</summary>
internal sealed record BatchContext(
    ILedgerSink Sink,
    ILedgerSource Source,
    ITransactionSigner Writer,
    ITransactionSigner Payer,
    BatchBudget Budget,
    BatchStrategyOptions Options,
    BatchFence Fence,
    BatchWindow Window,
    BatchTrace Trace,
    BatchSpending Spending);

/// <summary>An inner that landed with tesSUCCESS: its hash and the ledger it is in.</summary>
internal sealed record LandedInner(string Hash, long LedgerIndex);

/// <summary>What one round found: the memos it landed, by call index, and what the payer paid for the outers that applied (spec B29).</summary>
internal sealed record RoundResult(IReadOnlyDictionary<int, LandedInner> Landed, long PayerFeeDrops, bool PayerFeeKnown)
{
    public static RoundResult Empty { get; } = new(new Dictionary<int, LandedInner>(), 0, true);
}
