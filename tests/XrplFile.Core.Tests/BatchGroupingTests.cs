using Xunit;

namespace XrplFile.Tests;

public class BatchGroupingTests
{
    [Theory]
    [InlineData(0, new int[0])]
    [InlineData(1, new int[0])]
    [InlineData(2, new[] { 2 })]
    [InlineData(8, new[] { 8 })]
    [InlineData(9, new[] { 7, 2 })]
    [InlineData(16, new[] { 8, 8 })]
    [InlineData(17, new[] { 8, 7, 2 })]
    [InlineData(20, new[] { 8, 8, 4 })]
    public void Memos_go_eight_to_a_batch_and_a_trailing_one_borrows_a_memo(int memos, int[] expected)
    {
        Assert.Equal(expected, BatchGrouping.Sizes(memos).ToArray());
    }

    [Fact]
    public void No_batch_ever_holds_one_memo_or_more_than_eight()
    {
        for (int memos = 2; memos <= 300; memos++)
        {
            Assert.All(BatchGrouping.Sizes(memos), size => Assert.InRange(size, BatchGrouping.MinInners, BatchGrouping.MaxInners));
            Assert.Equal(memos, BatchGrouping.Sizes(memos).Sum());
        }
    }

    [Fact]
    public void A_round_of_one_memo_is_a_solo_and_no_batch()
    {
        RoundPlan plan = BatchGrouping.Plan([42], writerSequence: 10, payerSequence: 70);

        Assert.Empty(plan.Batches);
        Assert.Equal(42, plan.SoloMemo);
    }

    [Fact]
    public void Outers_take_the_payers_sequences_in_order_and_inners_the_writers_after_every_batch_before_them()
    {
        int[] memos = [.. Enumerable.Range(100, 17)];

        RoundPlan plan = BatchGrouping.Plan(memos, writerSequence: 10, payerSequence: 70);

        Assert.Null(plan.SoloMemo);
        Assert.Equal([70u, 71u, 72u], plan.Batches.Select(batch => batch.OuterSequence).ToArray());
        Assert.Equal([0, 1, 2], plan.Batches.Select(batch => batch.Ordinal).ToArray());
        Assert.Equal(
            Enumerable.Range(10, 17).Select(sequence => (uint)sequence).ToArray(),
            plan.Batches.SelectMany(batch => batch.Inners).Select(inner => inner.Sequence).ToArray());
        Assert.Equal(memos, plan.Batches.SelectMany(batch => batch.Inners).Select(inner => inner.MemoIndex).ToArray());
    }
}
