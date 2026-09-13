using Xrpl.Wallet;
using XrplFile;
using Xunit;

namespace XrplFile.Tests;

public class TransactionSignerTests
{
    [Fact]
    public void A_wallet_signer_reports_the_wallet_it_wraps()
    {
        XrplWallet wallet = XrplWallet.Generate();
        WalletSigner signer = new(wallet);

        Assert.Equal(wallet.ClassicAddress, signer.Account);
        Assert.Equal(wallet.PublicKey, signer.PublicKey);
    }

    [Fact]
    public async Task A_wallet_signer_produces_the_blob_the_wallet_would_have()
    {
        XrplWallet wallet = XrplWallet.Generate();
        WalletSigner signer = new(wallet);

        Dictionary<string, object> transaction = new()
        {
            ["TransactionType"] = "AccountSet",
            ["Account"] = wallet.ClassicAddress,
            ["Fee"] = "12",
            ["Sequence"] = 1u,
            ["LastLedgerSequence"] = 100u,
            ["SigningPubKey"] = wallet.PublicKey,
        };

        SignedTransaction signed = await signer.SignAsync(transaction, TestContext.Current.CancellationToken);

        SignatureResult direct = wallet.Sign(transaction);
        Assert.Equal(direct.TxBlob, signed.TxBlob);
        Assert.Equal(direct.Hash.ToUpperInvariant(), signed.Hash);
    }
}
