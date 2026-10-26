using Xunit;

namespace XrplFile.Tests;

public class SweepRecordTests
{
    private static LedgerTransaction Record(string? marker, string? data, string result = "tesSUCCESS") =>
        new("AB", 42, "rWriter", result == "tesSUCCESS", true, result, marker, data, null)
        {
            TransactionType = "AccountSet", Sequence = 7, Fee = 0, ParentBatchID = "CD", TransactionIndex = 3,
        };

    [Fact]
    public void A_chunk_record_is_classified_and_keeps_no_memo_data()
    {
        IReadOnlyList<MemoSubmission> memos = BatchTestNode.Memos(4);
        RoundMarkers markers = new(memos, [1, 2]);

        SweepRecord matching = SweepRecord.From(Record(memos[2].MemoTypeHex, memos[2].MemoDataHex), markers);
        SweepRecord conflicting = SweepRecord.From(Record(memos[1].MemoTypeHex, "00"), markers);
        SweepRecord outside = SweepRecord.From(Record(memos[3].MemoTypeHex, memos[3].MemoDataHex), markers);

        Assert.Equal((MarkerKind.Chunk, 2L, 2, DataVerdict.Matches), (matching.Marker, matching.MarkerIndex, matching.CallIndex, matching.Verdict));
        Assert.Equal(DataVerdict.Conflicts, conflicting.Verdict);
        Assert.Equal((-1, DataVerdict.NotChecked), (outside.CallIndex, outside.Verdict));
        Assert.Equal((3, "CD", 7u), (matching.TransactionIndex, matching.ParentBatchID, matching.Sequence));
        Assert.DoesNotContain(typeof(SweepRecord).GetProperties(), property => property.Name.Contains("Memo", StringComparison.Ordinal));
    }

    // The class is named as a string: ResultClass is internal, and a public test method may not
    // take it as a parameter (CS0051).
    [Theory]
    [InlineData("tesSUCCESS", "Success")]
    [InlineData("tecNO_PERMISSION", "Claimed")]
    [InlineData("tefPAST_SEQ", "Other")]
    [InlineData(null, "Other")]
    public void Results_fall_into_three_classes(string? result, string expected)
    {
        LedgerTransaction tx = new("AB", 42, "rWriter", result == "tesSUCCESS", true, result, null, null, null);

        Assert.Equal(expected, SweepRecord.From(tx, null).Result.ToString());
    }

    [Fact]
    public void A_foreign_ticket_create_covers_its_own_sequence_and_every_ticket_it_took()
    {
        SweepRecord create = SweepRecord.From(new LedgerTransaction("C", 5, "rW", true, true, "tesSUCCESS", null, null, null)
        {
            TransactionType = "TicketCreate", Sequence = 10, TicketCount = 3,
        }, null);
        SweepRecord next = SweepRecord.From(new LedgerTransaction("N", 6, "rW", false, true, "tecFAKE", null, null, null) { Sequence = 14 }, null);

        Assert.True(BatchSweep.Covers([create, next], 10, 15));
        Assert.False(BatchSweep.Covers([create], 10, 15));
        Assert.True(BatchSweep.Covers(Array.Empty<SweepRecord>(), 10, 10));
    }

    [Fact]
    public void Payer_fees_are_counted_by_the_outer_hash_and_only_when_validated()
    {
        BatchCandidate outer = new(0, isOuter: true, "rPayer", 500, 900, 120, [0], new SignedTransaction("00", "AA"));
        LedgerTransaction validated = new("AA", 5, "rPayer", true, true, "tesSUCCESS", null, null, null) { Fee = 120 };
        SweepRecord ours = SweepRecord.From(validated, null);
        SweepRecord pendingOne = SweepRecord.From(validated with { Validated = false, Fee = 999 }, null);
        SweepRecord foreign = SweepRecord.From(validated with { Hash = "BB", Fee = 500 }, null);

        RoundResult counted = BatchSweep.Outcome([outer], [], [ours, foreign]);
        RoundResult skipped = BatchSweep.Outcome([outer], [], [pendingOne]);

        Assert.Equal(120, counted.PayerFeeDrops);
        Assert.Equal(0, skipped.PayerFeeDrops);
        Assert.True(skipped.PayerFeeKnown);
    }

    [Fact]
    public void List_and_manifest_markers_are_named()
    {
        Assert.Equal((MarkerKind.List, 3L), SweepRecord.Classify(MemoLayout.ListMarkerHex(3)));
        Assert.Equal((MarkerKind.Manifest, 0L), SweepRecord.Classify(MemoLayout.ManifestMarkerHex));
        Assert.Equal((MarkerKind.None, 0L), SweepRecord.Classify(null));
        Assert.Equal((MarkerKind.Other, 0L), SweepRecord.Classify("ABCD"));
    }
}
