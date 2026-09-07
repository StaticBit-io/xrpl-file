namespace XrplFile;

/// <summary>One memo on its way into the ledger: what a transaction will carry.</summary>
public sealed record MemoSubmission(string MemoTypeHex, string MemoDataHex, string? MemoFormatHex);

/// <summary>Where a memo landed: its position in the batch, and the transaction that carried it.</summary>
public sealed record SubmittedMemo(int Index, string TxHash);

/// <summary>
/// How a set of memos reaches the ledger.
///
/// The contract is deliberately strong: when the call returns, every memo is in a validated
/// ledger and its transaction hash is known. How that was achieved — one at a time on the
/// account's sequence, or many at once on tickets — is the implementation's business and
/// nobody else's. A strategy holds state between calls, which is why it is disposable: a
/// ticketed one keeps unspent tickets so a later phase does not mint its own.
/// </summary>
public interface ISubmissionStrategy : IAsyncDisposable
{
    Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken);
}
