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
}
