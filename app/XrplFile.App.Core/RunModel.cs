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
    int Percent);

/// <summary>
/// The run screen (design 4.3). It decides nothing about the run itself — that lives in
/// <see cref="PublicationRun"/>, which survives this screen — and only turns the run's state into
/// what a person reads.
/// </summary>
public sealed class RunModel(PublicationRun run)
{
    public RunSnapshot Snapshot => run.Snapshot;

    /// <summary>
    /// Whether there is a run to show at all. <see cref="PublicationRunState.Idle"/> means nothing
    /// has started yet, or a finished run's outcome was already put away by <see cref="Dismiss"/> —
    /// both read as "show the picker instead of this screen", which is a decision, not a fact this
    /// application's one caller (Publish.razor) should make itself by comparing
    /// <see cref="Snapshot"/>.State to <see cref="PublicationRunState.Idle"/> in its own markup.
    /// </summary>
    public bool IsActive => Snapshot.State != PublicationRunState.Idle;

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

    public string StageText => Snapshot.Progress is { } progress
        ? $"{StageNameOf(progress.Stage)} — {progress.Done:N0} of {progress.Total:N0}"
        : "Starting";

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
            StageText: snapshot.Progress is { } progress
                ? $"{StageNameOf(progress.Stage)} — {progress.Done:N0} of {progress.Total:N0}"
                : "Starting",
            Percent: snapshot.Progress is { } percentProgress ? PercentOf(percentProgress) : 0);
    }

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
