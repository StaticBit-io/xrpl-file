using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>The throwaway mode end to end: funded from outside, published, and paid back.</summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class ThrowawayAccountStandaloneTests : IDisposable
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
    public async Task A_file_is_published_from_a_funded_throwaway_and_the_remainder_goes_home()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);

            XrplWallet funder = await StandaloneFixture.FundedAccountAsync(client, sink, 900_000_000, token);
            XrplWallet throwaway = XrplWallet.Generate();
            ThrowawayAccount account = new(sink, source, throwaway);

            byte[] file = VectorStream.Bytes("throwaway", 12_000);
            long startedIn = await source.CurrentLedgerAsync(token);

            PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
                source, throwaway.ClassicAddress, file.Length, ticketsPerBatch: 100, token);

            // The address goes on a screen; the money comes from wherever the person keeps it.
            FundingRequest request = await account.FundingAsync(estimate, token);
            Assert.False(request.Covered);
            Assert.Equal(throwaway.ClassicAddress, request.QrPayload);

            await StandaloneFixture.PayAsync(sink, funder, request.Address, request.RequiredDrops + 5_000_000, token);

            FundingRequest funded = await account.AwaitFundingAsync(estimate, TimeSpan.FromSeconds(2), token);
            Assert.True(funded.Covered);

            // The funder is read back from the ledger, and it is who actually paid.
            string? observed = await account.FunderAsync(startedIn, token);
            Assert.Equal(funder.ClassicAddress, observed);

            SessionOptions options = new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };

            // The session prices its own estimate from the node and refuses a strategy that would
            // charge anything else, so the strategy is built from the fee the estimate was read at.
            await using TicketStrategy strategy = new(
                sink, source, throwaway,
                new TicketStrategyOptions { TicketsPerBatch = 100, FeeDrops = (int)estimate.FeeDropsPerTransaction });

            PublicationSession session = await PublicationSession.StartAsync(
                strategy, source, file, "application/octet-stream", "throwaway.bin",
                throwaway.ClassicAddress, "key-throwaway", "1", options, token);

            PublicationReport report = await session.RunAsync(null, token);

            ReadResult read = await new XrplFileReader(source).ReadAsync(report.Link, token);
            Assert.Equal(file, read.Bytes);

            // What is left goes back to the address the funder was read from — because this test
            // passes that address in, not because anything inferred it.
            long before = await source.BalanceDropsAsync(funder.ClassicAddress, token);
            RefundResult refund = await account.RefundAsync(observed!, token);

            Assert.True(refund.SentDrops > 0, "the run should not have spent everything");
            Assert.Equal(funder.ClassicAddress, refund.Destination);

            await StandaloneFixture.WaitForBalanceAboveAsync(source, funder.ClassicAddress, before, token);

            // The base reserve is the price of the mode, and it is still on the dead account —
            // exactly the reserve, read from the node rather than assumed. A run that finished
            // spent every ticket it minted, so the account owns nothing and the reserve is the
            // whole floor; the aborted case, where it is not, is the fact below.
            NetworkFees fees = await source.FeesAsync(token);
            AccountState left = await source.AccountStateAsync(throwaway.ClassicAddress, token);
            Assert.Equal(0, left.OwnerCount);
            Assert.Equal(fees.BaseReserveDrops, left.BalanceDrops);
            Assert.Equal(fees.BaseReserveDrops, refund.LeftBehindDrops);
            Assert.Equal(RefundOutcome.Submitted, refund.Outcome);

            // And deletion is a long way off, because minting moved the sequence far ahead.
            long deletableFrom = await account.DeletableFromLedgerAsync(token);
            long now = await source.CurrentLedgerAsync(token);
            Assert.True(deletableFrom > now, $"deletion should still be out of reach at ledger {now}");
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_refund_after_an_aborted_run_clears_the_tickets_it_left_live()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);

            XrplWallet funder = await StandaloneFixture.FundedAccountAsync(client, sink, 900_000_000, token);
            XrplWallet throwaway = XrplWallet.Generate();
            ThrowawayAccount account = new(sink, source, throwaway);

            NetworkFees fees = await source.FeesAsync(token);
            await StandaloneFixture.PayAsync(sink, funder, throwaway.ClassicAddress, fees.BaseReserveDrops * 4, token);

            // What a run called off mid-batch leaves: tickets on the ledger that nothing will
            // ever spend. TicketStrategy's DisposeAsync drops its queue and the objects stay,
            // one owner reserve each, which is the whole point of this test.
            const uint Tickets = 5;
            await StandaloneFixture.MintTicketsAsync(sink, source, throwaway, Tickets, fees.OpenLedgerFeeDrops, token);

            AccountState stranded = await source.AccountStateAsync(throwaway.ClassicAddress, token);
            Assert.Equal((int)Tickets, stranded.OwnerCount);

            // A refund priced against the base reserve alone is more than the account may spend:
            // the node answers tecUNFUNDED_PAYMENT, claims the fee, and answers the same to every
            // retry until the remainder is gone.
            long before = await source.BalanceDropsAsync(funder.ClassicAddress, token);
            RefundResult refund = await account.RefundAsync(funder.ClassicAddress, token);

            Assert.Equal(RefundOutcome.Submitted, refund.Outcome);
            Assert.True(refund.SentDrops > 0, "there should have been something above the reserves to send");

            await StandaloneFixture.WaitForBalanceAboveAsync(source, funder.ClassicAddress, before, token);

            long floor = fees.BaseReserveDrops + (Tickets * fees.OwnerReserveDrops);
            AccountState after = await source.AccountStateAsync(throwaway.ClassicAddress, token);

            Assert.Equal(floor, refund.LeftBehindDrops);
            Assert.Equal(floor, after.BalanceDrops);
            Assert.Equal((int)Tickets, after.OwnerCount);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
