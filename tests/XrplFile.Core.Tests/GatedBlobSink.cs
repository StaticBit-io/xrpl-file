namespace XrplFile.Tests;

/// <summary>Holds its <paramref name="gateAt"/>-th blob submission in flight until released: registered, sent, not yet seen by the node.</summary>
internal sealed class GatedBlobSink(ILedgerSink inner, int gateAt) : ILedgerSink
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    public Task Reached => _reached.Task;

    /// <summary>The blob held at the gate.</summary>
    public string? Gated { get; private set; }

    public void Release() => _release.TrySetResult();

    public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
        inner.SequenceAsync(account, validated, cancellationToken);

    public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
        inner.SubmitAsync(transaction, signer, cancellationToken);

    public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _count) == gateAt)
        {
            Gated = txBlob;
            _reached.TrySetResult();
            await _release.Task;
        }

        return await inner.SubmitBlobAsync(txBlob, cancellationToken);
    }
}
