using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// <see cref="FakeLedgerSink"/> signs through the signer it is handed, at the point a real sink
/// signs, so that a strategy which stopped passing its signer through would be caught here rather
/// than only against a real node. This pins the other half of that promise: what the real binary
/// codec refuses must reach the caller as a failed submission, not as a submission the fake waved
/// through and only noted on the side.
/// </summary>
public class FakeLedgerSinkTests
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);

    private static Dictionary<string, object> Transaction(string destination) => new()
    {
        ["TransactionType"] = "Payment",
        ["Account"] = Wallet.ClassicAddress,
        ["Destination"] = destination,
        ["Amount"] = "1000000",
        ["Sequence"] = 1u,
        ["Fee"] = "10",
        ["SigningPubKey"] = Wallet.PublicKey,
    };

    [Fact]
    public async Task A_transaction_the_codec_cannot_encode_fails_the_submission()
    {
        // "rBack" is not a classic address, and the real binary codec — which SignAsync drives on
        // its way to a signature — refuses it. If the fake swallowed that refusal and submitted
        // anyway, this is exactly the case where a regressed caller (an integer Amount, a dropped
        // SigningPubKey, an unencodable field of any kind) would sail through a green suite while
        // a real node never saw the transaction at all.
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        await Assert.ThrowsAsync<Xrpl.BinaryCodec.InvalidJsonException>(
            () => sink.SubmitAsync(Transaction("rBack"), Signer, TestContext.Current.CancellationToken));

        // Nothing was signed, so nothing reached the ledger and nothing was counted as a
        // signature taken.
        Assert.Empty(sink.Submitted);
        Assert.Equal(0, sink.Signatures);
    }
}
