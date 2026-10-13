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

    [Fact]
    public void A_validation_timeout_promises_only_what_a_resume_actually_reuses()
    {
        Assert.Equal(
            "Transactions were submitted but not seen validated in time. The run is listed under unfinished runs, and "
                + "resuming does not pay again for the chunks and block lists that already landed.",
            PublishErrorMessages.For(XrplFileError.ValidationTimeout));
    }

    [Fact]
    public void A_node_that_fails_a_fresh_run_before_it_sends_anything_says_nothing_was_spent()
    {
        XrplFileException thrown = new(XrplFileError.NodeUnreachable, "no answer");

        Assert.Equal(
            "The node did not answer. Nothing was spent. Check the node URL in Settings and try again.",
            PublishErrorMessages.ForRun(thrown, RunFailurePoint.BeforeSubmitting, usesTickets: false, ownerReserveDrops: 0));
    }

    [Fact]
    public void A_node_that_fails_a_resume_before_it_sends_anything_says_the_run_is_still_listed()
    {
        XrplFileException thrown = new(XrplFileError.NodeUnreachable, "no answer");

        Assert.Equal(
            "The node did not answer. This attempt spent nothing, and the run is still listed under unfinished runs. "
                + "Check the node URL in Settings and try again.",
            PublishErrorMessages.ForRun(thrown, RunFailurePoint.BeforeResuming, usesTickets: false, ownerReserveDrops: 0));
    }

    [Fact]
    public void A_node_that_stops_answering_mid_run_does_not_claim_nothing_was_spent()
    {
        XrplFileException thrown = new(XrplFileError.NodeUnreachable, "no answer");

        Assert.Equal(
            "The node stopped answering partway through the run. Transactions sent before that may already be in the "
                + "ledger and paid for. The run is listed under unfinished runs: check the node URL in Settings, then "
                + "resume it. Resuming does not pay again for the chunks and block lists that already landed.",
            PublishErrorMessages.ForRun(thrown, RunFailurePoint.WhileSubmitting, usesTickets: false, ownerReserveDrops: 200_000));
    }

    [Theory]
    [InlineData(200_000, "0.2 XRP")]
    [InlineData(2_000_000, "2 XRP")]
    [InlineData(1_250_000, "1.25 XRP")]
    public void A_ticketed_run_that_loses_its_node_mid_run_names_the_reserve_its_tickets_may_hold(long drops, string xrp)
    {
        XrplFileException thrown = new(XrplFileError.NodeUnreachable, "no answer");

        Assert.Equal(
            "The node stopped answering partway through the run. Transactions sent before that may already be in the "
                + $"ledger and paid for, and tickets it reserved may be on this account, each holding {xrp} of owner "
                + "reserve until a later publication from this account spends them or the account is closed. The run "
                + "is listed under unfinished runs: check the node URL in Settings, then resume it. Resuming does not "
                + "pay again for the chunks and block lists that already landed.",
            PublishErrorMessages.ForRun(thrown, RunFailurePoint.WhileSubmitting, usesTickets: true, ownerReserveDrops: drops));
    }

    [Theory]
    [InlineData(RunFailurePoint.BeforeSubmitting)]
    [InlineData(RunFailurePoint.BeforeResuming)]
    [InlineData(RunFailurePoint.WhileSubmitting)]
    public void Every_other_error_keeps_its_own_sentence_wherever_the_run_failed(RunFailurePoint point)
    {
        foreach (XrplFileError error in Enum.GetValues<XrplFileError>().Where(e => e != XrplFileError.NodeUnreachable))
        {
            XrplFileException thrown = new(error, "failed", block: 7);

            Assert.Equal(
                PublishErrorMessages.For(thrown),
                PublishErrorMessages.ForRun(thrown, point, usesTickets: true, ownerReserveDrops: 200_000));
        }
    }
}
