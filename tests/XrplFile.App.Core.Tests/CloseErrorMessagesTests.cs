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
}
