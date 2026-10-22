using Xunit;

namespace XrplFile.Tests;

public class BatchFeesTests
{
    [Theory]
    [InlineData(10, 10, 15)]
    [InlineData(11, 10, 17)]
    [InlineData(6, 10, 10)]
    [InlineData(200, 10, 300)]
    public void The_unit_is_one_and_a_half_open_ledger_fees_rounded_up_and_never_below_base(long open, long baseFee, long unit)
    {
        Assert.Equal(unit, BatchFees.Unit(new OpenLedgerFee(open, baseFee)));
    }

    [Fact]
    public void The_ceiling_is_twice_the_approved_unit()
    {
        Assert.Equal(30, BatchFees.Ceiling(15));
    }

    [Theory]
    [InlineData(8, 15, 165)]
    [InlineData(2, 15, 75)]
    [InlineData(7, 10, 100)]
    public void A_batch_pays_its_inners_plus_three_units(int inners, long unit, long fee)
    {
        Assert.Equal(fee, BatchFees.BatchFee(inners, unit));
    }
}
