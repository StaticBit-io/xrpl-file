using Xunit;

namespace XrplFile.Fixtures.Tests;

public sealed class PlanQuoteTests
{
    private const string Writer = "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh";
    private const string Payer = "rPT1Sjq2YGrBMTttX4GZHjKu9dyfzbpAYe";

    private static BatchPublicationEstimate Estimate(
        long payerFee, long writerFee, long payerWorst, long writerWorst, long payerBalance = 100_000_000, long writerBalance = 100_000_000) =>
        new(
            Chunks: 4, Blocks: 1, Lists: 0, Memos: 5,
            Batches: 1, SoloTransactions: 1,
            OpenLedgerFeeDrops: 10, FeeUnitDrops: 10, FeeCeilingDrops: 200,
            PayerFeeDrops: payerFee,
            WriterFeeDrops: writerFee,
            PayerFeeDropsWorstCase: payerWorst,
            WriterFeeDropsWorstCase: writerWorst,
            BaseReserveDrops: 1_000_000, OwnerReserveDrops: 200_000,
            WriterBalanceDrops: writerBalance, WriterOwnerCount: 0,
            PayerBalanceDrops: payerBalance, PayerOwnerCount: 0);

    private static PlanQuote Two(long payerBalance = 100_000_000, long writerBalance = 100_000_000) =>
        new([
            new EntryQuote("cover", 1_000, Estimate(500, 100, 40_000, 9_000, payerBalance, writerBalance)),
            new EntryQuote("video", 2_000_000, Estimate(7_000, 300, 60_000, 12_000, payerBalance, writerBalance)),
        ]);

    [Fact]
    public void TheReserveIsCountedOnce()
    {
        PlanQuote quote = Two();

        Assert.Equal(1_000_000 + 40_000 + 60_000, quote.PayerRequiredDrops);
        Assert.Equal(1_000_000 + 9_000 + 12_000, quote.WriterRequiredDrops);
        Assert.Equal(500 + 100 + 7_000 + 300, quote.FeeDrops);
    }

    [Fact]
    public void CoversComparesTheTotalNotEachEntry()
    {
        // Enough for either entry alone (1,040,000 and 1,060,000), not for both (1,100,000).
        PlanQuote quote = Two(payerBalance: 1_070_000);

        Assert.True(quote.Entries[0].Estimate.PayerCovers);
        Assert.True(quote.Entries[1].Estimate.PayerCovers);
        Assert.False(quote.PayerCovers);
        Assert.True(quote.WriterCovers);
    }

    [Fact]
    public void ACoveringPlanCovers()
    {
        PlanQuote quote = Two();

        Assert.True(quote.PayerCovers);
        Assert.True(quote.WriterCovers);
        Assert.Equal(quote.Entries.Sum(entry => entry.Estimate.Batches + entry.Estimate.SoloTransactions), quote.Transactions);
    }

    [Fact]
    public void FormatListsEveryEntryAndTheTotal()
    {
        string text = PlanQuote.Format(Two(), Writer, Payer, "mainnet");

        Assert.Contains("cover", text, StringComparison.Ordinal);
        Assert.Contains("video", text, StringComparison.Ordinal);
        Assert.Contains("2000000", text.Replace(",", ""), StringComparison.Ordinal);
        Assert.Contains("0.000600", text, StringComparison.Ordinal);
        Assert.Contains("0.007300", text, StringComparison.Ordinal);
        Assert.Contains("0.007900", text, StringComparison.Ordinal);
        Assert.Contains(Writer, text, StringComparison.Ordinal);
        Assert.Contains(Payer, text, StringComparison.Ordinal);
        Assert.Contains("mainnet", text, StringComparison.Ordinal);
        Assert.Contains("1.100000", text, StringComparison.Ordinal);
        Assert.Contains("100.000000", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatPrintsTheWorstCaseCapPerEntryAndInTotal()
    {
        PlanQuote quote = Two();

        // The byte counts are grouped by the current culture; the figures under test are not.
        string text = System.Text.RegularExpressions.Regex.Replace(
            PlanQuote.Format(quote, Writer, Payer, "mainnet"), @"(?<=\d),(?=\d)", "");

        // cover: 40,000 + 9,000 = 49,000 drops; video: 60,000 + 12,000 = 72,000; total 121,000.
        Assert.Equal(121_000, quote.WorstCaseFeeDrops);
        Assert.Contains("cover: 1000 bytes, 2 transactions, fee 0.000600 XRP, at most 0.049000 XRP", text, StringComparison.Ordinal);
        Assert.Contains("video: 2000000 bytes, 2 transactions, fee 0.007300 XRP, at most 0.072000 XRP", text, StringComparison.Ordinal);
        Assert.Contains("Total: 4 transactions, fee 0.007900 XRP, at most 0.121000 XRP", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSessionCapIsTheWorstCasePrintedAsAtMost()
    {
        PlanQuote quote = Two();

        SpendingApproval approval = quote.ApprovalFor("cover", Writer, Payer);

        Assert.Equal(49_000, approval.WriterCeilingDrops + approval.PayerCeilingDrops);
    }

    [Theory]
    [InlineData("publish to mainnet", true)]
    [InlineData("  publish to mainnet\n", true)]
    [InlineData("yes", false)]
    [InlineData("Publish to mainnet", false)]
    [InlineData("publish  to mainnet", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheExactPhraseConfirms(string? line, bool expected) =>
        Assert.Equal(expected, PlanQuote.IsConfirmed(line));

    [Fact]
    public void ApprovalIsThePrintedEstimate()
    {
        PlanQuote quote = Two();

        SpendingApproval approval = quote.ApprovalFor("video", Writer, Payer);

        Assert.Equal(SpendingApproval.For(quote.Entries[1].Estimate, Writer, Payer), approval);
        Assert.Equal(60_000, approval.PayerCeilingDrops);
        Assert.Equal(12_000, approval.WriterCeilingDrops);
    }

    [Fact]
    public void ApprovalForAnUnknownKeyIsRefused() =>
        Assert.Throws<KeyNotFoundException>(() => Two().ApprovalFor("nope", Writer, Payer));

    [Fact]
    public void ACheaperRemainderIsApprovedAtItsOwnCeilings()
    {
        PlanQuote quote = Two();
        BatchPublicationEstimate remainder = Estimate(100, 50, 20_000, 4_000);

        SpendingApproval approval = quote.ResumeApprovalFor("video", new ResumeQuote(Writer, Payer, 2, null, remainder), Writer, Payer);

        Assert.Equal(SpendingApproval.For(remainder, Writer, Payer), approval);
        Assert.Equal(20_000, approval.PayerCeilingDrops);
        Assert.Equal(4_000, approval.WriterCeilingDrops);
    }

    [Fact]
    public void ADearerRemainderIsRefused()
    {
        PlanQuote quote = Two();
        BatchPublicationEstimate remainder = Estimate(100, 50, 60_001, 4_000);

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(
            () => quote.ResumeApprovalFor("video", new ResumeQuote(Writer, Payer, 2, null, remainder), Writer, Payer));

        Assert.Contains("run again for a new quote", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AResumeWithoutABatchEstimateIsRefused() =>
        Assert.Throws<InvalidOperationException>(
            () => Two().ResumeApprovalFor("video", new ResumeQuote(Writer, Payer, 2, null, null), Writer, Payer));

    [Fact]
    public void AResumingEntryIsPrintedAsAnUpperBound()
    {
        PlanQuote quote = new([new EntryQuote("video", 2_000, Estimate(500, 100, 40_000, 9_000), Resuming: true)]);

        string text = PlanQuote.Format(quote, Writer, Payer, "testnet");

        Assert.Contains("resume: at most", text, StringComparison.Ordinal);
    }
}
