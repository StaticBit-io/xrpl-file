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
public sealed record ReadSnapshot(
    ReadState State,
    int BlocksDone,
    int BlocksTotal,
    long BytesDone,
    long BytesTotal,
    string? Name,
    string? Failure,
    bool Saved);
