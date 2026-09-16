using System.Globalization;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public sealed class ThrowawayAccountDeletionTests
{
    // Real, codec-valid classic addresses rather than bare strings: DeleteAsync signs through
    // WalletSigner, the same double the rest of this test class's siblings use, and the binary
    // codec has to accept both Account and Destination for a signature to happen at all. A
    // regression that wrote an unencodable field must fail these tests rather than sail through a
    // fake that swallows what a real node would never see.
    private static readonly XrplWallet WriterWallet = XrplWallet.Generate();
    private static readonly string Writer = WriterWallet.ClassicAddress;
    private static readonly ITransactionSigner Signer = new WalletSigner(WriterWallet);
    private static readonly string Home = XrplWallet.Generate().ClassicAddress;

    private static (ThrowawayAccount Account, FakeLedgerSource Ledger, FakeLedgerSink Sink) Ready(
        long balanceDrops = 30_000_000,
        long currentLedger = 5_000,
        uint sequence = 100,
        bool destinationExists = true,
        uint? validatedSequence = null)
    {
        FakeLedgerSource ledger = new() { CurrentLedger = currentLedger };
        ledger.Balances[Writer] = balanceDrops;

        if (destinationExists)
        {
            ledger.Destinations[Home] = new DestinationState(Exists: true, RequiresDestinationTag: false, AuthorisesDepositors: false);
        }

        FakeLedgerSink sink = new(ledger)
        {
            // Every test in this class either funds Writer (the default 30,000,000 above) or sets
            // its own balanceDrops explicitly, so this is safe here and makes every one of them
            // pin, for free, that PreviewDeletionAsync never asks this account's sequence before it
            // has checked the account can even cover the fee -- see the dedicated unfunded test
            // below for the account this is actually guarding.
            ThrowForUnfundedAccounts = true,
        };
        sink.SetSequence(sequence);
        if (validatedSequence is not null) sink.SetValidatedSequence(validatedSequence);

        ThrowawayAccount account = new(sink, ledger, Signer);
        return (account, ledger, sink);
    }

    [Fact]
    public async Task A_deletion_with_no_destination_is_refused_before_anything_is_read()
    {
        (ThrowawayAccount account, _, _) = Ready();

        await Assert.ThrowsAsync<ArgumentException>(
            () => account.DeleteAsync("   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_preview_below_the_threshold_says_not_yet_and_names_the_ledger()
    {
        // Sequence 100 needs the validated index at 354 before rippled will allow the deletion.
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready(currentLedger: 300, sequence: 100);

        DeletionResult preview = await account.PreviewDeletionAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.NotYet, preview.Outcome);
        Assert.Equal(354, preview.DeletableFromLedger);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task The_gate_uses_the_sequence_the_transaction_will_carry_not_the_validated_one()
    {
        // A validated sequence that lags the unvalidated one -- exactly what an already-applied or
        // in-flight transaction leaves behind on a real node. Validated (100) clears the threshold
        // against ledger 400 (100 + 254 = 354); the unvalidated sequence DeleteAsync will actually
        // sign with (200) does not (200 + 254 = 454). A gate that read the validated number would
        // answer Ready and submit here -- rippled would refuse the resulting transaction with
        // tecTOO_SOON and still claim its owner reserve.
        (ThrowawayAccount account, _, FakeLedgerSink sink) =
            Ready(currentLedger: 400, sequence: 200, validatedSequence: 100);

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.NotYet, result.Outcome);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_deletion_below_the_threshold_submits_nothing()
    {
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready(currentLedger: 300, sequence: 100);

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.NotYet, result.Outcome);
        Assert.Empty(sink.Submitted);
        Assert.Equal(string.Empty, result.TxHash);
    }

    [Fact]
    public async Task A_destination_the_ledger_does_not_carry_submits_nothing()
    {
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready(destinationExists: false);

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.DestinationMissing, result.Outcome);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_destination_that_wants_a_tag_submits_nothing()
    {
        (ThrowawayAccount account, FakeLedgerSource ledger, FakeLedgerSink sink) = Ready();
        ledger.Destinations[Home] = new DestinationState(Exists: true, RequiresDestinationTag: true, AuthorisesDepositors: false);

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.DestinationRefuses, result.Outcome);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_preview_that_can_proceed_says_ready_and_prices_the_fee_as_one_owner_reserve()
    {
        (ThrowawayAccount account, FakeLedgerSource ledger, FakeLedgerSink sink) = Ready();

        DeletionResult preview = await account.PreviewDeletionAsync(Home, TestContext.Current.CancellationToken);

        NetworkFees fees = await ledger.FeesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeletionOutcome.Ready, preview.Outcome);
        Assert.Equal(fees.OwnerReserveDrops, preview.FeeDrops);
        Assert.Equal(30_000_000, preview.BalanceDrops);
        Assert.Equal(30_000_000 - fees.OwnerReserveDrops, preview.ArrivingDrops);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_deletion_that_can_proceed_submits_an_AccountDelete_carrying_the_destination()
    {
        (ThrowawayAccount account, FakeLedgerSource ledger, FakeLedgerSink sink) = Ready();

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.Submitted, result.Outcome);
        Dictionary<string, object> submitted = Assert.Single(sink.Submitted);
        Assert.Equal("AccountDelete", submitted["TransactionType"]);
        Assert.Equal(Writer, submitted["Account"]);
        Assert.Equal(Home, submitted["Destination"]);
        Assert.NotEqual(string.Empty, result.TxHash);

        // Pinned against the node's own fee rather than a literal: the fake's open-ledger fee (10)
        // and its owner reserve (200,000) are far enough apart that a regression writing the wrong
        // one into the transaction cannot pass this by accident.
        NetworkFees fees = await ledger.FeesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(fees.OwnerReserveDrops.ToString(CultureInfo.InvariantCulture), submitted["Fee"]);
    }

    [Fact]
    public async Task A_queued_deletion_says_it_is_queued_rather_than_that_the_account_is_gone()
    {
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready();
        sink.Refusals[0] = "terQUEUED";

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        // terQUEUED is not arrival: the queue can drop the submission, and until a ledger takes it
        // the account still exists and every drop is still on it.
        Assert.Equal(DeletionOutcome.Queued, result.Outcome);
        Assert.NotEqual(string.Empty, result.TxHash);
    }

    [Fact]
    public async Task A_deletion_the_node_refuses_is_not_reported_as_the_account_being_gone()
    {
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready();
        sink.Refusals[0] = "tecTOO_SOON";

        XrplFileException refused = await Assert.ThrowsAsync<XrplFileException>(
            () => account.DeleteAsync(Home, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, refused.Error);
    }

    [Fact]
    public async Task An_account_that_cannot_cover_the_fee_is_refused_without_submitting()
    {
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready(balanceDrops: 1);

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.NotYet, result.Outcome);
        Assert.Empty(sink.Submitted);

        // The sequence rule is not what is refusing here -- the account is well past its
        // threshold -- so naming a ledger would point at a moment that is not the actual reason
        // for the wait.
        Assert.Null(result.DeletableFromLedger);
    }

    [Fact]
    public async Task An_account_nobody_has_funded_is_refused_without_asking_the_node_for_its_sequence()
    {
        // Sequence 100 clears the threshold against ledger 5,000 -- if this test reached the
        // sequence read at all, it would say Ready, not NotYet. Instead the balance gate must stop
        // it before that: a zero balance is the state a freshly generated key is in until somebody
        // funds it, which is exactly when the Accounts tab's Check button gets pressed for real.
        // Ready's own sink already asks the node for the sequence of any account it does not
        // recognise as funded (ThrowForUnfundedAccounts), so if PreviewDeletionAsync ever regressed
        // to read the sequence before checking the balance, this test would fail with the
        // RippledException a real node's actNotFound produces, not with a wrong Outcome.
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready(balanceDrops: 0);

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.NotYet, result.Outcome);
        Assert.Null(result.DeletableFromLedger);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task Closing_an_account_to_itself_is_refused_without_submitting()
    {
        // The candidate list the closing screen offers already excludes the account being closed,
        // but nothing stops a typed address from being that same account -- an address that exists,
        // carries no awkward flags, and would otherwise sail through every check here to Ready. The
        // node would answer temDST_IS_SRC, a tem* that never applies and never claims the fee.
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready();

        DeletionResult result = await account.DeleteAsync(Writer, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.DestinationRefuses, result.Outcome);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_refusal_that_never_applied_carries_the_node_s_engine_result_on_the_exception()
    {
        // temDST_IS_SRC, tefPAST_SEQ, telINSUF_FEE_P and every ter* result never reach the ledger
        // and never claim the fee -- unlike a tec* result, which does. A caller deciding whether to
        // say "the attempt still claimed the fee" needs the actual code, not just the fact of a
        // refusal.
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready();
        sink.Refusals[0] = "telINSUF_FEE_P";

        XrplFileException refused = await Assert.ThrowsAsync<XrplFileException>(
            () => account.DeleteAsync(Home, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, refused.Error);
        Assert.Equal("telINSUF_FEE_P", refused.EngineResult);
    }

    [Fact]
    public async Task A_deletion_signs_with_exactly_the_sequence_its_own_preflight_measured_against()
    {
        // A sink that answers a DIFFERENT sequence on a second, independent read: if DeleteAsync
        // still asked the sink a second time after its own internal preview had already measured
        // 100 against the threshold, it would sign with 999 instead -- a value the preview never
        // actually checked. The fix is that DeleteAsync's transaction carries the same sequence its
        // own preview read, not a fresh one.
        FakeLedgerSource ledger = new() { CurrentLedger = 5_000 };
        ledger.Balances[Writer] = 30_000_000;
        ledger.Destinations[Home] = new DestinationState(Exists: true, RequiresDestinationTag: false, AuthorisesDepositors: false);

        FakeLedgerSink inner = new(ledger) { ThrowForUnfundedAccounts = true };
        DriftingSequenceSink sink = new(inner, first: 100, thereafter: 999);
        ThrowawayAccount account = new(sink, ledger, Signer);

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.Submitted, result.Outcome);
        Dictionary<string, object> submitted = Assert.Single(inner.Submitted);
        Assert.Equal(100u, Convert.ToUInt32(submitted["Sequence"], CultureInfo.InvariantCulture));
    }

    /// <summary>Answers a caller-chosen sequence on its first unvalidated read, and a different one on every read after that.</summary>
    private sealed class DriftingSequenceSink(ILedgerSink inner, uint first, uint thereafter) : ILedgerSink
    {
        private int _unvalidatedReads;

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken)
        {
            if (!validated)
            {
                _unvalidatedReads++;
                return Task.FromResult(_unvalidatedReads == 1 ? first : thereafter);
            }

            return inner.SequenceAsync(account, validated, cancellationToken);
        }

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
            inner.SubmitAsync(transaction, signer, cancellationToken);
    }
}
