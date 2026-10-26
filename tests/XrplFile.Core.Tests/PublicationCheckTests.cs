using Xunit;

namespace XrplFile.Tests;

public class PublicationCheckTests
{
    private const string Root = "6EDDD4789DCA250C1AE69229121C6CB3EE0190E82A9BEF6F797F728EB8D3B7C8";

    [Theory]
    [InlineData(1, new[] { 0 })]
    [InlineData(2, new[] { 0, 1 })]
    [InlineData(3, new[] { 0, 1, 2 })]
    [InlineData(4, new[] { 0, 1, 2, 3 })]
    public void Up_to_four_blocks_every_block_is_checked(int blocks, int[] expected)
    {
        Assert.Equal(expected, PublicationCheck.SampleBlocks(Root, blocks));
    }

    [Theory]
    [InlineData(Root, 1_651, new[] { 0, 1650, 1245, 1637 })]
    [InlineData(Root, 5, new[] { 0, 4, 3, 1 })]
    [InlineData(Root, 6, new[] { 0, 5, 4, 2 })]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", 1_651, new[] { 0, 1650, 1574, 1540 })]
    [InlineData("E52D9C508C502347344D8C07AD91CBD6068AFC75FF6292F062A09CA381C89E71", 5, new[] { 0, 4, 2, 3 })]
    public void The_sample_is_seeded_by_the_root(string root, int blocks, int[] expected)
    {
        // The last root draws block 2 at counters 0 and 1: the duplicate is skipped and counter 2 gives block 3.
        Assert.Equal(expected, PublicationCheck.SampleBlocks(root, blocks));
    }

    private static (List<BlockEntry> Plan, ChunkLanding Landing, Manifest Manifest) Published()
    {
        ChunkLanding landing = new(2_048);
        for (long chunk = 0; chunk < 2_048; chunk++) landing.Land(chunk, chunk < 1_024 ? 100 + chunk % 2 : 102);

        List<BlockEntry> plan = [new BlockEntry(0, new string('A', 64), 100), new BlockEntry(1, new string('B', 64), 102)];
        Manifest manifest = new("application/octet-stream", 2_048_000, 1_000, Root, 100, 103, 2, 1, null);
        return (plan, landing, manifest);
    }

    [Fact]
    public void A_published_plan_is_reachable()
    {
        (List<BlockEntry> plan, ChunkLanding landing, Manifest manifest) = Published();

        PublicationCheck.RequireReachable(plan, landing, manifest);
        PublicationCheck.RequireListed(plan, [.. plan.Select(entry => entry with { HashHex = entry.HashHex.ToLowerInvariant() })]);
    }

    [Fact]
    public void A_block_whose_first_ledger_is_after_one_of_its_chunks_fails_before_any_read()
    {
        (List<BlockEntry> plan, ChunkLanding landing, Manifest manifest) = Published();
        List<BlockEntry> late = [plan[0] with { FirstLedger = 101 }, plan[1]];

        XrplFileException error = Assert.Throws<XrplFileException>(() => PublicationCheck.RequireReachable(late, landing, manifest));

        Assert.Equal(XrplFileError.BlockMismatch, error.Error);
        Assert.Equal(0, error.Block);
    }

    [Fact]
    public void A_chunk_outside_the_window_fails()
    {
        (List<BlockEntry> plan, ChunkLanding landing, Manifest manifest) = Published();

        XrplFileException error = Assert.Throws<XrplFileException>(() =>
            PublicationCheck.RequireReachable(plan, landing, manifest with { LastLedger = 101 }));

        Assert.Equal(1, error.Block);
    }

    [Fact]
    public void A_list_on_the_ledger_that_is_not_the_plan_fails()
    {
        (List<BlockEntry> plan, _, _) = Published();
        List<BlockEntry> listed = [plan[0], plan[1] with { FirstLedger = 103 }];

        XrplFileException error = Assert.Throws<XrplFileException>(() => PublicationCheck.RequireListed(plan, listed));

        Assert.Equal(XrplFileError.ListMismatch, error.Error);
    }
}
