namespace XrplFile;

/// <summary>
/// One transaction as the reader needs it. Only the first memo is read: a chunk carries exactly
/// one. <see cref="Result"/> is <c>meta.TransactionResult</c> verbatim when the node reported
/// one — null when it did not, which happens for a transaction the node holds but has not yet
/// validated. <see cref="Succeeded"/> keeps its existing meaning (<c>Result == "tesSUCCESS"</c>)
/// for every caller that only cares about pass/fail; <see cref="Result"/> exists for the callers
/// that need to say which code it was.
/// </summary>
public sealed record LedgerTransaction(
    string Hash,
    long LedgerIndex,
    string Account,
    bool Succeeded,
    bool Validated,
    string? Result,
    string? MemoTypeHex,
    string? MemoDataHex,
    string? MemoFormatHex)
{
    /// <summary>The transaction's type as the node names it, or null when the record does not say.</summary>
    public string? TransactionType { get; init; }

    /// <summary>The sequence it consumed; zero for a transaction that rode a ticket (spec B30).</summary>
    public uint Sequence { get; init; }

    /// <summary>The ticket it rode, or null.</summary>
    public uint? TicketSequence { get; init; }

    /// <summary>How many tickets a <c>TicketCreate</c> asked for, or null for anything else.</summary>
    public uint? TicketCount { get; init; }

    /// <summary>The fee it carried, in drops: "0" for a Batch inner. Null when the record does not say.</summary>
    public long? Fee { get; init; }

    /// <summary>The outer Batch an inner belongs to, upper case; null for anything that is not an inner (spec B12).</summary>
    public string? ParentBatchID { get; init; }
}
