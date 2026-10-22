using System.Diagnostics;

namespace XrplFile;

/// <summary>
/// One event of a Batch run's trace (<see cref="BatchStrategy.TraceEntries"/>): what happened, and when,
/// counted from the strategy's construction.
/// </summary>
/// <param name="Elapsed">Time since the strategy was built.</param>
/// <param name="Event">
/// <c>round:</c>, <c>register:</c>, <c>transmit:</c>, <c>resend:</c>, <c>unsent:</c>, <c>discard:</c>,
/// <c>halt:</c>, <c>settled:</c> or <c>fee-wait:</c>, followed by ordinals, ledger indexes, fee units
/// and engine results. It never carries a key, a signature or a signed blob.
/// </param>
public readonly record struct BatchTraceEntry(TimeSpan Elapsed, string Event);

/// <summary>What a Batch run did, in order, and how long it spent signing. Read by tests, the 1 MB stand measurement and the application's trace log.</summary>
internal sealed class BatchTrace
{
    private readonly Lock _gate = new();
    private readonly List<BatchTraceEntry> _entries = [];
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _signingTicks;

    public IReadOnlyList<string> Events
    {
        get { lock (_gate) return [.. _entries.Select(entry => entry.Event)]; }
    }

    public IReadOnlyList<BatchTraceEntry> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }

    public TimeSpan SigningTime => TimeSpan.FromTicks(Interlocked.Read(ref _signingTicks));

    public void Add(string entry)
    {
        // Taken under the gate, so the entries' times never go backwards in their order.
        lock (_gate) _entries.Add(new BatchTraceEntry(Stopwatch.GetElapsedTime(_started), entry));
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
