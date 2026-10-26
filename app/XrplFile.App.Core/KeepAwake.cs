namespace XrplFile.App.Core;

/// <summary>Keeps the computer from sleeping while a publication runs (spec S13).</summary>
public interface IKeepAwake
{
    /// <summary>Starts holding the computer awake. Does nothing while already held.</summary>
    void Acquire();

    /// <summary>Lets the computer sleep again. Idempotent.</summary>
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
/// </summary>
public sealed class ThreadKeepAwake(IExecutionState state, Action<string>? log = null) : IKeepAwake
{
    private readonly Lock _gate = new();
    private Thread? _thread;
    private ManualResetEventSlim? _release;

    /// <inheritdoc />
    public void Acquire()
    {
        lock (_gate)
        {
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
        bool held = false;

        try
        {
            held = state.Require();
            if (!held) log?.Invoke("keep-awake: SetThreadExecutionState was refused; the computer may sleep during this run");
        }
        catch (Exception error)
        {
            log?.Invoke($"keep-awake: SetThreadExecutionState failed: {error.Message}");
        }

        release.Wait();

        if (!held) return;

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
