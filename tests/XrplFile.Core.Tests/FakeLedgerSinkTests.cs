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

    private static Dictionary<string, object> Ride(uint ticket) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = Wallet.ClassicAddress,
        ["Sequence"] = 0u,
        ["TicketSequence"] = ticket,
        ["Fee"] = "10",
        ["SigningPubKey"] = Wallet.PublicKey,
    };

    private static Dictionary<string, object> Mint(uint sequence, uint count) => new()
    {
        ["TransactionType"] = "TicketCreate",
        ["Account"] = Wallet.ClassicAddress,
        ["Sequence"] = sequence,
        ["TicketCount"] = count,
        ["Fee"] = "10",
        ["SigningPubKey"] = Wallet.PublicKey,
    };

    [Fact]
    public async Task A_ticket_that_was_already_ridden_is_answered_as_gone_the_way_a_node_answers_it()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        await sink.SubmitAsync(Mint(10, 2), Signer, TestContext.Current.CancellationToken);
        SubmitOutcome first = await sink.SubmitAsync(Ride(11), Signer, TestContext.Current.CancellationToken);
        SubmitOutcome second = await sink.SubmitAsync(Ride(11), Signer, TestContext.Current.CancellationToken);

        // Measured on xrpld 3.3.0: "Ticket is not in ledger". terPRE_TICKET here would send a
        // strategy down its "the mint was not awaited" path for a ticket that is simply spent.
        Assert.Equal("tesSUCCESS", first.EngineResult);
        Assert.Equal("tefNO_TICKET", second.EngineResult);
    }

    [Fact]
    public async Task A_ticket_above_the_accounts_sequence_does_not_exist_yet()
    {
        FakeLedgerSink sink = new(new FakeLedgerSource());

        SubmitOutcome outcome = await sink.SubmitAsync(Ride(5_000), Signer, TestContext.Current.CancellationToken);

        Assert.Equal("terPRE_TICKET", outcome.EngineResult);
    }

    [Fact]
    public async Task Live_tickets_are_listed_with_the_mint_that_made_them()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        SubmitOutcome mint = await sink.SubmitAsync(Mint(10, 2), Signer, TestContext.Current.CancellationToken);
        sink.LeakTickets(Wallet.ClassicAddress, 1);

        IReadOnlyList<LiveTicket> tickets = await ledger.LiveTicketsAsync(Wallet.ClassicAddress, TestContext.Current.CancellationToken);

        Assert.Equal(
            [new LiveTicket(11, mint.TxHash), new LiveTicket(12, mint.TxHash), new LiveTicket(1_000_000, FakeLedgerSink.UnknownCreator)],
            tickets);
    }

    [Fact]
    public async Task A_ticket_answered_as_gone_is_still_listed_until_its_consumer_validates()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);

        await sink.SubmitAsync(Mint(10, 1), Signer, TestContext.Current.CancellationToken);
        sink.AnswersGone.Add(11);

        SubmitOutcome outcome = await sink.SubmitAsync(Ride(11), Signer, TestContext.Current.CancellationToken);

        Assert.Equal("tefNO_TICKET", outcome.EngineResult);
        Assert.Contains(await ledger.LiveTicketsAsync(Wallet.ClassicAddress, TestContext.Current.CancellationToken), ticket => ticket.Sequence == 11);
    }
}
