namespace XrplFile;

/// <summary>
/// How many transmissions may be in flight on the connection (spec B10): it starts at the given size,
/// halves after three busy answers in a row and grows by one after ten clean ones, never past the
/// maximum. A public node closes a client at about a hundred unsent answers; this is what keeps the
/// client well below that. Growth alone would not: on testnet a 7 MB run's clean answers widened it
/// to 105, and 95 were in flight at once.
/// </summary>
internal sealed class BatchWindow(int initial, int maximum = BatchWindow.DefaultMaximum)
{
    /// <summary>The spike's ceiling: 32 in flight gave its best testnet rate, far below the node's limit.</summary>
    public const int DefaultMaximum = 32;

    private readonly Lock _gate = new();
    private readonly Queue<TaskCompletionSource> _waiting = new();
    private readonly int _maximum = Math.Max(1, maximum);
    private int _size = Math.Clamp(initial, 1, Math.Max(1, maximum));
    private int _inFlight;
    private int _busyInRow;
    private int _cleanInRow;

    public int Size
    {
        get { lock (_gate) return _size; }
    }

    public int InFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    public Task AcquireAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_inFlight < _size && _waiting.Count == 0)
            {
                _inFlight++;
                return Task.CompletedTask;
            }

            TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            _ = waiter.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _waiting.Enqueue(waiter);

            return waiter.Task;
        }
    }

    public void Release(bool busy)
    {
        lock (_gate)
        {
            _inFlight--;

            if (busy)
            {
                _cleanInRow = 0;

                if (++_busyInRow >= 3)
                {
                    _size = Math.Max(1, _size / 2);
                    _busyInRow = 0;
                }
            }
            else
            {
                _busyInRow = 0;

                if (++_cleanInRow >= 10)
                {
                    _size = Math.Min(_maximum, _size + 1);
                    _cleanInRow = 0;
                }
            }

            WakeUnderLock();
        }
    }

    /// <summary>Gives back a slot that no transmission used (a batch discarded unsent): neither busy nor clean.</summary>
    public void Return()
    {
        lock (_gate)
        {
            _inFlight--;
            WakeUnderLock();
        }
    }

    /// <summary>Called under the gate. A cancelled waiter refuses the slot (TrySetResult fails) and is skipped.</summary>
    private void WakeUnderLock()
    {
        while (_inFlight < _size && _waiting.TryDequeue(out TaskCompletionSource? next))
        {
            if (next.TrySetResult()) _inFlight++;
        }
    }
}
