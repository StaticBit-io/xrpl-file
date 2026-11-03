using XrplFile.App.Core.Localization;
using Xunit;

namespace XrplFile.App.Core.Tests.Localization;

/// <summary>The phrases of the Read screen whose English changes with a number.</summary>
public sealed class ReadAreaTextTests
{
    [Theory]
    [InlineData(0, 1, "0 of 1 block")]
    [InlineData(1, 1, "1 of 1 block")]
    [InlineData(2, 5, "2 of 5 blocks")]
    [InlineData(1200, 2400, "1,200 of 2,400 blocks")]
    public void Block_progress_agrees_with_the_total(int done, int total, string expected)
    {
        UiText text = UiText.Of(ReadText.ProgressBlocks, ("done", done), ("total", total), ("count", total));

        Assert.Equal(expected, TestText.En(text));
    }

    [Theory]
    [InlineData(0, 1, "0 of 1 byte")]
    [InlineData(10, 4096, "10 of 4,096 bytes")]
    public void Byte_progress_agrees_with_the_total(long done, long total, string expected)
    {
        UiText text = UiText.Of(ReadText.ProgressBytes, ("done", done), ("total", total), ("count", total));

        Assert.Equal(expected, TestText.En(text));
    }

    [Theory]
    [InlineData(1, "1 block")]
    [InlineData(3, "3 blocks")]
    public void The_block_count_of_a_file_agrees_with_its_number(int blocks, string expected)
    {
        Assert.Equal(expected, TestText.En(UiText.Of(ReadText.Blocks, ("count", blocks))));
    }

    [Fact]
    public void A_named_file_is_read_back_in_quotes()
    {
        Assert.Equal("Read \"photo.png\" successfully.", TestText.En(UiText.Of(ReadText.SucceededNamed, ("name", "photo.png"))));
        Assert.Equal("Read successfully.", TestText.En(ReadText.Succeeded));
    }

    [Fact]
    public void The_block_in_a_failure_is_not_grouped()
    {
        UiText text = UiText.Of(ReadText.ErrorWithBlock, ("sentence", ReadText.ErrorChunkMissing), ("block", 1234));

        Assert.Equal("Part of the file is not in the ledger. Nothing was saved. (block 1234)", TestText.En(text));
    }
}
