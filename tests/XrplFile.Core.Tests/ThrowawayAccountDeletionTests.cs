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
        bool destinationExists = true)
    {
        FakeLedgerSource ledger = new() { CurrentLedger = currentLedger };
        ledger.Balances[Writer] = balanceDrops;

        if (destinationExists)
        {
            ledger.Destinations[Home] = new DestinationState(Exists: true, RequiresDestinationTag: false, AuthorisesDepositors: false);
        }

        FakeLedgerSink sink = new(ledger);
        sink.SetSequence(Writer, sequence);

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
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready();

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.Submitted, result.Outcome);
        Dictionary<string, object> submitted = Assert.Single(sink.Submitted);
        Assert.Equal("AccountDelete", submitted["TransactionType"]);
        Assert.Equal(Writer, submitted["Account"]);
        Assert.Equal(Home, submitted["Destination"]);
        Assert.NotEqual(string.Empty, result.TxHash);
    }

    [Fact]
    public async Task An_account_that_cannot_cover_the_fee_is_refused_without_submitting()
    {
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready(balanceDrops: 1);

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.NotYet, result.Outcome);
        Assert.Empty(sink.Submitted);
    }
}
