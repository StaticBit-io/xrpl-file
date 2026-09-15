using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class PublishErrorMessagesTests
{
    [Fact]
    public void Every_error_the_library_can_report_has_a_sentence()
    {
        foreach (XrplFileError error in Enum.GetValues<XrplFileError>())
        {
            string message = PublishErrorMessages.For(error);

            Assert.False(string.IsNullOrWhiteSpace(message), $"{error} has no sentence");
            Assert.EndsWith(".", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_sentence_is_its_own()
    {
        string[] all = [.. Enum.GetValues<XrplFileError>().Select(PublishErrorMessages.For)];

        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_three_that_must_never_be_conflated_are_not()
    {
        string unreachable = PublishErrorMessages.For(XrplFileError.NodeUnreachable);
        string balance = PublishErrorMessages.For(XrplFileError.InsufficientBalance);
        string refused = PublishErrorMessages.For(XrplFileError.SubmitRefused);

        Assert.NotEqual(unreachable, balance);
        Assert.NotEqual(unreachable, refused);
        Assert.NotEqual(balance, refused);
    }

    [Fact]
    public void A_block_numbered_failure_names_its_block()
    {
        XrplFileException thrown = new(XrplFileError.BlockMismatch, "block 3 did not read back", block: 3);

        Assert.Contains("block 3", PublishErrorMessages.For(thrown), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_with_no_block_is_just_its_sentence()
    {
        XrplFileException thrown = new(XrplFileError.NodeUnreachable, "no answer");

        Assert.Equal(PublishErrorMessages.For(XrplFileError.NodeUnreachable), PublishErrorMessages.For(thrown));
    }
}
