namespace XrplFile;

/// <summary>The Batch strategy's knobs, with the spec's values as defaults (spec 4.1).</summary>
public sealed record BatchStrategyOptions
{
    /// <summary>Transmissions in flight at the start (spec B10).</summary>
    public int InitialWindow { get; init; } = 16;

    /// <summary>The most batches sent past the payer's sequence in the latest checked snapshot (spec B10).</summary>
    public int MaxAhead { get; init; } = 256;

    /// <summary><c>LastLedgerSequence</c> = the snapshot's index plus this (spec B7).</summary>
    public int DeadlineLedgers { get; init; } = 20;

    /// <summary>Ledgers waited after every candidate is resolved, before the sweep (spec B9).</summary>
    public int SettleLedgers { get; init; } = 2;

    /// <summary>How often a sending round checks the chain, and how often settlement polls (spec B8).</summary>
    public TimeSpan ChainCheck { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often a round reads the writer's history for progress (spec B20).</summary>
    public TimeSpan ProgressSweep { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The oldest a snapshot may be when a deadline is taken from it (spec B7).</summary>
    public TimeSpan SnapshotMaxAge { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the final sweep waits for history to cover the round (spec B30).</summary>
    public TimeSpan HistoryLagLimit { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a round waits for fees above the ceiling to fall (spec B15).</summary>
    public TimeSpan FeeWait { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Candidates one memo may be put into in one session (spec B13).</summary>
    public int MaxAttemptsPerMemo { get; init; } = 3;

    /// <summary>The step between transport retries of one blob: step × (retry + 1) (spec submit-answer table). Tests shorten it.</summary>
    public TimeSpan TransportRetryStep { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How long reads may keep failing, or the validated ledger stay where it is, before the run fails
    /// with <see cref="XrplFileError.NodeUnreachable"/>. The spec bounds sends, not a node that stops.
    /// </summary>
    public TimeSpan NodeOutageLimit { get; init; } = TimeSpan.FromMinutes(2);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}
