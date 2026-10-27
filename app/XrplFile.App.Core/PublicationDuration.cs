namespace XrplFile.App.Core;

/// <summary>How long a publication takes, as a range shown before a run (spec S11). The run corrects it with measured rates.</summary>
public readonly record struct DurationRange(TimeSpan Shortest, TimeSpan Longest);

public static class PublicationDuration
{
    /// <summary>Batch writing, fast end: the best rate the testnet acceptance measured (spec S11, amended 2026-10-27).</summary>
    public const double BatchFastMemosPerSecond = 160;

    /// <summary>Batch writing, slow end: the platform's conservative figure, also the run screen's prior (spec B21).</summary>
    public const double BatchSlowMemosPerSecond = 70;

    /// <summary>Hashing the file, fast end: the acceptance's first pass, 300,344 chunks in 0:32, rounded down (spec S11).</summary>
    public const double HashingFastChunksPerSecond = 9_000;

    /// <summary>Hashing the file, slow end: the acceptance's resume pass, 300,344 chunks in 1:05, rounded down (spec S11).</summary>
    public const double HashingSlowChunksPerSecond = 4_500;

    /// <summary>The ticket path's measured testnet range (spec S11).</summary>
    public const double TicketFastMemosPerSecond = 2;

    public const double TicketSlowMemosPerSecond = 1;

    /// <summary>The reader's measured pace: 0.7 s per 400 records.</summary>
    public const double ReaderSecondsPer400Records = 0.7;

    /// <summary>A sampled block's chunks, assumed contiguous: about 1,024 records (spec S11).</summary>
    public const int RecordsPerSampledBlock = FileTree.BlockChunks;

    /// <summary>One round's settle: about three ledgers (spec S11).</summary>
    public static TimeSpan SettlePerRound { get; } = LedgerTiming.ApproximateLedgerInterval * 3;

    /// <summary>How many blocks the writer's sampled read takes (spec S21).</summary>
    public static int SampledBlocks(int blocks) => Math.Min(blocks, 4);

    /// <summary>Reading this many history records at the reader's pace.</summary>
    public static TimeSpan Reading(long records) => TimeSpan.FromSeconds(records * ReaderSecondsPer400Records / 400);

    /// <summary>The hash pass over this many chunks, as a range between the fast and the slow rate.</summary>
    public static DurationRange Hashing(long chunks) => new(
        TimeSpan.FromSeconds(chunks / HashingFastChunksPerSecond),
        TimeSpan.FromSeconds(chunks / HashingSlowChunksPerSecond));

    /// <summary>A fresh Batch publication: the hash pass over the whole file, then <see cref="ForBatchRemaining"/> of all of it.</summary>
    public static DurationRange ForBatch(BatchPublicationEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        DurationRange writing = ForBatchRemaining(estimate);
        DurationRange hashing = Hashing(estimate.Chunks);

        return new DurationRange(writing.Shortest + hashing.Shortest, writing.Longest + hashing.Longest);
    }

    /// <summary>
    /// What a Batch publication still has to do once its file is hashed and its history read: the writing,
    /// the settles and the verification. A resume's quote is made of the remaining chunks and prices this
    /// alone, because the hash pass and the history read are over by the time the person sees it.
    /// </summary>
    public static DurationRange ForBatchRemaining(BatchPublicationEstimate remaining)
    {
        ArgumentNullException.ThrowIfNull(remaining);

        TimeSpan settles = SettlePerRound * remaining.Rounds;
        TimeSpan verification = Reading(Math.Min(remaining.Chunks, (long)SampledBlocks(remaining.Blocks) * RecordsPerSampledBlock));
        TimeSpan fixedPart = settles + verification;

        return new DurationRange(
            TimeSpan.FromSeconds(remaining.Memos / BatchFastMemosPerSecond) + fixedPart,
            TimeSpan.FromSeconds(remaining.Memos / BatchSlowMemosPerSecond) + fixedPart);
    }

    public static DurationRange ForOneAccount(PublicationEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        long memos = PublishPolicy.MemoCount(estimate);
        TimeSpan readBack = Reading(estimate.Chunks + estimate.Lists);

        if (!PublishPolicy.UsesTickets(estimate))
        {
            TimeSpan sequence = LedgerTiming.ApproximateLedgerInterval * memos + readBack;
            return new DurationRange(sequence, sequence);
        }

        return new DurationRange(
            TimeSpan.FromSeconds(memos / TicketFastMemosPerSecond) + readBack,
            TimeSpan.FromSeconds(memos / TicketSlowMemosPerSecond) + readBack);
    }

    /// <summary>Downloading a file on the Read screen: every chunk and list record at the reader's pace (spec S15).</summary>
    public static TimeSpan ForRead(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Reading(manifest.ChunkCount + manifest.Lists);
    }
}
