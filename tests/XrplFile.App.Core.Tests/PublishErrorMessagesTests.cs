using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class PublishErrorMessagesTests
{
    private static string ForRunText(XrplFileException exception, RunFailurePoint point, bool usesTickets, long ownerReserveDrops) =>
        TestText.En(PublishErrorMessages.ForRun(exception, point, usesTickets, ownerReserveDrops));

    [Fact]
    public void Every_error_the_library_can_report_has_a_sentence()
    {
        foreach (XrplFileError error in Enum.GetValues<XrplFileError>())
        {
            string message = TestText.En(PublishErrorMessages.For(error));

            Assert.False(string.IsNullOrWhiteSpace(message), $"{error} has no sentence");
            Assert.EndsWith(".", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_sentence_is_its_own()
    {
        string[] all = [.. Enum.GetValues<XrplFileError>().Select(error => TestText.En(PublishErrorMessages.For(error)))];

        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TooLarge_says_the_applications_limit()
    {
        Assert.Equal("This application publishes files up to 16 GiB.", TestText.En(PublishErrorMessages.For(XrplFileError.TooLarge)));
    }

    [Fact]
    public void The_three_that_must_never_be_conflated_are_not()
    {
        string unreachable = TestText.En(PublishErrorMessages.For(XrplFileError.NodeUnreachable));
        string balance = TestText.En(PublishErrorMessages.For(XrplFileError.InsufficientBalance));
        string refused = TestText.En(PublishErrorMessages.For(XrplFileError.SubmitRefused));

        Assert.NotEqual(unreachable, balance);
        Assert.NotEqual(unreachable, refused);
        Assert.NotEqual(balance, refused);
    }

    [Fact]
    public void A_block_numbered_failure_names_its_block()
    {
        XrplFileException thrown = new(XrplFileError.BlockMismatch, "block 3 did not read back", block: 3);

        Assert.Contains("block 3", TestText.En(PublishErrorMessages.For(thrown)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_number_is_not_grouped_like_a_count()
    {
        XrplFileException thrown = new(XrplFileError.BlockMismatch, "block 12345 did not read back", block: 12_345);

        Assert.EndsWith("(block 12345)", TestText.En(PublishErrorMessages.For(thrown)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_with_no_block_is_just_its_sentence()
    {
        XrplFileException thrown = new(XrplFileError.NodeUnreachable, "no answer");

        Assert.Equal(TestText.En(PublishErrorMessages.For(XrplFileError.NodeUnreachable)), TestText.En(PublishErrorMessages.For(thrown)));
    }

    [Fact]
    public void A_validation_timeout_promises_only_what_a_resume_actually_reuses()
    {
        Assert.Equal(
            "Transactions were submitted but not seen validated in time. The run is listed under unfinished runs, and "
                + "resuming does not pay again for the chunks and block lists that already landed.",
            TestText.En(PublishErrorMessages.For(XrplFileError.ValidationTimeout)));
    }

    [Fact]
    public void A_node_that_fails_a_fresh_run_before_it_sends_anything_says_nothing_was_spent()
    {
        XrplFileException thrown = new(XrplFileError.NodeUnreachable, "no answer");

        Assert.Equal(
            "The node did not answer. Nothing was spent. Check the node URL in Settings and try again.",
            ForRunText(thrown, RunFailurePoint.BeforeSubmitting, usesTickets: false, ownerReserveDrops: 0));
    }

    [Fact]
    public void A_node_that_fails_a_resume_before_it_sends_anything_says_the_run_is_still_listed()
    {
        XrplFileException thrown = new(XrplFileError.NodeUnreachable, "no answer");

        Assert.Equal(
            "The node did not answer. This attempt spent nothing, and the run is still listed under unfinished runs. "
                + "Check the node URL in Settings and try again.",
            ForRunText(thrown, RunFailurePoint.BeforeResuming, usesTickets: false, ownerReserveDrops: 0));
    }

    [Fact]
    public void A_node_that_stops_answering_mid_run_does_not_claim_nothing_was_spent()
    {
        XrplFileException thrown = new(XrplFileError.NodeUnreachable, "no answer");

        Assert.Equal(
            "The node stopped answering partway through the run. Transactions sent before that may already be in the "
                + "ledger and paid for. The run is listed under unfinished runs: check the node URL in Settings, then "
                + "resume it. Resuming does not pay again for the chunks and block lists that already landed.",
            ForRunText(thrown, RunFailurePoint.WhileSubmitting, usesTickets: false, ownerReserveDrops: 200_000));
    }

    [Fact]
    public void A_node_that_stops_answering_during_the_read_back_says_every_chunk_is_already_in()
    {
        XrplFileException thrown = new(XrplFileError.NodeUnreachable, "no answer");

        Assert.Equal(
            "Every chunk of the file is in the ledger and paid for, but the node stopped answering while the app read "
                + "the file back to check it, so the link has not been written yet. The run is listed under unfinished "
                + "runs: resume it to check the file again and write the link. Resuming pays only for the link's own "
                + "transaction.",
            ForRunText(thrown, RunFailurePoint.WhileReadingBack, usesTickets: true, ownerReserveDrops: 200_000));
    }

    [Theory]
    [InlineData(RunFailurePoint.WhileSubmitting, "read-back", RunFailurePoint.WhileReadingBack)]
    [InlineData(RunFailurePoint.WhileSubmitting, "chunks", RunFailurePoint.WhileSubmitting)]
    [InlineData(RunFailurePoint.WhileSubmitting, "manifest", RunFailurePoint.WhileSubmitting)]
    [InlineData(RunFailurePoint.WhileSubmitting, null, RunFailurePoint.WhileSubmitting)]
    [InlineData(RunFailurePoint.BeforeResuming, "read-back", RunFailurePoint.BeforeResuming)]
    [InlineData(RunFailurePoint.BeforeSubmitting, "read-back", RunFailurePoint.BeforeSubmitting)]
    public void Only_a_submitting_run_last_seen_reading_back_failed_while_reading_back(RunFailurePoint point, string? stage, RunFailurePoint expected)
    {
        Assert.Equal(expected, PublishErrorMessages.PointAt(point, stage));
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
            ForRunText(thrown, RunFailurePoint.WhileSubmitting, usesTickets: true, ownerReserveDrops: drops));
    }

    [Theory]
    [InlineData(RunFailurePoint.BeforeSubmitting)]
    [InlineData(RunFailurePoint.BeforeResuming)]
    [InlineData(RunFailurePoint.WhileSubmitting)]
    [InlineData(RunFailurePoint.WhileReadingBack)]
    public void Every_other_error_keeps_its_own_sentence_wherever_the_run_failed(RunFailurePoint point)
    {
        foreach (XrplFileError error in Enum.GetValues<XrplFileError>().Where(e => e != XrplFileError.NodeUnreachable))
        {
            if (error == XrplFileError.FeeTooHigh && point == RunFailurePoint.BeforeSubmitting) continue;
            if (error == XrplFileError.NodeLacksHistory && point is RunFailurePoint.WhileSubmitting or RunFailurePoint.WhileReadingBack or RunFailurePoint.BeforeResuming) continue;
            if (error is XrplFileError.PayerInsufficientBalance or XrplFileError.BatchNotEnabled && point == RunFailurePoint.BeforeResuming) continue;

            XrplFileException thrown = new(error, "failed", block: 7);

            Assert.Equal(
                TestText.En(PublishErrorMessages.For(thrown)),
                ForRunText(thrown, point, usesTickets: true, ownerReserveDrops: 200_000));
        }
    }

    [Fact]
    public void A_resume_refused_for_a_short_payer_does_not_offer_a_payer_chooser_the_screen_may_not_have()
    {
        XrplFileException thrown = new(XrplFileError.PayerInsufficientBalance, "payer holds too little");

        string sentence = ForRunText(thrown, RunFailurePoint.BeforeResuming, usesTickets: false, ownerReserveDrops: 0);

        Assert.Equal("The payer does not hold enough XRP to finish this publication. Fund it, then resume.", sentence);
        Assert.DoesNotContain("another payer", sentence, StringComparison.Ordinal);
        Assert.Equal(
            TestText.En(PublishErrorMessages.For(XrplFileError.PayerInsufficientBalance)),
            ForRunText(thrown, RunFailurePoint.BeforeSubmitting, usesTickets: false, ownerReserveDrops: 0));
        Assert.Equal(
            TestText.En(PublishErrorMessages.For(XrplFileError.PayerInsufficientBalance)),
            ForRunText(thrown, RunFailurePoint.WhileSubmitting, usesTickets: false, ownerReserveDrops: 0));
    }

    [Fact]
    public void A_resume_refused_because_Batch_is_off_does_not_offer_tickets_a_Batch_run_cannot_use()
    {
        XrplFileException thrown = new(XrplFileError.BatchNotEnabled, "Batch is not enabled");

        string sentence = ForRunText(thrown, RunFailurePoint.BeforeResuming, usesTickets: false, ownerReserveDrops: 0);

        Assert.Equal("This network no longer enables Batch; the run can be resumed once it does.", sentence);
        Assert.DoesNotContain("Nobody", sentence, StringComparison.Ordinal);
        Assert.Equal(
            TestText.En(PublishErrorMessages.For(XrplFileError.BatchNotEnabled)),
            ForRunText(thrown, RunFailurePoint.BeforeSubmitting, usesTickets: false, ownerReserveDrops: 0));
        Assert.Equal(
            TestText.En(PublishErrorMessages.For(XrplFileError.BatchNotEnabled)),
            ForRunText(thrown, RunFailurePoint.WhileSubmitting, usesTickets: false, ownerReserveDrops: 0));
    }

    [Fact]
    public void Batch_not_enabled_sends_the_person_to_Nobody()
    {
        Assert.Equal(
            "This network does not enable Batch. Choose Nobody under Paid by to publish with tickets.",
            TestText.En(PublishErrorMessages.For(XrplFileError.BatchNotEnabled)));
    }

    [Fact]
    public void A_short_payer_is_told_apart_from_a_short_writer()
    {
        Assert.Equal(
            "The payer does not hold enough XRP for this publication. Fund it, or choose another payer.",
            TestText.En(PublishErrorMessages.For(XrplFileError.PayerInsufficientBalance)));
    }

    [Theory]
    [InlineData(AccountRole.Writer, "writer")]
    [InlineData(AccountRole.Payer, "payer")]
    public void An_account_in_use_names_its_role(AccountRole role, string word)
    {
        XrplFileException thrown = new(XrplFileError.AccountInUse, "rX sent a payment", role: role);

        Assert.Equal(
            $"The {word} account sent a transaction of its own during the publication. Is this key in use somewhere else? "
                + "The run is listed under unfinished runs; resume it once nothing else uses the key.",
            TestText.En(PublishErrorMessages.For(thrown)));
    }

    [Fact]
    public void Fees_that_rose_mid_run_say_the_run_can_be_resumed()
    {
        Assert.Equal(
            "Network fees rose above what this publication was approved for. The run is listed under unfinished runs; "
                + "resume it when fees are back down.",
            TestText.En(PublishErrorMessages.For(XrplFileError.FeeTooHigh)));
    }

    [Fact]
    public void Fees_too_high_before_anything_started_do_not_mention_an_unfinished_run()
    {
        XrplFileException thrown = new(XrplFileError.FeeTooHigh, "open ledger fee 250");

        string sentence = ForRunText(thrown, RunFailurePoint.BeforeSubmitting, usesTickets: false, ownerReserveDrops: 0);

        Assert.Equal(TestText.En(PublishErrorMessages.FeeTooHighNow), sentence);
        Assert.DoesNotContain("unfinished", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void History_that_lags_a_running_publication_is_not_described_as_a_reader_problem()
    {
        XrplFileException thrown = new(XrplFileError.NodeLacksHistory, "account_tx lagged");

        Assert.Equal(
            TestText.En(PublishErrorMessages.HistoryLaggedWhilePublishing),
            ForRunText(thrown, RunFailurePoint.WhileSubmitting, usesTickets: false, ownerReserveDrops: 0));
        Assert.Equal(
            TestText.En(PublishErrorMessages.HistoryLaggedWhilePublishing),
            ForRunText(thrown, RunFailurePoint.BeforeResuming, usesTickets: false, ownerReserveDrops: 0));
        Assert.Equal(
            TestText.En(PublishErrorMessages.For(XrplFileError.NodeLacksHistory)),
            ForRunText(thrown, RunFailurePoint.BeforeSubmitting, usesTickets: false, ownerReserveDrops: 0));
    }

    [Fact]
    public void A_conflict_in_the_window_asks_for_a_fresh_writer()
    {
        Assert.Equal(
            "The writer account carries other data for this file's chunks — was it used for another publication while this one was interrupted? Publish this file again from a fresh writer key.",
            TestText.En(PublishErrorMessages.For(XrplFileError.WriterWindowConflict)));
    }

    [Fact]
    public void A_changed_estimate_asks_for_a_new_one()
    {
        Assert.Equal("Fees or balances changed since this was approved. Estimate it again.", TestText.En(PublishErrorMessages.For(XrplFileError.ApprovalExceeded)));
    }

    [Fact]
    public void A_rejected_manifest_names_its_link()
    {
        XrplFileLink link = new("testnet", new string('A', 64));
        XrplFileException thrown = new(XrplFileError.ManifestRejected, "rejected", link: link);

        Assert.Contains(link.ToString(), TestText.En(PublishErrorMessages.For(thrown)), StringComparison.Ordinal);
    }
}
