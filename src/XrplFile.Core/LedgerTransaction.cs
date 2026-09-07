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
    string? MemoFormatHex);
