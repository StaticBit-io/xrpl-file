using XrplFile.App.Core.Localization;

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
    UiText? Failure,
    UiText? HistoryNote,
    UiText StageText,
    int Percent,
    UiText? TicketNote,
    bool TicketsLocked,
    UiText? StateNote,
    bool CanResume,
    string? StatePath,
    CostSummary? Confirmation,
    bool ShowsProgress);

/// <summary>
/// The run screen (design 4.3). It decides nothing about the run itself — that lives in
/// <see cref="PublicationRun"/>, which survives this screen — and only turns the run's state into
/// what a person reads.
/// </summary>
public sealed class RunModel(PublicationRun run, TimeProvider? time = null)
{
    /// <summary>
    /// The stage line while a resume's cost waits for the person: the run is stopped on the answer, so it
    /// shows no progress, rate or elapsed time (spec amendment 3).
    /// </summary>
    public static UiText AwaitingConfirmationText => RunText.AwaitingConfirmation;

    /// <summary>The window the rate is measured over (spec B21).</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(30);

    /// <summary>The Batch path's rate before thirty seconds of data: the platform's conservative figure (spec B21).</summary>
    public const double BatchPriorMemosPerSecond = 70;

    /// <summary>One settle per pass still to come: about three ledgers, the landing and the two settle ledgers (spec B21).</summary>
    public static readonly TimeSpan SettlePerPass = LedgerTiming.ApproximateLedgerInterval * 3;

    /// <summary>
    /// How long a fence wait's ledgers are watched before their measured pace replaces
    /// <see cref="LedgerTiming.ApproximateLedgerInterval"/>. Shorter than <see cref="RateWindow"/>:
    /// ledgers close at a steady pace, unlike memos, and the whole wait is only a minute or two.
    /// </summary>
    public static readonly TimeSpan FencePaceWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A measured rate under this share of the best one the stage has shown reads as a stall. A Batch
    /// round waiting on one refused outer keeps a trickle in its window, and on testnet that read "1.1
    /// chunks/s · about 1 h 35 min left" for 30 s of a run that went on at full speed a minute later.
    /// </summary>
    public const double StallShareOfBestRate = 0.1;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _samplesGate = new();
    private readonly List<ProgressSample> _samples = [];
    private DateTimeOffset? _sampledRun;
    private string? _sampledStage;
    private long _stageOpeningDone;
    private double _bestRate;

    private readonly record struct ProgressSample(DateTimeOffset At, long Done);

    /// <summary>The progress line for this snapshot now, the rate measured from what this model has seen.</summary>
    internal UiText Describe(RunSnapshot snapshot)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return StageTextOf(snapshot, now, snapshot.State == PublicationRunState.Running ? RateOf(snapshot, now) : null);
    }

    /// <summary>
    /// Progress per second in the current stage of this run, or null until there is enough of it to
    /// measure: memos per second over the last <see cref="RateWindow"/>, or during a fence, ledgers per
    /// second once ledgers have closed over <see cref="FencePaceWindow"/>. Samples are taken as the
    /// screen renders, once a second.
    /// </summary>
    /// <remarks>
    /// Data starts at the stage's first progress, not at the stage's start: a Batch run spends its
    /// first seconds signing and filling the window with nothing landed, and measuring those as part
    /// of the rate showed 3 chunks/s and 35 minutes left for a run that finished three minutes later.
    /// Samples belong to one run, keyed by its start, so a new run in the same stage never inherits the
    /// last one's. The newest sample at least a window old is always kept, so a screen that comes back
    /// after a long gap measures across the gap instead of starting over. A rate under
    /// <see cref="StallShareOfBestRate"/> of the best this stage has shown is returned as zero, which
    /// the line shows as a stall rather than as hours left.
    /// </remarks>
    private double? RateOf(RunSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Progress is not { } progress) return null;

        lock (_samplesGate)
        {
            if (_sampledRun != snapshot.StartedAt || !string.Equals(_sampledStage, progress.Stage, StringComparison.Ordinal))
            {
                _samples.Clear();
                _sampledRun = snapshot.StartedAt;
                _sampledStage = progress.Stage;
                _stageOpeningDone = progress.Done;
                _bestRate = 0;
            }

            if (progress.Done <= _stageOpeningDone) return null;

            _samples.Add(new ProgressSample(now, progress.Done));

            if (progress.Stage != "fence")
            {
                int anchor = _samples.FindLastIndex(sample => now - sample.At >= RateWindow);
                if (anchor > 0) _samples.RemoveRange(0, anchor);

                ProgressSample start = _samples[0];
                if (now - start.At < RateWindow) return null;

                double rate = (progress.Done - start.Done) / (now - start.At).TotalSeconds;
                _bestRate = Math.Max(_bestRate, rate);

                return rate < _bestRate * StallShareOfBestRate ? 0 : rate;
            }

            // The pace over the whole wait, which lasts a minute or two, between the first ledger close
            // and the moment the latest one was seen: the session stops reporting once the fence is
            // behind it, and measuring up to now would then slow the pace and grow the wait while
            // nothing is waited for.
            ProgressSample from = _samples[0];
            ProgressSample latest = _samples[_samples.FindIndex(sample => sample.Done == progress.Done)];
            TimeSpan span = latest.At - from.At;

            return span < FencePaceWindow ? null : (latest.Done - from.Done) / span.TotalSeconds;
        }
    }

    /// <summary>
    /// The progress line (spec 6.3): "Writing the file — 1,240 of 7,124 · 52 chunks/s · about 2 min left ·
    /// 0:24 elapsed", and "Starting · 0:05 elapsed" until the first report. On the Batch path that
    /// comes when the pass's first batch is answered, with nothing found landed yet. Time left is what remains at the measured rate plus the settles still to
    /// come (spec B21; <see cref="SettlesAfter"/>), and the read-back's own blocks at their rate; before a rate is measured the Batch path uses its prior and shows no rate, the
    /// ticket path says "estimating…", and so does any stage whose measured rate shows as zero. A
    /// resume's fence wait has its own line, where <paramref name="rate"/> is ledgers per second. A run
    /// that has stopped shows how long it took, never a rate or a time left.
    /// </summary>
    public static UiText StageTextOf(RunSnapshot snapshot, DateTimeOffset now, double? rate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.State is PublicationRunState.Succeeded or PublicationRunState.Failed or PublicationRunState.Cancelled)
        {
            return StoppedTextOf(snapshot);
        }

        // The run is stopped on the person's answer, so a clock or a rate here would read as work in progress.
        if (snapshot.Confirmation is not null) return RunText.AwaitingConfirmation;

        UiText WithElapsed(UiText line) => snapshot.StartedAt is { } started
            ? UiText.Of(RunText.LineElapsed, ("line", line), ("clock", Clock(now - started)))
            : line;

        // Connecting, checking the accounts and signing the first batches report nothing; the clock
        // shows that a run which says this for ten seconds is still moving.
        if (snapshot.Progress is not { } progress) return WithElapsed(RunText.Starting);

        if (progress.Stage == "fence")
        {
            long ledgers = Math.Max(0, progress.Total - progress.Done);

            // The approximate interval is mainnet's pace; testnet closes faster, so a measured pace wins.
            TimeSpan wait = rate is { } pace && pace > 0
                ? TimeSpan.FromSeconds(ledgers / pace)
                : LedgerTiming.ApproximateLedgerInterval * ledgers;

            return UiText.Of(RunText.LineFence, ("stage", StageNameOf("fence")), ("wait", DurationText.Of(wait)));
        }

        UiText head = HeadOf(progress, inSentence: false);

        if (progress.Stage == "read-back")
        {
            long blocks = Math.Max(0, progress.Total - progress.Done);
            TimeSpan left = rate is { } blocksPerSecond && blocksPerSecond > 0
                ? TimeSpan.FromSeconds(blocks / blocksPerSecond)
                : PublicationDuration.Reading(blocks * PublicationDuration.RecordsPerSampledBlock);

            return WithElapsed(blocks == 0 ? head : TimeLeft(head, left));
        }

        if (progress.Stage is not ("chunks" or "lists")) return WithElapsed(head);

        long remaining = Math.Max(0, progress.Total - progress.Done);

        // Every memo of the pass is in; the pass still settles and the writer still has work, so no
        // figure of seconds is claimed for it (Spec question 23).
        if (remaining == 0) return WithElapsed(UiText.Of(RunText.LineFinishingPass, ("head", head)));
        TimeSpan settles = SettlesAfter(progress.Stage, remaining, snapshot.FileBytes, snapshot.UsesBatch);

        if (rate is { } measured && RateOf(progress.Stage, measured) is { } shown)
        {
            return WithElapsed(UiText.Of(
                RunText.LineRateTimeLeft,
                ("head", head),
                ("rate", shown),
                ("left", DurationText.Of(TimeSpan.FromSeconds(remaining / measured) + settles))));
        }

        if (rate is null && snapshot.UsesBatch)
        {
            return WithElapsed(TimeLeft(head, TimeSpan.FromSeconds(remaining / BatchPriorMemosPerSecond) + settles));
        }

        return WithElapsed(UiText.Of(RunText.LineEstimating, ("head", head)));
    }

    private static UiText TimeLeft(UiText head, TimeSpan left) =>
        UiText.Of(RunText.LineTimeLeft, ("head", head), ("left", DurationText.Of(left)));

    /// <summary>
    /// The line of a run that has stopped: how long it ran, frozen at the moment it stopped, and for a
    /// run that did not finish, where it stopped. The outcome itself is the alert below the line.
    /// </summary>
    private static UiText StoppedTextOf(RunSnapshot snapshot)
    {
        string? took = snapshot is { StartedAt: { } started, FinishedAt: { } finished } ? Clock(finished - started) : null;

        // The whole publication's working time: the sessions before this one, and this one without its wait for the person.
        if (snapshot.State == PublicationRunState.Succeeded)
        {
            return snapshot is { StartedAt: { } began, FinishedAt: { } ended }
                ? UiText.Of(RunText.FinishedIn, ("clock", Clock(snapshot.WorkedBefore + (ended - began - snapshot.WaitedForPerson))))
                : RunText.Finished;
        }

        UiText activity = snapshot.Progress switch
        {
            null => RunText.MidStarting,
            { Stage: "fence" } => StageInSentence("fence"),
            { } progress => HeadOf(progress, inSentence: true),
        };
        UiText where = UiText.Of(RunText.StoppedWhile, ("activity", activity));

        return took is null ? where : UiText.Of(RunText.LineElapsed, ("line", where), ("clock", took));
    }

    /// <summary>
    /// A rate as shown: whole numbers from 10 up, one decimal below that, so the ticket path's 1.3
    /// chunks/s is not shown as 1 or 0. Null when even one decimal shows zero: that is a stall, not a
    /// rate, and a time left built from it would run to days.
    /// </summary>
    private static UiText? RateOf(string stage, double perSecond)
    {
        bool chunks = stage == "chunks";
        double tenths = Math.Round(perSecond, 1, MidpointRounding.AwayFromZero);

        if (tenths >= 10) return UiText.Of(chunks ? RunText.RateChunksWhole : RunText.RateListsWhole, ("rate", perSecond));

        return tenths > 0 ? UiText.Of(chunks ? RunText.RateChunksTenths : RunText.RateListsTenths, ("rate", tenths)) : null;
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
            PublicationState state = PublicationState.Load(path);
            return !string.Equals(state.Phase, "done", StringComparison.Ordinal) && state.Failure is null && state.ManifestStatus is null;
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The settles still to come (spec S11, B21): on the Batch path one per round — the rounds of this
    /// pass after the one in progress, and every round of the passes after it; on the ticket path one
    /// per pass still to come.
    /// </summary>
    private static TimeSpan SettlesAfter(string stage, long remaining, long fileBytes, bool batch)
    {
        if (!batch) return SettlePerPass * PassesAfter(stage, fileBytes);

        // A list pass of more than 16,384 memos would need about 170 GB of file (some ten entries to a list memo,
        // a block of 1,024,000 bytes to each); within 16 GiB, one round per later pass is exact.
        long thisPass = Math.Max(0, (remaining + BatchStrategyOptions.DefaultMaxRoundMemos - 1) / BatchStrategyOptions.DefaultMaxRoundMemos - 1);
        return PublicationDuration.SettlePerRound * (thisPass + PassesAfter(stage, fileBytes));
    }

    /// <summary>The passes still to come after this stage: a list pass when the file has more than one block, and the manifest.</summary>
    private static int PassesAfter(string stage, long fileBytes)
    {
        bool lists = fileBytes > 0 && FileTree.BlockCount(FileTree.ChunkCount(fileBytes, MemoLayout.ChunkBytes)) > 1;
        return stage == "chunks" ? (lists ? 2 : 1) : 1;
    }

    /// <summary>
    /// A stage with its count (spec 5.3): hashing counts chunks of the file's total; the history pass
    /// counts records and has no total, because how far back the writer's history goes is what it is
    /// finding out. <paramref name="inSentence"/> names the stage for the middle of "Stopped while …".
    /// </summary>
    private static UiText HeadOf(PublishProgress progress, bool inSentence)
    {
        UiText stage = inSentence ? StageInSentence(progress.Stage) : StageNameOf(progress.Stage);

        return progress.Stage switch
        {
            "hashing" => UiText.Of(RunText.HeadHashing, ("stage", stage), ("done", progress.Done), ("total", progress.Total)),
            "history" => progress.Done > 0 ? UiText.Of(RunText.HeadRecords, ("stage", stage), ("count", progress.Done)) : stage,
            _ => UiText.Of(RunText.HeadProgress, ("stage", stage), ("done", progress.Done), ("total", progress.Total)),
        };
    }

    private static string Lowered(string text) =>
        text.Length == 0 ? text : string.Concat(char.ToLowerInvariant(text[0]).ToString(), text.AsSpan(1));

    private static string Clock(TimeSpan span)
    {
        TimeSpan whole = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(span.TotalSeconds)));
        return whole.TotalHours >= 1
            ? $"{(int)whole.TotalHours}:{whole.Minutes:00}:{whole.Seconds:00}"
            : $"{(int)whole.TotalMinutes}:{whole.Seconds:00}";
    }

    public RunSnapshot Snapshot => run.Snapshot;

    /// <summary>
    /// Whether there is a run to show at all, running or finished. <see cref="PublicationRunState.Idle"/>
    /// means nothing has started yet, or a finished run's outcome was put away by <see cref="Dismiss"/>.
    /// The Publish tab asks <see cref="IsRunning"/> and <see cref="IsFinished"/> instead, because it
    /// draws the two differently.
    /// </summary>
    public bool IsActive => Snapshot.State != PublicationRunState.Idle;

    /// <summary>
    /// What the window asks before closing over a running publication (spec 6.1, I11). A ticketed
    /// run names the only two ways its tickets ever go, and promises neither: a later publication
    /// tries, and closing the account always removes them. A run without tickets reads as it always did.
    /// </summary>
    public static UiText CloseConfirmation(RunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        object name = snapshot.Name ?? (object)RunText.CloseDefaultName;

        return snapshot.UsesTickets
            ? UiText.Of(RunText.CloseConfirmTickets, ("name", name), ("reserve", new XrpAmount(snapshot.OwnerReserveDrops)))
            : UiText.Of(RunText.CloseConfirmPlain, ("name", name));
    }

    public bool CanCancel => Snapshot.State == PublicationRunState.Running;

    /// <summary>
    /// Whether a publication is under way. Only then does the Publish tab show the run alone; a
    /// finished run's outcome sits above the picker, so it never stands between the person and the
    /// next publication.
    /// </summary>
    public bool IsRunning => Snapshot.State == PublicationRunState.Running;

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

    public UiText StageText => Describe(run.Snapshot);

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
    public UiText? HistoryNote => Snapshot.HistoryNote;

    /// <summary>
    /// Why the run ended badly, or <see langword="null"/> on anything else — including a success
    /// that still carries a <see cref="HistoryNote"/>. Never set together with a non-null
    /// <see cref="Link"/>: <see cref="PublicationRun"/> only ever populates one of
    /// <see cref="RunSnapshot.Report"/> or <see cref="RunSnapshot.Failure"/> for a finished run.
    /// </summary>
    public UiText? Failure => Snapshot.Failure;

    public void Cancel() => run.Cancel();

    /// <summary>Goes on with a resume that waits for the person (spec S25); false when none is waiting.</summary>
    public bool ConfirmResume() => run.AnswerConfirmation(true);

    /// <summary>Stops a resume that waits for the person before anything is sent (spec S25); false when none is waiting.</summary>
    public bool DeclineResume() => run.AnswerConfirmation(false);

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
            CanCancel: snapshot.State == PublicationRunState.Running && snapshot.Confirmation is null,
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
            StatePath: snapshot.StatePath,
            Confirmation: snapshot.Confirmation,
            ShowsProgress: snapshot.Confirmation is null);
    }

    /// <inheritdoc cref="TicketNoteOf"/>
    public UiText? TicketNote => TicketNoteOf(Snapshot);

    /// <inheritdoc cref="TicketsLockedOf"/>
    public bool TicketsLocked => TicketsLockedOf(Snapshot);

    /// <inheritdoc cref="StateNoteOf"/>
    public UiText? StateNote => StateNoteOf(Snapshot);

    /// <summary>
    /// What the tidy-up after a succeeded publication did with this library's leftover tickets
    /// (spec 4.6), or <see langword="null"/> when it found none or the run did not succeed. Released
    /// and already-gone tickets are told together: either way their reserve is free. A tidy-up whose
    /// counts were lost — it failed, or a cancellation cut it short — does not claim to know how many
    /// tickets stay, since all-zero counts then say nothing about the account. Tickets that stay are
    /// named with the only two ways they ever go, as the close confirmation names them.
    /// </summary>
    public static UiText? TicketNoteOf(RunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (FinishedReport(snapshot) is not { } report) return null;

        XrpAmount reserve = new(snapshot.OwnerReserveDrops);

        if (report.TicketReleaseFeeDrops is null) return UiText.Of(RunText.TicketsUnfinished, ("reserve", reserve));

        int released = report.TicketsReleased + report.TicketsAlreadyGone;
        int locked = report.TicketsNotReleased;

        if (locked == 0) return released == 0 ? null : UiText.Of(RunText.TicketsReleased, ("count", released));

        UiText stay = UiText.Of(RunText.TicketsStay, ("count", locked), ("reserve", reserve));

        UiText lead = released == 0
            ? UiText.Of(RunText.TicketsNoneReleased, ("count", locked))
            : UiText.Of(RunText.TicketsSomeReleased, ("count", released), ("locked", locked));

        return UiText.Of(RunText.TicketsJoined, ("lead", lead), ("stay", stay));
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
    public static UiText? StateNoteOf(RunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return FinishedReport(snapshot)?.StateSaveFailure is null ? null : RunText.StateNote;
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
    public static UiText StageNameOf(string stage) => stage switch
    {
        "hashing" => RunText.StageHashing,
        "history" => RunText.StageHistory,
        "chunks" => RunText.StageChunks,
        "lists" => RunText.StageLists,
        "read-back" => RunText.StageReadBack,
        "manifest" => RunText.StageManifest,
        "fence" => RunText.StageFence,
        "done" => RunText.StageDone,

        // Not an exception: a stage this application does not know about is still a stage the run is
        // genuinely in, and showing its raw name beats showing nothing or crashing the screen.
        _ => UiText.Of(RunText.StageUnknown, ("stage", stage)),
    };

    /// <summary>
    /// <see cref="StageNameOf"/> for the middle of a sentence ("Stopped while …"), where English lowers
    /// the first letter and another language may need another form altogether.
    /// </summary>
    private static UiText StageInSentence(string stage) => stage switch
    {
        "hashing" => RunText.MidHashing,
        "history" => RunText.MidHistory,
        "chunks" => RunText.MidChunks,
        "lists" => RunText.MidLists,
        "read-back" => RunText.MidReadBack,
        "manifest" => RunText.MidManifest,
        "fence" => RunText.MidFence,
        "done" => RunText.MidDone,
        _ => UiText.Of(RunText.StageUnknown, ("stage", Lowered(stage))),
    };
}
