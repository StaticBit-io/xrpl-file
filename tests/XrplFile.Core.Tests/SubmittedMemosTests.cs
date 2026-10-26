using Xunit;

namespace XrplFile.Tests;

public class SubmittedMemosTests
{
    [Fact]
    public void A_landed_set_counts_once_and_finds_the_next_gap()
    {
        LandedSet set = new(130);

        Assert.True(set.Add(0));
        Assert.False(set.Add(0));
        for (int i = 1; i < 70; i++) set.Add(i);
        set.Add(129);

        Assert.Equal(71, set.Count);
        Assert.True(set.Contains(64));
        Assert.False(set.Contains(70));
        Assert.Equal(70, set.NextMissing(0));
        Assert.Equal(128, set.NextMissing(71 + 57));
        Assert.Equal(-1, set.NextMissing(129));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.Contains(130));
    }

    [Fact]
    public void Batch_keeps_no_hash_and_no_ledger_for_a_chunk_but_folds_it_into_its_block()
    {
        SubmittedMemosBuilder builder = new(3, keepChunkFacts: false);
        builder.Record(0, MemoLayout.ChunkMarkerHex(1023), "H0", 50);
        builder.Record(1, MemoLayout.ChunkMarkerHex(1024), "H1", 52);
        builder.Record(2, MemoLayout.ListMarkerHex(0), "H2", 53);

        SubmittedMemos memos = builder.Build();

        Assert.Equal(3, memos.Count);
        Assert.Equal(3, memos.Landed.Count);
        Assert.False(memos.TryHashOf(0, out _));
        Assert.False(memos.TryLedgerOf(1, out _));
        Assert.True(memos.TryHashOf(2, out string? list));
        Assert.Equal("H2", list);
        Assert.True(memos.TryLedgerOf(2, out long ledger));
        Assert.Equal(53, ledger);
        Assert.Equal(new LedgerRange(50, 50), memos.BlockLedgers[0]);
        Assert.Equal(new LedgerRange(52, 52), memos.BlockLedgers[1]);
    }

    [Fact]
    public void Tickets_keep_every_hash_and_ledger_and_the_first_record_of_a_memo_wins()
    {
        SubmittedMemosBuilder builder = new(2, keepChunkFacts: true);
        builder.Record(0, MemoLayout.ChunkMarkerHex(0), "first", 10);
        builder.Record(0, MemoLayout.ChunkMarkerHex(0), "second", 11);
        builder.Record(1, MemoLayout.ChunkMarkerHex(1), "other", null);

        SubmittedMemos memos = builder.Build();

        Assert.True(memos.TryHashOf(0, out string? hash));
        Assert.Equal("first", hash);
        Assert.True(memos.TryLedgerOf(0, out long ledger));
        Assert.Equal(10, ledger);
        Assert.True(memos.TryHashOf(1, out _));
        Assert.False(memos.TryLedgerOf(1, out _));
        Assert.Equal(new LedgerRange(10, 10), memos.BlockLedgers[0]);
    }
}
