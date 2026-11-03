using XrplFile.App.Core.Localization;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class ReadErrorMessagesTests
{
    private static string Say(XrplFileError error) => TestText.En(ReadErrorMessages.For(error));

    private static string Say(XrplFileException exception) => TestText.En(ReadErrorMessages.For(exception));

    [Fact]
    public void Every_error_has_a_sentence()
    {
        foreach (XrplFileError error in Enum.GetValues<XrplFileError>())
        {
            string message = Say(error);

            Assert.False(string.IsNullOrWhiteSpace(message), $"{error} has no sentence");
            Assert.EndsWith(".", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_sentence_is_its_own()
    {
        // The seven errors publishing can produce but reading cannot -- InsufficientBalance,
        // BudgetExceeded, FeeMismatch, SubmitRefused, ValidationTimeout, ResumeMismatch,
        // SessionAlreadyRun -- deliberately share one sentence: nothing is submitted while
        // reading, so to a reader they are all the same situation, a defect in this application,
        // and telling them apart would be dishonest about that. Excluded here and represented by
        // one of them (SubmitRefused) instead, so the shared sentence is still checked for
        // distinctness against the ten reading actually produces, without failing on its own
        // repetition. Excluding six of the seven here is safe only because
        // The_seven_publishing_only_errors_share_one_sentence pins all seven to that one shared
        // string directly -- if one of them ever drifted to its own wording, that test would fail
        // even though this one would not notice.
        XrplFileError[] readingErrors =
        [
            XrplFileError.InvalidLink,
            XrplFileError.ManifestNotFound,
            XrplFileError.NodeLacksHistory,
            XrplFileError.CorruptManifest,
            XrplFileError.ListMismatch,
            XrplFileError.BlockMismatch,
            XrplFileError.ChunkMissing,
            XrplFileError.TooLarge,
            XrplFileError.NodeUnreachable,
            XrplFileError.SubmitRefused,
        ];

        string[] all = [.. readingErrors.Select(Say)];

        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_seven_publishing_only_errors_share_one_sentence()
    {
        // What Every_sentence_is_its_own's exclusion comment relies on: the group's shared-ness is
        // pinned here directly, rather than merely asserted in a comment, so a future change that
        // gives one of these seven its own wording fails this test even though it would not disturb
        // the distinctness check above (which only ever looks at one representative, SubmitRefused).
        XrplFileError[] publishingOnlyErrors =
        [
            XrplFileError.InsufficientBalance,
            XrplFileError.BudgetExceeded,
            XrplFileError.FeeMismatch,
            XrplFileError.SubmitRefused,
            XrplFileError.ValidationTimeout,
            XrplFileError.ResumeMismatch,
            XrplFileError.SessionAlreadyRun,
        ];

        Assert.Single(publishingOnlyErrors.Select(Say).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void The_four_batch_publishing_errors_share_the_publishing_only_sentence()
    {
        string shared = Say(XrplFileError.SubmitRefused);

        foreach (XrplFileError error in new[]
                 {
                     XrplFileError.BatchNotEnabled, XrplFileError.PayerInsufficientBalance,
                     XrplFileError.AccountInUse, XrplFileError.FeeTooHigh,
                 })
        {
            Assert.Equal(shared, Say(error));
        }
    }

    [Fact]
    public void The_streaming_publication_errors_share_the_publishing_only_sentence()
    {
        string shared = Say(XrplFileError.SubmitRefused);

        foreach (XrplFileError error in new[] { XrplFileError.WriterWindowConflict, XrplFileError.ManifestRejected, XrplFileError.ApprovalExceeded })
        {
            Assert.Equal(shared, Say(error));
        }
    }

    [Fact]
    public void A_reading_failure_does_not_describe_a_publication()
    {
        // The same enum value means something different to a reader. BlockMismatch to a publisher
        // is "the ledger did not return what was just written"; to a reader it is "the file in the
        // ledger does not match its own manifest". Reusing the publisher's wording would tell a
        // reader about a publication that never happened.
        foreach (XrplFileError error in new[]
                 {
                     XrplFileError.BlockMismatch, XrplFileError.ListMismatch, XrplFileError.ChunkMissing,
                 })
        {
            Assert.NotEqual(TestText.En(PublishErrorMessages.For(error)), Say(error));
        }
    }

    [Fact]
    public void A_failure_reading_cannot_cause_says_so()
    {
        // Nothing is submitted while reading, so these cannot happen here. They still need a
        // sentence, because the enum-coverage test enumerates every value.
        Assert.Contains("reading", Say(XrplFileError.SubmitRefused), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_block_numbered_failure_names_its_block()
    {
        XrplFileException thrown = new(XrplFileError.BlockMismatch, "block 3 did not verify", block: 3);

        Assert.Contains("block 3", Say(thrown), StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_numbered_failure_keeps_its_sentence_and_names_the_block_exactly()
    {
        XrplFileException thrown = new(XrplFileError.BlockMismatch, "block 3 did not verify", block: 3);

        Assert.Equal(
            "A block of the file does not match what the file's own description says it should be. Nothing was saved. (block 3)",
            Say(thrown));
    }

    [Fact]
    public void The_publishing_only_errors_are_one_phrase_not_just_one_text()
    {
        // The decision "these cannot happen while reading" is the phrase's identity, so a translation
        // cannot make them diverge.
        UiText shared = ReadErrorMessages.For(XrplFileError.SubmitRefused);

        Assert.Equal(shared, ReadErrorMessages.For(XrplFileError.ManifestRejected));
        Assert.Equal(shared, ReadErrorMessages.For(XrplFileError.AccountInUse));
    }
}
