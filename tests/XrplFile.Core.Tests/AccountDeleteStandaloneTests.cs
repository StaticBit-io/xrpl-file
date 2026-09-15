using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// The proof against a live node, for <see cref="ThrowawayAccount.DeleteAsync"/>: everything up
/// to this point is checked against a fake ledger that answers whatever a test put in it. This is
/// the one that proves rippled agrees with the sequence-gate the previous task built.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class AccountDeleteStandaloneTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "xrpl-file-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    [Fact]
    public async Task A_throwaway_account_publishes_a_file_and_then_gives_its_reserve_back()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);

            XrplWallet home = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);
            XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);

            ITransactionSigner signer = new WalletSigner(writer);
            ThrowawayAccount throwaway = new(sink, source, signer);

            // A small file, published with SequenceStrategy rather than tickets: the sequence this
            // publication spends is exactly the number the deletion threshold has to be measured
            // from, and SequenceStrategy leaves nothing else owned behind for AccountDelete to
            // trip on.
            byte[] file = VectorStream.Bytes("account-delete-standalone", 1_500);

            SessionOptions options = new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };

            PublicationEstimate priced = await PublicationSession.EstimateAsync(
                source, writer.ClassicAddress, file.Length, options, token);

            await using SequenceStrategy strategy = new(
                sink, signer, new SequenceStrategyOptions { FeeDrops = (int)priced.FeeDropsPerTransaction });

            PublicationSession session = await PublicationSession.StartAsync(
                strategy, source, file, "application/octet-stream", "account-delete.bin",
                writer.ClassicAddress, "key-account-delete", "1", options, token);

            PublicationReport report = await session.RunAsync(null, token);

            ReadResult read = await new XrplFileReader(source).ReadAsync(report.Link, token);
            Assert.Equal(file, read.Bytes);

            long homeBefore = await source.BalanceDropsAsync(home.ClassicAddress, token);

            // The account's sequence starts at the ledger that created it, and the publication
            // above already moved it further. The threshold has to be computed after publishing,
            // not before, or the ledgers driven forward below would land short of the number the
            // publication itself pushed the threshold to. The stand's sidecar closes one every few
            // seconds; this drives them as fast as the node will take them.
            long deletableFrom = await throwaway.DeletableFromLedgerAsync(token);
            await StandaloneFixture.CloseLedgersUntilAsync(client, deletableFrom, token);

            DeletionResult result = await throwaway.DeleteAsync(home.ClassicAddress, token);

            Assert.Equal(DeletionOutcome.Submitted, result.Outcome);
            Assert.True(result.ArrivingDrops > 0);

            // The deletion has to reach a validated ledger before either side of it is true.
            await StandaloneFixture.CloseLedgersUntilAsync(
                client, await StandaloneFixture.CurrentLedgerAsync(client, token) + 2, token);

            Assert.Equal(AccountState.Absent, await source.AccountStateAsync(writer.ClassicAddress, token));
            Assert.Equal(homeBefore + result.ArrivingDrops, await source.BalanceDropsAsync(home.ClassicAddress, token));
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_deletion_to_a_missing_destination_is_refused_without_spending_the_reserve()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);

            XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);
            XrplWallet missing = XrplWallet.Generate();

            ThrowawayAccount throwaway = new(sink, source, new WalletSigner(writer));

            // The refusal this test is about is DestinationMissing, not NotYet -- so the sequence
            // gate has to be cleared first, the same way the successful deletion above clears it,
            // or every attempt below would be refused for the wrong reason.
            long deletableFrom = await throwaway.DeletableFromLedgerAsync(token);
            await StandaloneFixture.CloseLedgersUntilAsync(client, deletableFrom, token);

            long before = await source.BalanceDropsAsync(writer.ClassicAddress, token);

            DeletionResult result = await throwaway.DeleteAsync(missing.ClassicAddress, token);

            Assert.Equal(DeletionOutcome.DestinationMissing, result.Outcome);
            Assert.Equal(string.Empty, result.TxHash);

            // The point of the whole preflight: nothing was submitted, so nothing was spent. A
            // tec* result would have taken an owner reserve out of this balance.
            Assert.Equal(before, await source.BalanceDropsAsync(writer.ClassicAddress, token));
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
