namespace XrplFile.App.Core;

/// <summary>
/// Keeps the vault's idle lock from firing while a run works (spec S14): a touch at once, then one
/// a minute, until disposed. Progress reports touch too, but a run can wait minutes in one await
/// that reports nothing. A touch racing an idle lock already firing changes nothing; the run then
/// meets a locked vault at its next signature and ends resumable.
/// </summary>
public sealed class ActivityLease : IDisposable
{
    /// <summary>Well inside the five-minute idle window, so one late tick cannot let it lapse.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly ITimer _timer;

    private ActivityLease(Action touch, TimeProvider time)
    {
        touch();
        _timer = time.CreateTimer(_ => touch(), null, Interval, Interval);
    }

    public static ActivityLease Start(Action touch, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(touch);
        ArgumentNullException.ThrowIfNull(time);

        return new ActivityLease(touch, time);
    }

    public void Dispose() => _timer.Dispose();
}
