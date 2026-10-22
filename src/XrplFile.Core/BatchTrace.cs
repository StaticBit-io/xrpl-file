using System.Diagnostics;

namespace XrplFile;

/// <summary>What a Batch run did, in order, and how long it spent signing. Read by tests and by the 1 MB stand measurement.</summary>
internal sealed class BatchTrace
{
    private readonly Lock _gate = new();
    private readonly List<string> _events = [];
    private long _signingTicks;

    public IReadOnlyList<string> Events
    {
        get { lock (_gate) return [.. _events]; }
    }

    public TimeSpan SigningTime => TimeSpan.FromTicks(Interlocked.Read(ref _signingTicks));

    public void Add(string entry)
    {
        lock (_gate) _events.Add(entry);
    }

    public async Task<T> TimeSigningAsync<T>(Func<Task<T>> signing)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            return await signing().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Add(ref _signingTicks, Stopwatch.GetElapsedTime(started).Ticks);
        }
    }
}
