namespace XrplFile.Tests;

/// <summary>A strategy that can stop at one of its SubmitAllAsync calls (1-based): hold there until resumed, or die there.</summary>
internal sealed class ScriptedStrategy(ISubmissionStrategy inner) : ISubmissionStrategy
{
    private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public int PauseAtCall { get; init; }

    public int FailAtCall { get; init; }

    public Task Paused => _paused.Task;

    public long FeeDropsPerTransaction => inner.FeeDropsPerTransaction;

    public string Account => inner.Account;

    public int TicketMints => inner.TicketMints;

    public void Resume() => _resume.TrySetResult();

    public async Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
    {
        int call = Interlocked.Increment(ref _calls);

        if (call == FailAtCall) throw new XrplFileException(XrplFileError.NodeUnreachable, "scripted: the process died here");

        if (call == PauseAtCall)
        {
            _paused.TrySetResult();
            await _resume.Task.WaitAsync(cancellationToken);
        }

        return await inner.SubmitAllAsync(memos, submitted, cancellationToken);
    }

    public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken) => inner.ReleaseLeftoverTicketsAsync(cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
