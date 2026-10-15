using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class EstimateModelTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-estimate-").FullName;
    private readonly FakeConnectionFactory _connections = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private EstimateModel NewModel() => new(_connections, new RunPaths(_directory));

    private static PublishRequest Request(int size = 4096) =>
        new(new byte[size], "application/octet-stream", "file.bin", "ref-1", "rWriter", "standalone");

    [Fact]
    public async Task A_funded_account_gets_an_estimate_it_can_publish_from()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.NotNull(model.Estimate);
        Assert.True(model.CanPublish);
        Assert.Equal("fake://node", model.NodeUrl);
        Assert.Null(model.Message);
    }

    [Fact]
    public async Task An_account_that_cannot_cover_it_is_told_what_is_held_and_what_is_needed()
    {
        _connections.Ledger.Balances["rWriter"] = 1;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.NotNull(model.Estimate);
        Assert.False(model.CanPublish);
        Assert.NotNull(model.Message);
    }

    [Fact]
    public async Task The_shortfall_is_stated_in_XRP_and_says_how_much_of_it_comes_back()
    {
        _connections.Ledger.Balances["rWriter"] = 1_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(size: 100_000), TestContext.Current.CancellationToken);

        PublicationEstimate estimate = model.Estimate!;
        Assert.True(estimate.PeakReserveDrops * 2 > estimate.RequiredDrops);
        Assert.Equal(
            $"This publication needs {XrpAmount.Format(estimate.RequiredDrops)} and the account can spend "
            + $"{XrpAmount.Format(estimate.SpendableDrops)}. Most of it is reserve that comes back as the publication "
            + $"finishes: {XrpAmount.Format(estimate.PeakReserveDrops)} for tickets. Fund it, or publish a smaller file.",
            model.Message);
        Assert.DoesNotContain("drops", model.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shortfall_that_is_mostly_fees_does_not_claim_the_reserve_is_most_of_it()
    {
        // A node quoting a high fee: the worst-case fee outweighs the ticket reserve.
        _connections.Ledger.Fees = new NetworkFees(500_000, 1_000_000, 200_000);
        _connections.Ledger.Balances["rWriter"] = 1_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(size: 4096), TestContext.Current.CancellationToken);

        PublicationEstimate estimate = model.Estimate!;
        Assert.True(estimate.PeakReserveDrops > 0);
        Assert.True(estimate.PeakReserveDrops * 2 <= estimate.RequiredDrops);
        Assert.DoesNotContain("Most of it", model.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"Some of it is reserve that comes back as the publication finishes: {XrpAmount.Format(estimate.PeakReserveDrops)} for tickets.",
            model.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_node_that_does_not_answer_says_so_in_its_own_words()
    {
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Null(model.Estimate);
        Assert.False(model.CanPublish);
        Assert.Equal(PublishErrorMessages.For(XrplFileError.NodeUnreachable), model.Message);
    }

    [Fact]
    public async Task The_connection_is_closed_whatever_happened()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(_connections.Connections, _connections.Disposals);
    }

    [Fact]
    public async Task A_price_the_application_cannot_use_is_refused_even_when_the_account_could_afford_it()
    {
        // Past int.MaxValue: both strategy option types price a transaction in an int, so a node
        // quoting more than that is a price this application refuses rather than wraps around.
        _connections.Ledger.Fees = new NetworkFees((long)int.MaxValue + 1, 1_000_000, 200_000);

        // Generous on purpose: a balance too small to cover the worst case would also fail
        // CanPublish, and then this test would pass for the wrong reason. This is well past what
        // the inflated fee's worst case could ever demand.
        _connections.Ledger.Balances["rWriter"] = 9_000_000_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.NotNull(model.Estimate);
        Assert.False(model.CanPublish);
        Assert.Equal("The node is quoting a price this application cannot use. Try a different node.", model.Message);
    }

    [Fact]
    public async Task A_second_estimate_replaces_the_first()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(size: 4096), TestContext.Current.CancellationToken);
        long first = model.Estimate!.Chunks;

        await model.EstimateAsync(Request(size: 40_960), TestContext.Current.CancellationToken);

        Assert.True(model.Estimate!.Chunks > first);
    }

    [Fact]
    public async Task A_ticketed_file_on_an_account_whose_ticket_slots_are_all_taken_cannot_be_published()
    {
        _connections.Ledger.Balances["rWriter"] = 1_000_000_000;
        _connections.Sink.LeakTickets("rWriter", TicketStrategy.MaxLiveTickets);

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(size: 100_000), TestContext.Current.CancellationToken);

        Assert.NotNull(model.Estimate);
        Assert.False(model.CanPublish);
        Assert.Contains("250", model.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_small_file_is_still_publishable_when_every_ticket_slot_is_taken()
    {
        _connections.Ledger.Balances["rWriter"] = 1_000_000_000;
        _connections.Sink.LeakTickets("rWriter", TicketStrategy.MaxLiveTickets);

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(size: 4096), TestContext.Current.CancellationToken);

        Assert.True(model.CanPublish);
        Assert.Null(model.Message);
    }
}
