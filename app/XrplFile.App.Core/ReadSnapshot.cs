namespace XrplFile.App.Core;

public enum ReadState
{
    Idle,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// One consistent view of a read. A screen takes this rather than reading fields one at a time:
/// separate reads could see <see cref="ReadState.Succeeded"/> with a block count from the middle of
/// the run. Part C2 had to fix exactly that twice.
/// </summary>
/// <param name="Saving">
/// Whether a <see cref="ReadRun.SaveAsync"/> call is currently between opening the temporary file
/// and the platform picker returning. Read.razor uses it to disable the Save button while one is
/// already in flight — the generation pin already stops a stale write from corrupting
/// <see cref="Saved"/>/<see cref="Failure"/>, so nothing here is a data hazard, but a screen is not
/// supposed to leave a control enabled for the duration of an operation it is already running.
/// </param>
public sealed record ReadSnapshot(
    ReadState State,
    int BlocksDone,
    int BlocksTotal,
    long BytesDone,
    long BytesTotal,
    string? Name,
    string? Failure,
    bool Saved,
    bool Saving)
{
    /// <summary>
    /// Whether the read has ended, for whatever reason. Read.razor asks this rather than comparing
    /// <see cref="State"/> against three named values itself — the same decision
    /// <c>RunModel.IsFinished</c> makes for <c>PublicationRun</c> (design C2), moved here because
    /// this screen has no wrapping model of its own to hold it on.
    /// </summary>
    /// <remarks>
    /// Written as a switch that throws on anything it has not been told about, rather than
    /// <c>is A or B or C</c>, which reads more nicely and checks nothing: a sixth
    /// <see cref="ReadState"/> would simply fall out of that as <see langword="false"/> and reach a
    /// screen as a wrong answer. The throw is unreachable by design — the test that walks every
    /// member of the enum is what makes it so, and is what fails the day somebody adds one — which
    /// is the same trade <c>ReadErrorMessages</c> already makes for the same reason.
    /// </remarks>
    public bool IsFinished => State switch
    {
        ReadState.Succeeded or ReadState.Failed or ReadState.Cancelled => true,
        ReadState.Idle or ReadState.Running => false,
        _ => throw new ArgumentOutOfRangeException(nameof(State), State, "This state has not been told whether it has ended."),
    };
}
