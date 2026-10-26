using Xunit;

namespace XrplFile.Tests;

public class ChunkLandingTests
{
    [Fact]
    public void The_first_landing_of_a_chunk_counts_and_later_copies_change_nothing()
    {
        ChunkLanding landing = new(2_500);

        Assert.True(landing.Land(5, 100));
        Assert.False(landing.Land(5, 90));

        Assert.Equal(1, landing.Landed);
        Assert.True(landing.TryBlockLedgers(0, out LedgerRange range));
        Assert.Equal(new LedgerRange(100, 100), range);
        Assert.False(landing.TryBlockLedgers(1, out _));
    }

    [Fact]
    public void Blocks_keep_their_first_and_last_ledger_and_a_landed_count()
    {
        ChunkLanding landing = new(2_500);

        for (long chunk = 0; chunk < 2_500; chunk++) landing.Land(chunk, 200 - (chunk % 3) + (chunk / 1_024) * 10);

        Assert.True(landing.IsComplete);
        Assert.Equal(1_024, landing.LandedIn(0));
        Assert.Equal(452, landing.LandedIn(2));
        Assert.Equal([198L, 208L, 218L], landing.BlockFirstLedgers());
        Assert.Equal(new LedgerRange(198, 220), landing.Window());
    }

    [Fact]
    public void A_block_widened_from_a_strategy_keeps_the_widest_range()
    {
        ChunkLanding landing = new(1_500);
        for (long chunk = 0; chunk < 1_500; chunk++) landing.MarkLanded(chunk);

        landing.Widen(0, new LedgerRange(40, 44));
        landing.Widen(0, new LedgerRange(38, 41));
        landing.Widen(1, new LedgerRange(45, 45));

        Assert.Equal([38L, 45L], landing.BlockFirstLedgers());
        Assert.Equal(new LedgerRange(38, 45), landing.Window());
    }

    [Fact]
    public void A_block_without_a_ledger_has_no_first_ledger_to_give()
    {
        ChunkLanding landing = new(1_500);
        landing.Land(0, 40);

        Assert.Throws<InvalidOperationException>(() => landing.BlockFirstLedgers());
    }

    [Fact]
    public void Every_chunk_index_stays_an_int()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkLanding((long)int.MaxValue + 1));
    }
}
