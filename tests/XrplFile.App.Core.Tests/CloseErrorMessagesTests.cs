using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class CloseErrorMessagesTests
{
    [Fact]
    public void Every_outcome_has_a_sentence()
    {
        foreach (DeletionOutcome outcome in Enum.GetValues<DeletionOutcome>())
        {
            string message = CloseErrorMessages.For(outcome);

            Assert.False(string.IsNullOrWhiteSpace(message), $"{outcome} has no sentence");
            Assert.EndsWith(".", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_sentence_is_its_own()
    {
        string[] all = [.. Enum.GetValues<DeletionOutcome>().Select(CloseErrorMessages.For)];

        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_three_that_must_never_be_conflated_are_not()
    {
        Assert.NotEqual(CloseErrorMessages.For(DeletionOutcome.NotYet), CloseErrorMessages.For(DeletionOutcome.DestinationMissing));
        Assert.NotEqual(CloseErrorMessages.For(DeletionOutcome.NotYet), CloseErrorMessages.For(DeletionOutcome.DestinationRefuses));
        Assert.NotEqual(CloseErrorMessages.For(DeletionOutcome.DestinationMissing), CloseErrorMessages.For(DeletionOutcome.DestinationRefuses));
    }

    [Fact]
    public void DeletionRefused_says_a_fee_was_claimed_only_when_one_was()
    {
        string claimed = CloseErrorMessages.DeletionRefused(feeClaimed: true);
        string notClaimed = CloseErrorMessages.DeletionRefused(feeClaimed: false);

        Assert.NotEqual(claimed, notClaimed);
        Assert.Contains("claimed", claimed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("claimed", notClaimed, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tecTOO_SOON", true)]
    [InlineData("tecDST_TAG_NEEDED", true)]
    [InlineData("temDST_IS_SRC", false)]
    [InlineData("tefPAST_SEQ", false)]
    [InlineData("telINSUF_FEE_P", false)]
    [InlineData("terQUEUED", false)]
    [InlineData(null, false)]
    public void ClaimsFee_reads_only_tec_results_as_having_claimed_one(string? engineResult, bool expected)
    {
        Assert.Equal(expected, CloseErrorMessages.ClaimsFee(engineResult));
    }
}
