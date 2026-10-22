namespace XrplFile.Tests;

/// <summary>Holds its <paramref name="gateAt"/>-th signature, signed and not yet returned, until released.</summary>
internal sealed class GatedSigner(ITransactionSigner inner, int gateAt) : ITransactionSigner
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    public Task Reached => _reached.Task;

    /// <summary>The hash of the signature held at the gate.</summary>
    public string? GatedHash { get; private set; }

    public string Account => inner.Account;

    public string PublicKey => inner.PublicKey;

    public void Release() => _release.TrySetResult();

    public async Task<SignedTransaction> SignAsync(IDictionary<string, object> transaction, CancellationToken cancellationToken)
    {
        SignedTransaction signed = await inner.SignAsync(transaction, cancellationToken);

        if (Interlocked.Increment(ref _count) == gateAt)
        {
            GatedHash = signed.Hash;
            _reached.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        return signed;
    }
}
