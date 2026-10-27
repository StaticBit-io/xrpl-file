using System.Collections.Concurrent;
using Xunit;

namespace XrplFile.App.Core.Tests;

/// <summary>Records which thread made each call, so a test can tell the state was set and cleared by one thread.</summary>
internal sealed class RecordingExecutionState(bool succeeds = true) : IExecutionState
{
    private readonly ConcurrentQueue<(string Call, int Thread)> _calls = new();

    public IReadOnlyList<(string Call, int Thread)> Calls => [.. _calls];

    public bool Require()
    {
        _calls.Enqueue(("require", Environment.CurrentManagedThreadId));
        return succeeds;
    }

    public void Clear() => _calls.Enqueue(("clear", Environment.CurrentManagedThreadId));
}

/// <summary>A keep-awake whose release fails, as a platform call may: counts the calls so a test sees the release was tried.</summary>
internal sealed class ThrowingReleaseKeepAwake : IKeepAwake
{
    private int _acquired;
    private int _released;

    public int Acquired => Volatile.Read(ref _acquired);

    public int Released => Volatile.Read(ref _released);

    public void Acquire() => Interlocked.Increment(ref _acquired);

    public void Release()
    {
        Interlocked.Increment(ref _released);
        throw new InvalidOperationException("fake: the platform refused to let the computer sleep again");
    }
}

public sealed class ThreadKeepAwakeTests
{
    [Fact]
    public void The_state_is_set_and_cleared_by_one_thread_that_is_not_the_callers()
    {
        RecordingExecutionState state = new();
        ThreadKeepAwake keepAwake = new(state);

        keepAwake.Acquire();
        keepAwake.Release();

        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
        Assert.Equal(state.Calls[0].Thread, state.Calls[1].Thread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, state.Calls[0].Thread);
    }

    [Fact]
    public void Release_twice_clears_once_and_acquire_twice_holds_once()
    {
        RecordingExecutionState state = new();
        ThreadKeepAwake keepAwake = new(state);

        keepAwake.Acquire();
        keepAwake.Acquire();
        keepAwake.Release();
        keepAwake.Release();

        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
    }

    [Fact]
    public void A_refused_call_is_logged_and_nothing_fails()
    {
        List<string> log = [];
        ThreadKeepAwake keepAwake = new(new RecordingExecutionState(succeeds: false), line => { lock (log) log.Add(line); });

        keepAwake.Acquire();
        keepAwake.Release();

        Assert.Single(log);
        Assert.Contains("SetThreadExecutionState", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_request_is_still_cleared_on_release()
    {
        RecordingExecutionState state = new(succeeds: false);
        ThreadKeepAwake keepAwake = new(state);

        keepAwake.Acquire();
        keepAwake.Release();

        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
        Assert.Equal(state.Calls[0].Thread, state.Calls[1].Thread);
    }

    [Fact]
    public async Task The_state_is_held_while_any_holder_holds_it_and_cleared_at_the_last_release()
    {
        RecordingExecutionState state = new();
        ThreadKeepAwake keepAwake = new(state);

        // A publication and a read: whichever finishes first, the other is still covered.
        keepAwake.Acquire();
        keepAwake.Acquire();
        await BatchTestWait.UntilAsync(() => state.Calls.Count == 1);

        keepAwake.Release();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["require"], state.Calls.Select(call => call.Call));

        keepAwake.Release();

        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
        Assert.Equal(state.Calls[0].Thread, state.Calls[1].Thread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, state.Calls[0].Thread);
    }

    [Fact]
    public async Task A_holder_that_starts_while_another_is_held_shares_the_one_request()
    {
        RecordingExecutionState state = new();
        ThreadKeepAwake keepAwake = new(state);

        keepAwake.Acquire();
        await BatchTestWait.UntilAsync(() => state.Calls.Count == 1);
        keepAwake.Acquire();
        keepAwake.Release();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["require"], state.Calls.Select(call => call.Call));

        keepAwake.Release();

        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
    }

    [Fact]
    public void A_release_with_nothing_held_does_not_take_a_later_hold_away()
    {
        RecordingExecutionState state = new();
        ThreadKeepAwake keepAwake = new(state);

        keepAwake.Release();
        keepAwake.Acquire();
        keepAwake.Release();

        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
    }

    [Fact]
    public void It_can_be_held_again_after_a_release()
    {
        RecordingExecutionState state = new();
        ThreadKeepAwake keepAwake = new(state);

        keepAwake.Acquire();
        keepAwake.Release();
        keepAwake.Acquire();
        keepAwake.Release();

        Assert.Equal(["require", "clear", "require", "clear"], state.Calls.Select(call => call.Call));
        Assert.Equal(state.Calls[2].Thread, state.Calls[3].Thread);
    }
}
