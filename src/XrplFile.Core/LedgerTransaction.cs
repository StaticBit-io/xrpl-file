namespace XrplFile;

/// <summary>One transaction as the reader needs it. Only the first memo is read: a chunk carries exactly one.</summary>
public sealed record LedgerTransaction(
    string Hash,
    long LedgerIndex,
    string Account,
    bool Succeeded,
    bool Validated,
    string? MemoTypeHex,
    string? MemoDataHex,
    string? MemoFormatHex);
