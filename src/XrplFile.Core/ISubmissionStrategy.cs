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
/// nobody else's. A strategy is disposable because a ticketed one may hold tickets that were
/// minted and never spent — a submission handed back its ticket, or the call was cut short — and
/// disposing does not reclaim them: they stay live on the account, each locking an owner reserve,
/// until something spends them or the account is deleted.
/// </summary>
public interface ISubmissionStrategy : IAsyncDisposable
{
    Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken);
}
