using System.Text.Json;
using XrplFile.App.Core;
using Xunit;

namespace XrplFile.SitePrice.Tests;

/// <summary>
/// Answers only what a price table may ask of the network: fees, the open-ledger fee and the
/// Batch amendment. Every account question throws, so a test fails if the hypothetical accounts
/// ever reach the node.
/// </summary>
internal sealed class FeeOnlySource(NetworkFees fees, OpenLedgerFee open) : ILedgerSource
{
    public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => Task.FromResult(fees);

    public Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken) => Task.FromResult(open);

    public Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) => throw new NotSupportedException();

    public IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(string account, long fromLedger, long toLedger, bool forward, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) => throw new NotSupportedException();
}

public sealed class SitePriceTests
{
    private static readonly NetworkFees Fees = new(12, 1_000_000, 200_000);
    private static readonly OpenLedgerFee Open = new(12, 10);
    private static readonly DateTimeOffset Taken = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    // One chunk: Batch would hold no Batch transaction at all, so the publisher drops the payer.
    private static readonly long TooSmallForBatch = MemoLayout.ChunkBytes - 1;

    private static Task<PriceTable> ComputeAsync(params long[] sizes) =>
        SitePrice.ComputeAsync(new FeeOnlySource(Fees, Open), sizes, Taken, SitePrice.Mainnet, CancellationToken.None);

    [Fact]
    public async Task SmallFileIsPricedWithTickets()
    {
        Assert.True(PublishPolicy.TooSmallForBatch(TooSmallForBatch));

        PriceTable table = await ComputeAsync(TooSmallForBatch);

        Assert.Equal("tickets", Assert.Single(table.Rows).Method);
    }

    [Fact]
    public async Task LargeFileIsPricedWithBatch()
    {
        Assert.False(PublishPolicy.TooSmallForBatch(51_200));

        PriceTable table = await ComputeAsync(51_200);

        Assert.Equal("batch", Assert.Single(table.Rows).Method);
    }

    [Fact]
    public async Task FeeIsTheRunAsItGoes()
    {
        PriceTable table = await ComputeAsync(TooSmallForBatch);
        PriceRow row = Assert.Single(table.Rows);

        Assert.True(row.Transactions > 0);
        Assert.Equal(row.Transactions * Fees.OpenLedgerFeeDrops, row.FeeDrops);
    }

    [Fact]
    public async Task TicketedRowHoldsBaseReservePlusTheTicketReserve()
    {
        PriceTable table = await ComputeAsync(TooSmallForBatch);
        PriceRow row = Assert.Single(table.Rows);

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            new FreshAccountSource(new FeeOnlySource(Fees, Open)), "r", TooSmallForBatch, PublishPolicy.TicketsPerBatch, CancellationToken.None);

        Assert.Equal(estimate.Transactions, row.Transactions);
        Assert.Equal(estimate.BaseReserveDrops + estimate.PeakReserveDrops, row.HeldDrops);
    }

    [Fact]
    public async Task BatchRowSumsWriterAndPayerFeesAndHoldsOnlyTheBaseReserve()
    {
        long size = 51_200;
        PriceTable table = await ComputeAsync(size);
        PriceRow row = Assert.Single(table.Rows);

        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(
            new FreshAccountSource(new FeeOnlySource(Fees, Open)), "w", "p", size, CancellationToken.None);

        Assert.Equal(estimate.PayerFeeDrops + estimate.WriterFeeDrops, row.FeeDrops);
        Assert.Equal(estimate.Memos + estimate.Batches, row.Transactions);
        Assert.Equal(Fees.BaseReserveDrops, row.HeldDrops);
    }

    [Fact]
    public async Task ReturnedIsHeldMinusOwnerReserve()
    {
        PriceTable table = await ComputeAsync(TooSmallForBatch, 10_240, 51_200, 184_320, 1_048_576);

        Assert.Equal(5, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(row.HeldDrops - Fees.OwnerReserveDrops, row.ReturnedDrops));
    }

    [Fact]
    public async Task TheTableCarriesTheNetworkFeesItWasPricedAt()
    {
        PriceTable table = await ComputeAsync(10_240);

        Assert.Equal("mainnet", table.Network);
        Assert.Equal(Taken, table.TakenAt);
        Assert.Equal(new PriceFees(12, 1_000_000, 200_000), table.Fees);
    }

    [Fact]
    public async Task TheTableIsStampedWithTheNetworkItWasGiven()
    {
        PriceTable table = await SitePrice.ComputeAsync(
            new FeeOnlySource(Fees, Open), [10_240], Taken, "testnet", CancellationToken.None);

        Assert.Equal("testnet", table.Network);

        using JsonDocument json = JsonDocument.Parse(SitePrice.ToJson(table));
        Assert.Equal("testnet", json.RootElement.GetProperty("network").GetString());
    }

    [Fact]
    public async Task AnEmptyNetworkNameIsRefused()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => SitePrice.ComputeAsync(
            new FeeOnlySource(Fees, Open), [10_240], Taken, " ", CancellationToken.None));
    }

    [Theory]
    [InlineData(null, "mainnet")]
    [InlineData(0u, "mainnet")]
    [InlineData(1u, "testnet")]
    [InlineData(2u, "devnet")]
    [InlineData(21338u, "21338")]
    public void NetworkNameFollowsTheNodesNetworkId(uint? networkId, string expected)
    {
        Assert.Equal(expected, SitePrice.NetworkName(networkId));
    }

    [Fact]
    public async Task RowsKeepTheOrderOfTheSizesAsked()
    {
        PriceTable table = await ComputeAsync(10_240, 51_200, 184_320, 1_048_576);

        Assert.Equal([10_240L, 51_200L, 184_320L, 1_048_576L], table.Rows.Select(row => row.Bytes));
    }

    [Fact]
    public async Task FreshAccountSourceAnswersNothingAboutRealAccounts()
    {
        FreshAccountSource source = new(new FeeOnlySource(Fees, Open));

        Assert.Equal(AccountState.Absent, await source.AccountStateAsync("r", CancellationToken.None));
        Assert.Empty(await source.LiveTicketsAsync("r", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => source.BalanceDropsAsync("r", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => source.TransactionAsync("00", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => source.CurrentLedgerAsync(CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => source.DestinationAsync("r", CancellationToken.None));
    }

    [Fact]
    public async Task JsonMatchesSchema()
    {
        PriceTable table = (await ComputeAsync(51_200, TooSmallForBatch)) with { Node = "wss://example.test" };

        using JsonDocument json = JsonDocument.Parse(SitePrice.ToJson(table));
        JsonElement root = json.RootElement;

        Assert.Equal(["network", "takenAt", "node", "fees", "rows"], Keys(root));
        Assert.Equal("mainnet", root.GetProperty("network").GetString());
        Assert.Equal("wss://example.test", root.GetProperty("node").GetString());
        Assert.Equal(Taken, root.GetProperty("takenAt").GetDateTimeOffset());
        Assert.Equal(["openLedgerFeeDrops", "baseReserveDrops", "ownerReserveDrops"], Keys(root.GetProperty("fees")));

        foreach (JsonElement row in root.GetProperty("rows").EnumerateArray())
        {
            Assert.Equal(["bytes", "transactions", "feeDrops", "heldDrops", "returnedDrops", "method"], Keys(row));
        }

        Assert.Equal("batch", root.GetProperty("rows")[0].GetProperty("method").GetString());
        Assert.Equal("tickets", root.GetProperty("rows")[1].GetProperty("method").GetString());
    }

    private static string[] Keys(JsonElement element) => [.. element.EnumerateObject().Select(property => property.Name)];
}
