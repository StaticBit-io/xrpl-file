using System.Text.Json;
using Xrpl.Client;
using Xrpl.Client.Json;
using Xrpl.Models;
using Xrpl.Models.Methods;
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

    /// <summary>
    /// A node pages account_objects at its own page size; 10 is the smallest a node that clamps
    /// the limit allows, and 12 tickets are more than that.
    /// </summary>
    private const int PageSize = 10;

    private const uint Tickets = 12;

    [Fact]
    public async Task A_listing_longer_than_a_page_is_read_through_the_nodes_own_marker()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client, PageSize);
            XrplWallet wallet = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);
            NetworkFees fees = await source.FeesAsync(token);

            await StandaloneFixture.MintTicketsAsync(sink, source, wallet, Tickets, fees.OpenLedgerFeeDrops, token);

            // The premise of the test, read raw: this node cuts the list at the page size and hands
            // back a marker. Without it a node that ignored the limit would pass the test untested.
            XrplResponse<object> first = await client.GRequest<object, AccountObjectsRequest>(
                new AccountObjectsRequest(wallet.ClassicAddress)
                {
                    Type = LedgerEntryType.Ticket,
                    LedgerIndex = new Xrpl.Models.Common.LedgerIndex(Xrpl.Models.Ledger.LedgerIndexType.Validated),
                    Limit = PageSize,
                }, token);
            JsonElement page = first.Raw.ToJsonElement();

            Assert.Equal(PageSize, page.GetProperty("account_objects").GetArrayLength());
            Assert.True(page.TryGetProperty("marker", out JsonElement marker) && marker.ValueKind != JsonValueKind.Null);

            IReadOnlyList<LiveTicket> tickets = await source.LiveTicketsAsync(wallet.ClassicAddress, token);

            // Every ticket once: none lost at the page boundary, none listed on both sides of it.
            uint[] sequences = [.. tickets.Select(ticket => ticket.Sequence).Order()];
            Assert.Equal(Enumerable.Range((int)sequences[0], (int)Tickets).Select(sequence => (uint)sequence), sequences);
            Assert.Single(tickets.Select(ticket => ticket.CreatedBy).Distinct());
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
