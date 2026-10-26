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
internal sealed class BatchTrace(Action<BatchTraceEntry>? listener = null)
{
    /// <summary>The entries kept (spec S18); a session-wide list grew with the run.</summary>
    public const int Capacity = 10_000;

    private readonly Lock _gate = new();
    private readonly BatchTraceEntry[] _ring = new BatchTraceEntry[Capacity];
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _total;
    private long _signingTicks;

    /// <summary>Entries ever added, the dropped ones included.</summary>
    public long Total
    {
        get { lock (_gate) return _total; }
    }

    public IReadOnlyList<string> Events => [.. Entries.Select(entry => entry.Event)];

    /// <summary>The last <see cref="Capacity"/> entries, oldest first.</summary>
    public IReadOnlyList<BatchTraceEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                int kept = (int)Math.Min(_total, Capacity);
                int oldest = (int)((_total - kept) % Capacity);
                BatchTraceEntry[] copy = new BatchTraceEntry[kept];

                for (int i = 0; i < kept; i++) copy[i] = _ring[(oldest + i) % Capacity];

                return copy;
            }
        }
    }

    public TimeSpan SigningTime => TimeSpan.FromTicks(Interlocked.Read(ref _signingTicks));

    public void Add(string entry)
    {
        // Under the gate, so the entries' times never go backwards in their order and the listener hears them in that order.
        lock (_gate)
        {
            BatchTraceEntry added = new(Stopwatch.GetElapsedTime(_started), entry);
            _ring[(int)(_total % Capacity)] = added;
            _total++;
            listener?.Invoke(added);
        }
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
