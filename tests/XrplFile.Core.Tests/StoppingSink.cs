namespace XrplFile.Tests;

/// <summary>Forwards, and cancels the run once <paramref name="stopAfter"/> blobs have been transmitted — closing the window mid-run.</summary>
internal sealed class StoppingSink(ILedgerSink inner, CancellationTokenSource stop, int stopAfter) : ILedgerSink
{
    private int _count;

    public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) => inner.SequenceAsync(account, validated, cancellationToken);

    public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
        inner.SubmitAsync(transaction, signer, cancellationToken);

    public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
    {
        string answer = await inner.SubmitBlobAsync(txBlob, cancellationToken);
        if (Interlocked.Increment(ref _count) == stopAfter) await stop.CancelAsync();
        return answer;
    }
}
