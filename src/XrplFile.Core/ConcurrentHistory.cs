using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace XrplFile;

/// <summary>
/// A resume's forward walk over the writer's history, read as several ledger ranges at once.
///
/// On a public node the answer to each account_tx page is nearly all of a walk's time (436 ms of
/// every 442 on testnet, report 2026-10-27), and one walk waits for each page's marker before it can
/// ask for the next. Ranges that do not overlap each have a marker of their own, so they are asked
/// for together.
///
/// What a read yields is exactly what one forward <see cref="ILedgerSource.AccountTransactionsAsync"/>
/// over the whole range yields, in the same canonical order, given validated history that does not
/// change during the read: the range is cut into adjacent ledger segments, never splitting a ledger,
/// each segment is one forward walk, and segments are handed over whole and in ledger order. The
/// reconciliation's rules, which depend on that order, are unchanged (spec H2).
///
/// Memory is bounded whatever the file's size (spec H7): the segment the consumer reads holds at most
/// <see cref="HeadRecords"/>, and the others hand a record over only while fewer than
/// <see cref="HeldRecords"/> are held in all.
///
/// A failure reaches the consumer in the walk's order: after every record of the segments before the
/// one that failed and that segment's own records, as a single walk failing on the same page would
/// (spec H8). A consumer that stops early cancels every lane and waits for them, so no request
/// outlives the read.
///
/// A node that warns of load, or a read that fails with <see cref="XrplFileError.NodeUnreachable"/>,
/// halves the lanes for the rest of this instance's life; at one lane a read is a single walk again
/// (spec H9).
/// </summary>
internal sealed class ConcurrentHistory
{
    /// <summary>About how many records a segment is cut to hold, once the history's density is known (spec H4).</summary>
    internal const int TargetRecords = 1_000;

    /// <summary>
    /// The records waiting for the consumer below which a segment other than the one it reads may hand
    /// over another (spec H7). A Batch ledger on testnet holds up to about 2,000 of a writer's records
    /// and a ledger is never split, so three lanes ahead of the consumer need about 6,000; at 4,096 four
    /// lanes read 1.5 times as fast as one walk, at 8,192 2.4 times (testnet, 2026-10-27).
    /// </summary>
    internal const int HeldRecords = 8_192;

    /// <summary>
    /// The most the segment the consumer reads may hold of its own (spec H7). The consumer takes its
    /// records as they come, far faster than a node answers, so this only matters to a consumer slower
    /// than the node; held to two pages, it keeps the bound near <see cref="HeldRecords"/> rather than twice it.
    /// </summary>
    internal const int HeadRecords = 1_024;

    private readonly ILedgerSource _source;
    private readonly Lock _gate = new();
    private int _lanes;
    private long _warningsSeen;
    private int _peakHeld;

    /// <param name="source">The node.</param>
    /// <param name="lanes">How many segments are read at once; 1 reads the range as one walk.</param>
    public ConcurrentHistory(ILedgerSource source, int lanes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(lanes, 1);

        _source = source;
        _lanes = lanes;
        _warningsSeen = source.LoadWarnings;
    }

    /// <summary>How many segments a read may have in flight now; it only ever falls.</summary>
    public int Lanes
    {
        get { lock (_gate) return _lanes; }
    }

    /// <summary>The most records any read of this instance has held at once.</summary>
    internal int PeakHeld => Volatile.Read(ref _peakHeld);

    /// <summary>
    /// The account's transactions in [<paramref name="fromLedger"/>, <paramref name="toLedger"/>],
    /// ascending in canonical order, as one forward <see cref="ILedgerSource.AccountTransactionsAsync"/>
    /// answers.
    /// </summary>
    public IAsyncEnumerable<LedgerTransaction> ReadAsync(string account, long fromLedger, long toLedger, CancellationToken cancellationToken)
    {
        ObserveLoad();

        // A range that ends before it starts is the node's to refuse, as it always was.
        return Lanes == 1 || fromLedger > toLedger
            ? _source.AccountTransactionsAsync(account, fromLedger, toLedger, forward: true, cancellationToken)
            : ReadConcurrentlyAsync(account, fromLedger, toLedger, cancellationToken);
    }

    private async IAsyncEnumerable<LedgerTransaction> ReadConcurrentlyAsync(
        string account, long fromLedger, long toLedger, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Read read = new(this, account, fromLedger, toLedger, Lanes, cancellationToken);

        try
        {
            while (true)
            {
                LedgerTransaction? tx;

                try
                {
                    tx = await read.NextAsync().ConfigureAwait(false);
                }
                catch (XrplFileException e) when (e.Error == XrplFileError.NodeUnreachable)
                {
                    // A node that dropped the connection or refused is not helped by a retry at the
                    // same pressure.
                    Halve();
                    throw;
                }

                if (tx is null) yield break;

                yield return tx;
            }
        }
        finally
        {
            await read.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Halves the lanes when the node's load warnings grew since the last look (spec H9).</summary>
    private void ObserveLoad()
    {
        long warnings = _source.LoadWarnings;

        lock (_gate)
        {
            if (warnings <= _warningsSeen) return;

            _warningsSeen = warnings;
            _lanes = Math.Max(1, _lanes / 2);
        }
    }

    /// <summary>
    /// The span the next segment is cut to, after one of <paramref name="span"/> ledgers finished having
    /// handed over <paramref name="handed"/> records, with <paramref name="current"/> in force (spec H4).
    /// </summary>
    internal static long NextSpan(long current, long span, long handed) =>

        // An empty stretch (days between two sessions, or the ledgers after the last one) is crossed in
        // a handful of round trips rather than one per doubling; a span that lands on dense records is
        // shared by the next free lanes (spec H5).
        handed == 0
            ? Math.Max(current, Times(span, EmptyGrowth))
            : Math.Clamp(TargetRecords * span / handed, 1, Times(span, 2));

    private const long EmptyGrowth = 8;

    private static long Times(long span, long factor) => span > long.MaxValue / factor ? long.MaxValue : span * factor;

    private void Halve()
    {
        lock (_gate) _lanes = Math.Max(1, _lanes / 2);
    }

    private void RecordHeld(int held)
    {
        int peak = Volatile.Read(ref _peakHeld);

        while (held > peak)
        {
            int seen = Interlocked.CompareExchange(ref _peakHeld, held, peak);
            if (seen == peak) return;
            peak = seen;
        }
    }

    /// <summary>A ledger range read by one forward walk, and the records it has handed over that the consumer has not taken.</summary>
    private sealed class Segment(long first, long last)
    {
        public long First { get; } = first;

        /// <summary>Lowered when another lane takes the rest of its range (spec H5).</summary>
        public long Last { get; set; } = last;

        public Segment? Next { get; set; }

        /// <summary>The ledger of the last record it handed over; one before <see cref="First"/> until then.</summary>
        public long Position { get; set; } = first - 1;

        /// <summary>The ledger of the first record it handed over: its density is measured from here, not across the empty ledgers before it.</summary>
        public long FirstRecord { get; set; }

        /// <summary>Its walk is over: it reached its end, stopped past a ledger another lane took, or failed.</summary>
        public bool Finished { get; set; }

        /// <summary>Dropped because a segment before it failed: whatever its walk does now is never delivered.</summary>
        public bool Abandoned { get; set; }

        public ExceptionDispatchInfo? Failure { get; set; }

        public Queue<LedgerTransaction> Waiting { get; } = new();

        /// <summary>Every record it handed over, for the density of spec H4 and H5.</summary>
        public long Handed { get; set; }

        /// <summary>
        /// Cancelled when the read stops or an earlier segment fails, and disposed by its lane when its
        /// walk is over.
        /// </summary>
        public CancellationTokenSource? Cancel { get; set; }
    }

    /// <summary>One <see cref="ReadAsync"/> call: its segments, its lanes and the consumer's place.</summary>
    private sealed class Read
    {
        private readonly ConcurrentHistory _owner;
        private readonly string _account;
        private readonly long _toLedger;
        private readonly CancellationTokenSource _stop;
        private readonly Lock _gate = new();
        private readonly Task[] _lanes;

        // Segments not yet taken by the consumer, in ledger order; _head is the one it reads.
        private Segment? _head;
        private Segment? _tail;
        private long _frontier;
        private long _span = 1;
        private int _held;
        private bool _stopped;
        private ExceptionDispatchInfo? _broken;

        // Completed whenever anything a lane or the consumer may be waiting on changes.
        private TaskCompletionSource? _changed;

        public Read(ConcurrentHistory owner, string account, long fromLedger, long toLedger, int lanes, CancellationToken cancellationToken)
        {
            _owner = owner;
            _account = account;
            _toLedger = toLedger;
            _frontier = fromLedger;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _lanes = new Task[lanes];

            for (int lane = 0; lane < lanes; lane++)
            {
                int index = lane;
                _lanes[lane] = Task.Run(() => LaneAsync(index));
            }
        }

        /// <summary>The next record in canonical order, or null at the end of the range.</summary>
        public async Task<LedgerTransaction?> NextAsync()
        {
            while (true)
            {
                Task changed;

                lock (_gate)
                {
                    _broken?.Throw();

                    while (_head is { Finished: true, Waiting.Count: 0, Failure: null })
                    {
                        _head = _head.Next;
                        if (_head is null) _tail = null;

                        // The new head's lane may be waiting for room it now has (spec H7).
                        Changed();
                    }

                    if (_head is null && _frontier > _toLedger) return null;

                    if (_head is { } head)
                    {
                        if (head.Waiting.TryDequeue(out LedgerTransaction? tx))
                        {
                            _held--;
                            Changed();
                            return tx;
                        }

                        if (head.Finished) head.Failure?.Throw();
                    }

                    changed = WhenChanged();
                }

                await changed.WaitAsync(_stop.Token).ConfigureAwait(false);
            }
        }

        /// <summary>Cancels every lane and waits for them: nothing this read asked for is still in flight afterwards.</summary>
        public async Task StopAsync()
        {
            lock (_gate)
            {
                _stopped = true;
                Changed();
            }

            await _stop.CancelAsync().ConfigureAwait(false);

            // Every lane catches its own failures; this only waits.
            await Task.WhenAll(_lanes).ConfigureAwait(false);

            lock (_gate)
            {
                _head = _tail = null;
                _held = 0;
            }

            _stop.Dispose();
        }

        private async Task LaneAsync(int lane)
        {
            try
            {
                while (true)
                {
                    Segment? segment;
                    Task? changed;

                    lock (_gate)
                    {
                        if (_stopped) return;

                        segment = Take(lane, out changed);
                    }

                    if (segment is not null)
                    {
                        await WalkAsync(segment).ConfigureAwait(false);
                        continue;
                    }

                    if (changed is null) return;

                    await changed.WaitAsync(_stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // The read is over; the consumer is not waiting for this lane.
            }
            catch (Exception e)
            {
                // Nothing a walk throws gets here (WalkAsync keeps it on its segment); this is a fault
                // in the scheduling itself, and the consumer must not wait for records that never come.
                lock (_gate)
                {
                    _broken ??= ExceptionDispatchInfo.Capture(e);
                    Changed();
                }
            }
        }

        /// <summary>
        /// The work for <paramref name="lane"/> (spec H6, H9): the rest of the earliest segment that
        /// still has about <see cref="TargetRecords"/> ahead of it, else a new segment cut at the
        /// frontier. Null with a task to wait for when there is nothing yet; null with none when the
        /// lane is done.
        /// </summary>
        private Segment? Take(int lane, out Task? changed)
        {
            changed = null;

            // The first lane carries the read on alone if the others are let go (spec H9).
            _owner.ObserveLoad();
            if (lane > 0 && lane >= _owner.Lanes) return null;

            // Work closest to the consumer first: a lane that cut far ahead instead would only fill the
            // hold and wait, while the segment the consumer reads went on one page at a time.
            if (Steal() is { } piece) return Start(piece);

            // With the hold full a segment cut ahead could only wait, and its first page would keep a
            // lane from the work the consumer reaches sooner.
            if (_frontier <= _toLedger && (_held < HeldRecords || _head is null))
            {
                long last = _toLedger - _frontier < _span ? _toLedger : _frontier + _span - 1;
                Segment cut = new(_frontier, last);
                _frontier = last + 1;

                if (_tail is null)
                {
                    _head = _tail = cut;
                }
                else
                {
                    _tail.Next = cut;
                    _tail = cut;
                }

                return Start(cut);
            }

            // The frontier waits for room; a segment still being read may yet have enough ahead of it
            // to share (spec H5).
            bool more = _frontier <= _toLedger;

            for (Segment? segment = _head; segment is not null && !more; segment = segment.Next)
            {
                more = !segment.Finished;
            }

            if (more) changed = WhenChanged();

            return null;
        }

        /// <summary>
        /// Takes what the earliest segment still being read has beyond about <see cref="TargetRecords"/>
        /// ahead of its walk, judged by its density since its first record (spec H5). The ledger of its
        /// last record stays with it: the rest of that ledger may still be ahead in its walk.
        /// </summary>
        private Segment? Steal()
        {
            for (Segment? segment = _head; segment is not null; segment = segment.Next)
            {
                if (segment.Finished || segment.Abandoned || segment.Handed == 0) continue;

                // Halving what is left would hand a lane the empty far end of a segment cut across a
                // long empty stretch, while the dense ledgers next to the walk stayed with it.
                long covered = segment.Position - segment.FirstRecord + 1;
                long keep = TargetRecords * covered / segment.Handed;
                if (keep >= segment.Last - segment.Position) continue;

                long at = segment.Position + 1 + keep;
                Segment rest = new(at, segment.Last) { Next = segment.Next };

                segment.Next = rest;
                segment.Last = at - 1;

                if (ReferenceEquals(_tail, segment)) _tail = rest;

                return rest;
            }

            return null;
        }

        private Segment Start(Segment segment)
        {
            segment.Cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            return segment;
        }

        private async Task WalkAsync(Segment segment)
        {
            CancellationToken token = segment.Cancel!.Token;

            try
            {
                long last;
                lock (_gate) last = segment.Last;

                await foreach (LedgerTransaction tx in _owner._source.AccountTransactionsAsync(_account, segment.First, last, forward: true, token).ConfigureAwait(false))
                {
                    if (!await HandOverAsync(segment, tx, token).ConfigureAwait(false)) break;
                }

                lock (_gate)
                {
                    segment.Finished = true;
                    if (!segment.Abandoned) Learn(segment);
                    Changed();
                }
            }
            catch (Exception e)
            {
                List<CancellationTokenSource> abandoned = [];

                lock (_gate)
                {
                    segment.Finished = true;

                    // What a segment dropped behind a failure, or cut off by the read stopping, throws
                    // is never delivered: the consumer has a failure before it, or has gone.
                    if (!segment.Abandoned && !_stopped)
                    {
                        segment.Failure = ExceptionDispatchInfo.Capture(e);
                        AbandonAfter(segment, abandoned);
                    }

                    Changed();
                }

                // Outside the lock: a cancellation can run the cancelled lane's continuation inline,
                // and that continuation takes the lock.
                foreach (CancellationTokenSource cancel in abandoned)
                {
                    try
                    {
                        await cancel.CancelAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Its walk ended after the lock was let go; there is nothing left to cancel.
                    }
                }
            }
            finally
            {
                // Released with the walk, not the read: a long history is many segments.
                lock (_gate)
                {
                    segment.Cancel!.Dispose();
                    segment.Cancel = null;
                }
            }
        }

        /// <summary>
        /// Gives the consumer one record once there is room for it (spec H7). False when the record lies
        /// past the segment's end after another lane took the rest, which ends the segment's walk.
        /// </summary>
        private async Task<bool> HandOverAsync(Segment segment, LedgerTransaction tx, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;

                lock (_gate)
                {
                    if (segment.Abandoned) return false;
                    if (tx.LedgerIndex > segment.Last) return false;

                    // Counting only a segment's predecessors would let segments answered out of order
                    // each fill to the bound; counting everything for the one the consumer reads could
                    // stall it behind records it alone can release.
                    bool room = ReferenceEquals(segment, _head) ? segment.Waiting.Count < HeadRecords : _held < HeldRecords;

                    if (room)
                    {
                        segment.Waiting.Enqueue(tx);
                        if (segment.Handed++ == 0) segment.FirstRecord = tx.LedgerIndex;
                        segment.Position = tx.LedgerIndex;
                        _held++;
                        _owner.RecordHeld(_held);
                        Changed();
                        return true;
                    }

                    changed = WhenChanged();
                }

                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>The span the next cut segment gets, from what a finished one held (spec H4).</summary>
        private void Learn(Segment segment) => _span = NextSpan(_span, segment.Last - segment.First + 1, segment.Handed);

        /// <summary>The segments after a failed one are never delivered: they are cancelled and dropped, and nothing more is cut.</summary>
        private void AbandonAfter(Segment failed, List<CancellationTokenSource> cancels)
        {
            for (Segment? segment = failed.Next; segment is not null; segment = segment.Next)
            {
                _held -= segment.Waiting.Count;
                segment.Waiting.Clear();
                segment.Abandoned = true;

                if (segment.Cancel is { } cancel) cancels.Add(cancel);
            }

            failed.Next = null;
            _tail = failed;
            _frontier = _toLedger + 1;
        }

        private Task WhenChanged()
        {
            _changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _changed.Task;
        }

        private void Changed()
        {
            if (_changed is not { } waiting) return;

            _changed = null;
            waiting.SetResult();
        }
    }
}
