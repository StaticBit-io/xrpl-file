namespace XrplFile.App.Core;

/// <summary>
/// The run screen (design 4.3). It decides nothing about the run itself — that lives in
/// <see cref="PublicationRun"/>, which survives this screen — and only turns the run's state into
/// what a person reads.
/// </summary>
public sealed class RunModel(PublicationRun run)
{
    public RunSnapshot Snapshot => run.Snapshot;

    public bool CanCancel => Snapshot.State == PublicationRunState.Running;

    public bool IsFinished => Snapshot.State is
        PublicationRunState.Succeeded or PublicationRunState.Failed or PublicationRunState.Cancelled;

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
