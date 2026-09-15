namespace XrplFile.Tests;

/// <summary>
/// The shared manual clock for the tests in this repository: time moves only when the test calls
/// <see cref="Advance"/>, and advancing fires every still-scheduled timer whose due time has now
/// been reached, synchronously, on the calling thread. Used by anything that needs a timer to fire
/// on demand rather than on the wall clock — <c>VaultAutoLock</c> (a real repeating period,
/// re-armed through <c>Change</c>) and <c>RevealModel</c> (a one-shot period, never re-armed) both
/// drive their timers through it. <c>Microsoft.Extensions.Time.Testing</c> is not referenced
/// anywhere in this repository, and a hand-written double is deliberate.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state, dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves the clock forward and fires every still-scheduled timer whose due time has now passed.</summary>
    public void Advance(TimeSpan by)
    {
        _now += by;

        // A snapshot: a callback firing here may go on (on its own task, never waited on by this
        // call — see VaultAutoLock's remarks) to touch this same provider through a later Touch
        // during some other test, but never through this one call's own iteration.
        foreach (ManualTimer timer in _timers.ToArray())
        {
            timer.FireIfDue(_now);
        }
    }

    private void Forget(ManualTimer timer) => _timers.Remove(timer);

    /// <summary>
    /// A due time relative to "now" at construction or at the last <see cref="Change"/>, plus the
    /// period <see cref="ITimer"/> itself defines: firing re-arms the timer one period later, and
    /// only <see cref="Timeout.InfiniteTimeSpan"/> as the period makes it one-shot. The period is
    /// honoured rather than ignored because <c>VaultAutoLock</c> now passes a real one — a double
    /// that silently dropped it would let a one-shot implementation pass a test written for a
    /// repeating window.
    /// </summary>
    private sealed class ManualTimer(
        ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private DateTimeOffset _dueAt = owner.GetUtcNow() + dueTime;
        private TimeSpan _period = period;
        private bool _disposed;
        private bool _fired;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ManualTimer));
            }

            _dueAt = owner.GetUtcNow() + dueTime;
            _period = period;
            _fired = false;
            return true;
        }

        internal void FireIfDue(DateTimeOffset now)
        {
            if (_disposed || _fired || now < _dueAt)
            {
                return;
            }

            if (_period == Timeout.InfiniteTimeSpan)
            {
                _fired = true;
            }
            else
            {
                _dueAt = now + _period;
            }

            callback(state);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.Forget(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
