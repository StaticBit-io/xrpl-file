using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>The ticket listing against a node: the request shape, the pagination and the creator field are the node's, not a script's.</summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class LiveTicketsStandaloneTests
{
    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    [Fact]
    public async Task A_minted_ticket_is_listed_with_the_mint_that_made_it()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet wallet = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);
            NetworkFees fees = await source.FeesAsync(token);

            Assert.Empty(await source.LiveTicketsAsync(wallet.ClassicAddress, token));

            await StandaloneFixture.MintTicketsAsync(sink, source, wallet, 3, fees.OpenLedgerFeeDrops, token);

            IReadOnlyList<LiveTicket> tickets = await source.LiveTicketsAsync(wallet.ClassicAddress, token);

            Assert.Equal(3, tickets.Count);
            Assert.Single(tickets.Select(ticket => ticket.CreatedBy).Distinct());

            LedgerTransaction? mint = await source.TransactionAsync(tickets[0].CreatedBy, token);
            Assert.NotNull(mint);
            Assert.Equal(wallet.ClassicAddress, mint!.Account);
            Assert.True(mint.Succeeded);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
