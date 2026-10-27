using Xunit;

namespace XrplFile.Tests;

public class SequenceRangesTests
{
    private static LedgerTransaction Tx(uint sequence, string type = "AccountSet", uint? tickets = null) =>
        new("AB", 1, "rW", true, true, "tesSUCCESS", null, null, null) { Sequence = sequence, TransactionType = type, TicketCount = tickets };

    [Fact]
    public void Records_out_of_order_merge_into_one_run()
    {
        SequenceRanges ranges = new();
        foreach (uint sequence in new uint[] { 12, 10, 11, 14, 13 }) ranges.Add(Tx(sequence));

        Assert.True(ranges.Covers(10, 15));
        Assert.False(ranges.Covers(9, 15));
        Assert.False(ranges.Covers(10, 16));
        Assert.Equal(10u, ranges.Lowest);
    }

    [Fact]
    public void Disjoint_ranges_stay_apart_and_a_record_bridging_them_joins_them()
    {
        SequenceRanges ranges = new();
        ranges.Add(Tx(10));
        ranges.Add(Tx(11));
        ranges.Add(Tx(20));
        ranges.Add(Tx(21));

        Assert.True(ranges.Covers(10, 12));
        Assert.True(ranges.Covers(20, 22));
        Assert.False(ranges.Covers(10, 22));
        Assert.False(ranges.Covers(12, 20));

        // A TicketCreate at 12 with seven tickets took 12 through 19, the gap between the two ranges.
        ranges.Add(Tx(12, "TicketCreate", tickets: 7));

        Assert.True(ranges.Covers(10, 22));
        Assert.False(ranges.Covers(9, 22));
        Assert.False(ranges.Covers(10, 23));
        Assert.Equal(10u, ranges.Lowest);
    }

    [Fact]
    public void A_ticket_create_covers_the_sequences_its_tickets_took_and_a_ticketed_record_covers_nothing()
    {
        SequenceRanges ranges = new();
        ranges.Add(Tx(20, "TicketCreate", tickets: 3));
        ranges.Add(Tx(0));
        ranges.Add(Tx(24));

        Assert.True(ranges.Covers(20, 25));
        Assert.True(ranges.Covers(7, 7));
        Assert.False(ranges.Covers(19, 21));
    }

    [Fact]
    public void A_garbled_record_near_the_top_of_the_range_is_clamped_not_thrown()
    {
        SequenceRanges ranges = new();

        ranges.Add(Tx(uint.MaxValue - 1, "TicketCreate", tickets: uint.MaxValue));
        ranges.Add(Tx(uint.MaxValue));

        Assert.True(ranges.Covers(uint.MaxValue - 1, uint.MaxValue));
        Assert.Equal(uint.MaxValue - 1, ranges.Lowest);
    }
}
