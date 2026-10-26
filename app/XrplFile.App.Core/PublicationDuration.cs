namespace XrplFile.App.Core;

/// <summary>How long a publication takes, as a range shown before a run (spec S11). The run corrects it with measured rates.</summary>
public readonly record struct DurationRange(TimeSpan Shortest, TimeSpan Longest);

public static class PublicationDuration
{
    /// <summary>Batch writing, fast end: the platform's measured best (spec S11).</summary>
    public const double BatchFastMemosPerSecond = 85;

    /// <summary>Batch writing, slow end: the platform's conservative figure, also the run screen's prior (spec B21).</summary>
    public const double BatchSlowMemosPerSecond = 70;

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

    public static DurationRange ForBatch(BatchPublicationEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        TimeSpan settles = SettlePerRound * estimate.Rounds;
        TimeSpan verification = Reading(Math.Min(estimate.Chunks, (long)SampledBlocks(estimate.Blocks) * RecordsPerSampledBlock));
        TimeSpan fixedPart = settles + verification;

        return new DurationRange(
            TimeSpan.FromSeconds(estimate.Memos / BatchFastMemosPerSecond) + fixedPart,
            TimeSpan.FromSeconds(estimate.Memos / BatchSlowMemosPerSecond) + fixedPart);
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
