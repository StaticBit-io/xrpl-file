using System.Globalization;

namespace XrplFile.App.Core;

/// <summary>
/// One consistent read of everything Run.razor's own render needs, taken from a single
/// <see cref="RunSnapshot"/> by <see cref="RunModel.Capture"/> -- see that method's own remarks for
/// why every field here has to come from the same read rather than each being asked for separately.
/// </summary>
public readonly record struct RunView(
    bool CanCancel,
    bool IsFinished,
    bool WasCancelled,
    string? Link,
    string? Failure,
    string? HistoryNote,
    string StageText,
    int Percent,
    string? TicketNote,
    bool TicketsLocked,
    string? StateNote,
    bool CanResume,
    string? StatePath);

/// <summary>
/// The run screen (design 4.3). It decides nothing about the run itself — that lives in
/// <see cref="PublicationRun"/>, which survives this screen — and only turns the run's state into
/// what a person reads.
/// </summary>
public sealed class RunModel(PublicationRun run, TimeProvider? time = null)
{
    /// <summary>The window the rate is measured over (spec B21).</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(30);

    /// <summary>The Batch path's rate before thirty seconds of data: the platform's conservative figure (spec B21).</summary>
    public const double BatchPriorMemosPerSecond = 70;

    /// <summary>One settle per pass still to come: about three ledgers, the landing and the two settle ledgers (spec B21).</summary>
    public static readonly TimeSpan SettlePerPass = LedgerTiming.ApproximateLedgerInterval * 3;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _samplesGate = new();
    private readonly List<ProgressSample> _samples = [];

    private readonly record struct ProgressSample(DateTimeOffset At, string Stage, long Done);

    /// <summary>The progress line for this snapshot now, the rate measured from what this model has seen.</summary>
    internal string Describe(RunSnapshot snapshot)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return StageTextOf(snapshot, now, RateOf(snapshot, now));
    }

    /// <summary>
    /// Memos per second over the last <see cref="RateWindow"/> of this stage, or null until the stage
    /// has been watched that long. Samples are taken as the screen renders, once a second.
    /// </summary>
    private double? RateOf(RunSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Progress is not { } progress) return null;

        lock (_samplesGate)
        {
            if (_samples.Count > 0 && !string.Equals(_samples[^1].Stage, progress.Stage, StringComparison.Ordinal)) _samples.Clear();

            _samples.Add(new ProgressSample(now, progress.Stage, progress.Done));
            _samples.RemoveAll(sample => now - sample.At > RateWindow * 2);

            if (now - _samples[0].At < RateWindow) return null;

            ProgressSample from = _samples.Last(sample => now - sample.At >= RateWindow);
            double seconds = (now - from.At).TotalSeconds;

            return seconds <= 0 ? null : (progress.Done - from.Done) / seconds;
        }
    }

    /// <summary>
    /// The progress line (spec 6.3): "Writing the file — 1,240 of 7,124 · 52 chunks/s · about 2 min left ·
    /// 0:24 elapsed". Time left is what remains at the measured rate plus one settle per pass still to
    /// come (spec B21); before a rate is measured the Batch path uses its prior and shows no rate, the
    /// ticket path says "estimating…", and so does any stage whose measured rate is zero. A resume's
    /// fence wait has its own line.
    /// </summary>
    public static string StageTextOf(RunSnapshot snapshot, DateTimeOffset now, double? rate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Progress is not { } progress) return "Starting";

        if (progress.Stage == "fence")
        {
            long ledgers = Math.Max(0, progress.Total - progress.Done);
            return $"Waiting for earlier transactions to expire — about {Duration(LedgerTiming.ApproximateLedgerInterval * ledgers)}";
        }

        string elapsed = snapshot.StartedAt is { } started ? $" · {Clock(now - started)} elapsed" : string.Empty;
        string head = $"{StageNameOf(progress.Stage)} — {progress.Done:N0} of {progress.Total:N0}";

        if (progress.Stage is not ("chunks" or "lists")) return head + elapsed;

        string unit = progress.Stage == "chunks" ? "chunks/s" : "lists/s";
        long remaining = Math.Max(0, progress.Total - progress.Done);

        // Every memo of the pass is in; the pass still settles and the writer still has work, so no
        // figure of seconds is claimed for it (Spec question 23).
        if (remaining == 0) return $"{head} · finishing this pass{elapsed}";
        TimeSpan settles = SettlePerPass * PassesAfter(progress.Stage, snapshot.FileBytes);

        if (rate is { } measured && measured > 0)
        {
            return $"{head} · {measured:N0} {unit} · about {Duration(TimeSpan.FromSeconds(remaining / measured) + settles)} left{elapsed}";
        }

        if (rate is null && snapshot.UsesBatch)
        {
            return $"{head} · about {Duration(TimeSpan.FromSeconds(remaining / BatchPriorMemosPerSecond) + settles)} left{elapsed}";
        }

        return $"{head} · estimating…{elapsed}";
    }

    /// <summary>
    /// Whether the failed-run screen offers Resume (spec B19): the run failed and its state file is
    /// still there and unfinished, which is when the unfinished-runs list shows it.
    /// </summary>
    public static bool IsResumable(RunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.State != PublicationRunState.Failed || snapshot.StatePath is not { } path || !File.Exists(path)) return false;

        try
        {
            return !string.Equals(PublicationState.Load(path).Phase, "done", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The passes still to come after this stage: a list pass when the file has more than one block, and the manifest.</summary>
    private static int PassesAfter(string stage, long fileBytes)
    {
        bool lists = fileBytes > 0 && FileTree.BlockCount(FileTree.ChunkCount(fileBytes, MemoLayout.ChunkBytes)) > 1;
        return stage == "chunks" ? (lists ? 2 : 1) : 1;
    }

    private static string Duration(TimeSpan span)
    {
        double seconds = Math.Max(1, Math.Ceiling(span.TotalSeconds));
        return seconds < 60 ? $"{seconds:0} s" : $"{Math.Ceiling(seconds / 60):0} min";
    }

    private static string Clock(TimeSpan span)
    {
        TimeSpan whole = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(span.TotalSeconds)));
        return whole.TotalHours >= 1
            ? $"{(int)whole.TotalHours}:{whole.Minutes:00}:{whole.Seconds:00}"
            : $"{(int)whole.TotalMinutes}:{whole.Seconds:00}";
    }

    public RunSnapshot Snapshot => run.Snapshot;

    /// <summary>
    /// Whether there is a run to show at all. <see cref="PublicationRunState.Idle"/> means nothing
    /// has started yet, or a finished run's outcome was already put away by <see cref="Dismiss"/> —
    /// both read as "show the picker instead of this screen", which is a decision, not a fact this
    /// application's one caller (Publish.razor) should make itself by comparing
    /// <see cref="Snapshot"/>.State to <see cref="PublicationRunState.Idle"/> in its own markup.
    /// </summary>
    public bool IsActive => Snapshot.State != PublicationRunState.Idle;

    /// <summary>
    /// What the window asks before closing over a running publication (spec 6.1, I11). A ticketed
    /// run names the only two ways its tickets ever go, and promises neither: a later publication
    /// tries, and closing the account always removes them. A run without tickets reads as it always did.
    /// </summary>
    public static string CloseConfirmation(RunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string name = snapshot.Name ?? "the file";

        return snapshot.UsesTickets
            ? $"\"{name}\" is still publishing. Closing now stops the run; it can be resumed later. The tickets it "
                + $"reserved stay on this account, each holding {XrpAmount.Format(snapshot.OwnerReserveDrops)} of owner reserve, "
                + $"until a later publication from this account spends them or the account is closed. Close anyway?"
            : $"\"{name}\" is still publishing. Closing now will stop the run; it can be resumed later. Close anyway?";
    }

    public bool CanCancel => Snapshot.State == PublicationRunState.Running;

    public bool IsFinished => Snapshot.State is
        PublicationRunState.Succeeded or PublicationRunState.Failed or PublicationRunState.Cancelled;

    /// <summary>
    /// Whether the run ended because it was cancelled, rather than succeeding or failing.
    /// <see cref="PublicationRun"/> reports a cancellation with <see cref="RunSnapshot.Failure"/> left
    /// <see langword="null"/> — cancelling is something a person did, not a fault to describe as one
    /// — which otherwise leaves a finished screen with neither <see cref="Link"/> nor
    /// <see cref="Failure"/> to show: a frozen progress bar and a Dismiss button with nothing saying
    /// what happened. This is the third outcome a screen needs alongside those two.
    /// </summary>
    public bool WasCancelled => Snapshot.State == PublicationRunState.Cancelled;

    public string? Link => Snapshot.Report?.Link.ToString();

    public string StageText => Describe(run.Snapshot);

    public int Percent => Snapshot.Progress is { } progress ? PercentOf(progress) : 0;

    /// <summary>
    /// Something about the publications list the person should know, or <see langword="null"/> when
    /// there is nothing to say (design 4.3, 12). Deliberately its own reader rather than folded into
    /// <see cref="Failure"/> or read off <see cref="Snapshot"/> directly by the screen: a run can be
    /// <see cref="PublicationRunState.Succeeded"/>, with a good <see cref="Link"/>, and still carry
    /// one — the publication already reached the ledger, and this is only about whether the local
    /// list of it did. A screen that showed this alongside a failure banner, or hid it because the
    /// run "succeeded", would both be claiming something about the publication that is not true.
    /// </summary>
    public string? HistoryNote => Snapshot.HistoryNote;

    /// <summary>
    /// Why the run ended badly, or <see langword="null"/> on anything else — including a success
    /// that still carries a <see cref="HistoryNote"/>. Never set together with a non-null
    /// <see cref="Link"/>: <see cref="PublicationRun"/> only ever populates one of
    /// <see cref="RunSnapshot.Report"/> or <see cref="RunSnapshot.Failure"/> for a finished run.
    /// </summary>
    public string? Failure => Snapshot.Failure;

    public void Cancel() => run.Cancel();

    /// <summary>Puts the outcome away so the Publish tab can offer a new publication.</summary>
    public void Dismiss() => run.Reset();

    /// <summary>
    /// Everything a render of Run.razor needs, read from one <see cref="RunSnapshot"/> rather than
    /// the one this type's own instance properties above each take fresh (<see cref="Snapshot"/> is
    /// a live pass-through to <see cref="PublicationRun.Snapshot"/>, so two of those properties read
    /// moments apart can observe two different instants of a run that is changing on its own
    /// background task). A render that read <see cref="CanCancel"/> and then, after the run finished
    /// in between, <see cref="IsFinished"/> and <see cref="Link"/> would draw an enabled Cancel
    /// button directly above the "Published" alert -- the exact self-contradicting frame
    /// <see cref="RunSnapshot"/>'s own remarks describe <see cref="PublicationRun.Snapshot"/> as
    /// existing to prevent, reopened one layer up by reading it more than once. Run.razor calls this
    /// once per render and reads every field from the result instead of calling back into this type.
    /// </summary>
    public RunView Capture()
    {
        RunSnapshot snapshot = run.Snapshot;

        return new RunView(
            CanCancel: snapshot.State == PublicationRunState.Running,
            IsFinished: snapshot.State is
                PublicationRunState.Succeeded or PublicationRunState.Failed or PublicationRunState.Cancelled,
            WasCancelled: snapshot.State == PublicationRunState.Cancelled,
            Link: snapshot.Report?.Link.ToString(),
            Failure: snapshot.Failure,
            HistoryNote: snapshot.HistoryNote,
            StageText: Describe(snapshot),
            Percent: snapshot.Progress is { } percentProgress ? PercentOf(percentProgress) : 0,
            TicketNote: TicketNoteOf(snapshot),
            TicketsLocked: TicketsLockedOf(snapshot),
            StateNote: StateNoteOf(snapshot),
            CanResume: IsResumable(snapshot),
            StatePath: snapshot.StatePath);
    }

    /// <inheritdoc cref="TicketNoteOf"/>
    public string? TicketNote => TicketNoteOf(Snapshot);

    /// <inheritdoc cref="TicketsLockedOf"/>
    public bool TicketsLocked => TicketsLockedOf(Snapshot);

    /// <inheritdoc cref="StateNoteOf"/>
    public string? StateNote => StateNoteOf(Snapshot);

    /// <summary>
    /// What the tidy-up after a succeeded publication did with this library's leftover tickets
    /// (spec 4.6), or <see langword="null"/> when it found none or the run did not succeed. Released
    /// and already-gone tickets are told together: either way their reserve is free. A tidy-up whose
    /// counts were lost — it failed, or a cancellation cut it short — does not claim to know how many
    /// tickets stay, since all-zero counts then say nothing about the account. Tickets that stay are
    /// named with the only two ways they ever go, as the close confirmation names them.
    /// </summary>
    public static string? TicketNoteOf(RunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (FinishedReport(snapshot) is not { } report) return null;

        string reserve = XrpAmount.Format(snapshot.OwnerReserveDrops);

        if (report.TicketReleaseFeeDrops is null)
        {
            return "Releasing the leftover tickets on this account did not finish. Any it did not release stay on the "
                + $"account, each holding {reserve} of owner reserve, until a later publication from this account "
                + "spends them or the account is closed.";
        }

        int released = report.TicketsReleased + report.TicketsAlreadyGone;
        int locked = report.TicketsNotReleased;

        if (locked == 0)
        {
            return released == 0
                ? null
                : released == 1
                    ? "1 leftover ticket was released, freeing its owner reserve."
                    : string.Create(CultureInfo.InvariantCulture,
                        $"{released:N0} leftover tickets were released, freeing their owner reserve.");
        }

        string stay = locked == 1
            ? $"It stays on this account, holding {reserve} of owner reserve, until a later publication from "
                + "this account spends it or the account is closed."
            : $"They stay on this account, each holding {reserve} of owner reserve, until a later publication "
                + "from this account spends them or the account is closed.";

        string lead = released == 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"{locked:N0} leftover {(locked == 1 ? "ticket" : "tickets")} could not be released.")
            : string.Create(CultureInfo.InvariantCulture,
                $"{released:N0} leftover {(released == 1 ? "ticket was" : "tickets were")} released, but {locked:N0} could not be.");

        return $"{lead} {stay}";
    }

    /// <summary>
    /// Whether <see cref="TicketNoteOf"/> says tickets may still be holding a reserve: some were not
    /// released, or the tidy-up's counts were lost. False when there is no note at all.
    /// </summary>
    public static bool TicketsLockedOf(RunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return FinishedReport(snapshot) is { } report
            && (report.TicketReleaseFeeDrops is null || report.TicketsNotReleased > 0);
    }

    /// <summary>
    /// What a succeeded run's unsaved state file means, or <see langword="null"/> when it was saved.
    /// The library keeps the first save that failed, so the file on disk may still name an earlier
    /// stage and the unfinished-runs list may go on showing the run; resuming it would write the
    /// block lists and the manifest again.
    /// </summary>
    public static string? StateNoteOf(RunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return FinishedReport(snapshot)?.StateSaveFailure is null
            ? null
            : "This run's progress file could not be updated, so it may still be listed under unfinished runs. The "
                + "file is published; delete that entry rather than resume it, which would pay again for transactions "
                + "the file no longer needs.";
    }

    private static PublicationReport? FinishedReport(RunSnapshot snapshot) =>
        snapshot.State == PublicationRunState.Succeeded ? snapshot.Report : null;

    public static int PercentOf(PublishProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        return progress.Total <= 0 ? 0 : (int)Math.Clamp(progress.Done * 100 / progress.Total, 0, 100);
    }

    /// <summary>
    /// The writer's stage names, in words. "read-back" is the one that has to be named explicitly:
    /// it submits nothing, so a person watching a transaction counter would read a working run as a
    /// stalled one.
    /// </summary>
    public static string StageNameOf(string stage) => stage switch
    {
        "chunks" => "Writing the file",
        "lists" => "Writing the block list",
        "read-back" => "Reading it back from the ledger",
        "manifest" => "Writing the manifest",
        "done" => "Finished",

        // Not an exception: a stage this application does not know about is still a stage the run is
        // genuinely in, and showing its raw name beats showing nothing or crashing the screen.
        _ => stage,
    };
}
