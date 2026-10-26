namespace XrplFile.App.Core;

/// <summary>
/// Keeps the computer from sleeping while a publication runs (spec S13) or a file is read (spec S15).
/// Holders overlap: every <see cref="Acquire"/> is paired with one <see cref="Release"/>, and the computer
/// is held for as long as any holder has not released.
/// </summary>
public interface IKeepAwake
{
    /// <summary>Takes a hold on the computer's wakefulness; the first hold starts holding it awake.</summary>
    void Acquire();

    /// <summary>Gives up one hold; the last one lets the computer sleep again. Does nothing when nothing is held.</summary>
    void Release();
}

/// <summary>The platform's execution-state calls, made on <see cref="ThreadKeepAwake"/>'s own thread.</summary>
public interface IExecutionState
{
    /// <summary>Asks the system to stay awake for the calling thread. False when the system refused.</summary>
    bool Require();

    /// <summary>Withdraws the calling thread's request.</summary>
    void Clear();
}

/// <summary>
/// Holds the computer awake from a thread it owns (spec S13). The execution state belongs to the
/// thread that set it, and a run's continuations move between pool threads, so the state is set
/// and cleared here, on one thread that waits between the two. A refused call is logged and does not
/// stop the run: a computer that may sleep is worse, not fatal, because the run stays resumable.
/// A publication and a read can overlap, so holds are counted: the thread lives until the last
/// holder releases, and a release by one never ends the other's hold.
/// </summary>
public sealed class ThreadKeepAwake(IExecutionState state, Action<string>? log = null) : IKeepAwake
{
    private readonly Lock _gate = new();
    private Thread? _thread;
    private ManualResetEventSlim? _release;
    private int _holders;

    /// <inheritdoc />
    public void Acquire()
    {
        lock (_gate)
        {
            _holders++;
            if (_thread is not null) return;

            ManualResetEventSlim release = new(initialState: false);
            Thread thread = new(() => Hold(release)) { IsBackground = true, Name = "xrpl-file keep-awake" };
            _release = release;
            _thread = thread;
            thread.Start();
        }
    }

    /// <inheritdoc />
    public void Release()
    {
        Thread? thread;
        ManualResetEventSlim? release;

        lock (_gate)
        {
            if (_holders == 0) return;
            if (--_holders > 0) return;

            thread = _thread;
            release = _release;
            _thread = null;
            _release = null;
        }

        if (thread is null || release is null) return;

        release.Set();

        // Joined so that "released" means cleared: a run that ends and a next one that starts must
        // not see this thread's request outlive the run it was for.
        thread.Join();
        release.Dispose();
    }

    private void Hold(ManualResetEventSlim release)
    {
        try
        {
            if (!state.Require()) log?.Invoke("keep-awake: SetThreadExecutionState was refused; the computer may sleep during this run");
        }
        catch (Exception error)
        {
            log?.Invoke($"keep-awake: SetThreadExecutionState failed: {error.Message}");
        }

        release.Wait();

        // Cleared whatever Require returned: withdrawing a request that was never made is harmless, and
        // the release then does not depend on what the first call on a fresh thread reports.
        try
        {
            state.Clear();
        }
        catch (Exception error)
        {
            log?.Invoke($"keep-awake: clearing the execution state failed: {error.Message}");
        }
    }
}
