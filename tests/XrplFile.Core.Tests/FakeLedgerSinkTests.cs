using Xrpl.Client.Exceptions;
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

    [Fact]
    public async Task With_ThrowForUnfundedAccounts_an_account_nobody_has_funded_refuses_a_sequence_read()
    {
        // A real node's account_info answers actNotFound for an account it does not carry, and
        // this is the shape XrplClientLedgerSink.SequenceAsync lets that escape as: a
        // RippledException nothing in that class catches. Off by default (see the property's own
        // remarks on why), but a test that turns it on must see exactly this.
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger) { ThrowForUnfundedAccounts = true };

        Xrpl.Client.Exceptions.RippledException error = await Assert.ThrowsAsync<Xrpl.Client.Exceptions.RippledException>(
            () => sink.SequenceAsync(Wallet.ClassicAddress, validated: false, TestContext.Current.CancellationToken));

        Assert.Equal("actNotFound", error.Classify().RawError);
    }

    [Fact]
    public async Task With_ThrowForUnfundedAccounts_a_balance_of_zero_still_refuses_a_sequence_read()
    {
        // Zero drops is this fake's own convention for "does not exist" everywhere else
        // (AccountState.Absent), and an explicit zero-balance entry must mean the same thing here
        // as no entry at all.
        FakeLedgerSource ledger = new();
        ledger.Balances[Wallet.ClassicAddress] = 0;
        FakeLedgerSink sink = new(ledger) { ThrowForUnfundedAccounts = true };

        await Assert.ThrowsAsync<Xrpl.Client.Exceptions.RippledException>(
            () => sink.SequenceAsync(Wallet.ClassicAddress, validated: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_ThrowForUnfundedAccounts_an_unfunded_account_still_answers_a_sequence()
    {
        // The default: every other test in this repository that signs through an account it never
        // bothered to fund relies on this being lenient unless a test opts into the stricter check.
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        uint sequence = await sink.SequenceAsync(Wallet.ClassicAddress, validated: false, TestContext.Current.CancellationToken);

        Assert.Equal(10u, sequence);
    }

    [Fact]
    public async Task With_ThrowForUnfundedAccounts_a_funded_account_still_answers_a_sequence()
    {
        FakeLedgerSource ledger = new();
        ledger.Balances[Wallet.ClassicAddress] = 1;
        FakeLedgerSink sink = new(ledger) { ThrowForUnfundedAccounts = true };

        uint sequence = await sink.SequenceAsync(Wallet.ClassicAddress, validated: false, TestContext.Current.CancellationToken);

        Assert.Equal(10u, sequence);
    }
}
