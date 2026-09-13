using Xrpl.Wallet;

namespace XrplFile;

/// <summary>A signed transaction: the blob a node is given, and the hash it will be known by.</summary>
public sealed record SignedTransaction(string TxBlob, string Hash);

/// <summary>
/// Whatever can turn a prepared transaction into a signed blob.
///
/// The seam exists so that key material does not have to leave whatever is holding it. A caller
/// that holds an <see cref="XrplWallet"/> wraps it in <see cref="WalletSigner"/> and nothing
/// changes; a caller backed by a vault hands out no key at all, which is what makes locking that
/// vault mean something while a publication is running.
///
/// Signing is asynchronous although the SDK signs synchronously: an implementation that has to
/// take a lock, or ask something else, would otherwise be forced into sync-over-async on a path
/// that runs once per transaction — thousands of times in one publication.
/// </summary>
public interface ITransactionSigner
{
    /// <summary>The classic address every signature is made for.</summary>
    string Account { get; }

    /// <summary>The public key the transaction's <c>SigningPubKey</c> must carry.</summary>
    string PublicKey { get; }

    /// <summary>Signs the transaction and returns the blob with its hash in upper case.</summary>
    Task<SignedTransaction> SignAsync(
        IDictionary<string, object> transaction, CancellationToken cancellationToken);
}

/// <summary>The signer a caller that already holds a wallet uses. Holds the seed for its lifetime.</summary>
public sealed class WalletSigner(XrplWallet wallet) : ITransactionSigner
{
    private readonly XrplWallet _wallet = wallet;

    public string Account => _wallet.ClassicAddress;

    public string PublicKey => _wallet.PublicKey;

    public Task<SignedTransaction> SignAsync(
        IDictionary<string, object> transaction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SignatureResult signed = _wallet.Sign(new Dictionary<string, object>(transaction));
        return Task.FromResult(new SignedTransaction(signed.TxBlob, signed.Hash.ToUpperInvariant()));
    }
}
