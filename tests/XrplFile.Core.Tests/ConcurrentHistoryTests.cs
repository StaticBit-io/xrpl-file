using System.Runtime.CompilerServices;
using Xunit;

namespace XrplFile.Tests;

public class ConcurrentHistoryTests
{
    private const string Writer = "rWriter";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A node's account_tx as far as paging goes: records in canonical order, a page of
    /// <see cref="PageSize"/> per answer, each answer after <see cref="PageDelay"/>, every walk counted.
    /// </summary>
    private sealed class PagedSource(IReadOnlyList<LedgerTransaction> records) : ILedgerSource
    {
        private readonly Lock _gate = new();
        private int _inFlight;
        private long _loadWarnings;

        public int PageSize { get; init; } = 200;

        public TimeSpan PageDelay { get; init; }

        /// <summary>A further delay before the first answer of a walk, by the walk's first ledger.</summary>
        public Func<long, TimeSpan>? FirstAnswerDelay { get; init; }

        /// <summary>Called before each answer with the walk's range and the page's number; an exception it returns is thrown there.</summary>
        public Func<long, long, int, Exception?>? Fails { get; init; }

        /// <summary>The walk with this number (from 1) answers with a load warning.</summary>
        public int? WarnsOnCall { get; init; }

        public List<(long From, long To)> Calls { get; } = [];

        public int InFlight
        {
            get { lock (_gate) return _inFlight; }
        }

        public int PeakInFlight { get; private set; }

        public long LoadWarnings => Interlocked.Read(ref _loadWarnings);

        public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
            string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Assert.True(forward);

            int call;

            lock (_gate)
            {
                Calls.Add((fromLedger, toLedger));
                call = Calls.Count;
            }

            if (fromLedger > toLedger) throw new XrplFileException(XrplFileError.NodeUnreachable, "invalidLgrRange");

            if (call == WarnsOnCall) Interlocked.Increment(ref _loadWarnings);

            List<LedgerTransaction> range = [.. records.Where(tx => tx.LedgerIndex >= fromLedger && tx.LedgerIndex <= toLedger)];

            for (int page = 0; ; page++)
            {
                lock (_gate)
                {
                    _inFlight++;
                    PeakInFlight = Math.Max(PeakInFlight, _inFlight);
                }

                try
                {
                    if (PageDelay > TimeSpan.Zero) await Task.Delay(PageDelay, cancellationToken);
                    else await Task.Yield();

                    if (page == 0 && FirstAnswerDelay?.Invoke(fromLedger) is { } late && late > TimeSpan.Zero) await Task.Delay(late, cancellationToken);

                    if (Fails?.Invoke(fromLedger, toLedger, page) is { } failure) throw failure;
                }
                finally
                {
                    lock (_gate) _inFlight--;
                }

                foreach (LedgerTransaction tx in range.Skip(page * PageSize).Take(PageSize))
                {
                    yield return tx;
                }

                if ((page + 1) * PageSize >= range.Count) yield break;
            }
        }

        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static LedgerTransaction Record(long ledger, int position) =>
        new($"{ledger:X16}{position:X8}", ledger, Writer, true, true, "tesSUCCESS", null, null, null) { TransactionIndex = position };

    /// <summary>Records at the given ledgers, in the order given, which must be ascending.</summary>
    private static List<LedgerTransaction> History(IEnumerable<(long Ledger, int Count)> ledgers) =>
        [.. ledgers.SelectMany(each => Enumerable.Range(0, each.Count).Select(position => Record(each.Ledger, position)))];

    /// <summary>The shapes a writer's history takes, as (first ledger, last ledger, records).</summary>
    public static TheoryData<string> Shapes => new() { "dense", "one-huge-ledger", "sessions-with-gaps", "sparse", "empty", "single" };

    private static (List<LedgerTransaction> Records, long From, long To) Shape(string name)
    {
        Random random = new(name.GetHashCode(StringComparison.Ordinal) & 0x7fff);

        return name switch
        {
            "dense" => (History(Enumerable.Range(1_000, 30).Select(ledger => ((long)ledger, 300 + random.Next(400)))), 990, 1_040),
            "one-huge-ledger" => (History([(5_000, 20_000)]), 4_000, 6_000),
            "sessions-with-gaps" => (History(
                Enumerable.Range(100, 10).Select(ledger => ((long)ledger, 250))
                    .Concat(Enumerable.Range(500_000, 6).Select(ledger => ((long)ledger, 600)))
                    .Concat([(550_000L, 3)])), 90, 600_000),
            "sparse" => (History(Enumerable.Range(0, 5_000).Select(_ => (long)random.Next(1, 100_000)).Distinct().Order().Select(ledger => (ledger, 1))), 1, 100_000),
            "empty" => ([], 1, 50_000),
            "single" => ([Record(777, 0)], 1, 1_000),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    private static async Task<List<LedgerTransaction>> ReadAllAsync(
        ConcurrentHistory history, long from, long to, CancellationToken cancellationToken, Func<LedgerTransaction, Task>? perRecord = null)
    {
        List<LedgerTransaction> seen = [];

        await foreach (LedgerTransaction tx in history.ReadAsync(Writer, from, to, cancellationToken))
        {
            seen.Add(tx);
            if (perRecord is not null) await perRecord(tx);
        }

        return seen;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Every_shape_of_history_reads_as_one_walk_would_for_any_number_of_lanes(string shape)
    {
        (List<LedgerTransaction> records, long from, long to) = Shape(shape);

        foreach (int lanes in new[] { 1, 2, 4, 8 })
        {
            PagedSource source = new(records);
            List<LedgerTransaction> read = await ReadAllAsync(new ConcurrentHistory(source, lanes), from, to, Token);

            Assert.Equal(records, read);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_consumer_slower_than_the_node_gets_the_same_records_and_the_hold_stays_bounded(string shape)
    {
        (List<LedgerTransaction> records, long from, long to) = Shape(shape);
        PagedSource source = new(records);
        ConcurrentHistory history = new(source, 4);
        int taken = 0;

        List<LedgerTransaction> read = await ReadAllAsync(history, from, to, Token, async _ =>
        {
            if (++taken % 256 == 0) await Task.Delay(1, Token);
        });

        Assert.Equal(records, read);
        Assert.InRange(history.PeakHeld, 0, ConcurrentHistory.HeldRecords + ConcurrentHistory.HeadRecords);
    }

    [Fact]
    public async Task A_node_that_sends_records_faster_than_they_are_taken_fills_the_hold_but_not_past_its_bound()
    {
        List<LedgerTransaction> records = History(Enumerable.Range(1, 40).Select(ledger => ((long)ledger, 1_000)));
        PagedSource source = new(records) { PageSize = 400 };
        ConcurrentHistory history = new(source, 8);
        int taken = 0;

        List<LedgerTransaction> read = await ReadAllAsync(history, 1, 40, Token, async _ =>
        {
            if (++taken % 256 == 0) await Task.Delay(2, Token);
        });

        Assert.Equal(records, read);
        Assert.InRange(history.PeakHeld, ConcurrentHistory.TargetRecords, ConcurrentHistory.HeldRecords + ConcurrentHistory.HeadRecords);
    }

    [Fact]
    public async Task Ranges_answered_last_first_do_not_each_fill_the_hold()
    {
        // Four dense ledgers, one segment each; the node answers the last first and the first last,
        // so each later segment has its records in hand while every segment before it has none.
        List<LedgerTransaction> records = History(Enumerable.Range(1, 4).Select(ledger => ((long)ledger, 6_000)));
        PagedSource source = new(records)
        {
            PageSize = 400,
            FirstAnswerDelay = first => TimeSpan.FromMilliseconds(300 * (4 - Math.Min(first, 4))),
        };
        ConcurrentHistory history = new(source, 4);

        Assert.Equal(records, await ReadAllAsync(history, 1, 4, Token));
        Assert.InRange(history.PeakHeld, ConcurrentHistory.HeldRecords - 1, ConcurrentHistory.HeldRecords + ConcurrentHistory.HeadRecords);
    }

    [Fact]
    public async Task An_empty_stretch_after_the_records_is_crossed_in_a_few_answers()
    {
        List<LedgerTransaction> records = History(Enumerable.Range(1_000, 10).Select(ledger => ((long)ledger, 300)));
        PagedSource source = new(records);

        Assert.Equal(records, await ReadAllAsync(new ConcurrentHistory(source, 4), 1_000, 101_000, Token));

        // How many walks depends on which lane learns first; 14 to 18 were measured, 22 to 45 when an
        // empty answer only doubled the span. The rule itself is pinned below.
        Assert.InRange(source.Calls.Count, 1, 30);
    }

    [Theory]
    [InlineData(1, 1, 0, 8)]
    [InlineData(64, 4, 0, 64)]
    [InlineData(1, 10, 3_000, 3)]
    [InlineData(1, 1, 20_000, 1)]
    [InlineData(1, 4, 10, 8)]
    [InlineData(1, long.MaxValue / 4, 0, long.MaxValue)]
    public void An_empty_segment_widens_the_next_eightfold_and_a_full_one_sizes_it_to_about_a_thousand_records(long current, long span, long handed, long next)
    {
        Assert.Equal(next, ConcurrentHistory.NextSpan(current, span, handed));
    }

    [Fact]
    public async Task Ranges_are_asked_for_together_and_the_read_takes_less_than_half_as_long_as_one_walk()
    {
        List<LedgerTransaction> records = History(Enumerable.Range(1_000, 40).Select(ledger => ((long)ledger, 200)));
        TimeSpan delay = TimeSpan.FromMilliseconds(40);

        PagedSource single = new(records) { PageDelay = delay };
        System.Diagnostics.Stopwatch walk = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(records, await ReadAllAsync(new ConcurrentHistory(single, 1), 1_000, 1_039, Token));
        walk.Stop();

        PagedSource lanes = new(records) { PageDelay = delay };
        System.Diagnostics.Stopwatch together = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(records, await ReadAllAsync(new ConcurrentHistory(lanes, 4), 1_000, 1_039, Token));
        together.Stop();

        Assert.InRange(lanes.PeakInFlight, 2, 4);
        Assert.True(together.Elapsed < walk.Elapsed / 2, $"four lanes took {together.Elapsed}, one walk {walk.Elapsed}");
    }

    [Fact]
    public async Task Dense_ledgers_after_a_long_empty_stretch_are_shared_between_the_lanes()
    {
        // The empty stretch grows the span until one segment lands on every dense ledger at once; the
        // free lanes must take shares of it rather than leave it to one walk.
        List<LedgerTransaction> records = History(Enumerable.Range(200_001, 60).Select(ledger => ((long)ledger, 600)));
        TimeSpan delay = TimeSpan.FromMilliseconds(40);

        PagedSource single = new(records) { PageDelay = delay };
        System.Diagnostics.Stopwatch walk = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(records, await ReadAllAsync(new ConcurrentHistory(single, 1), 1, 300_000, Token));
        walk.Stop();

        PagedSource lanes = new(records) { PageDelay = delay };
        System.Diagnostics.Stopwatch together = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(records, await ReadAllAsync(new ConcurrentHistory(lanes, 4), 1, 300_000, Token));
        together.Stop();

        Assert.True(together.Elapsed < walk.Elapsed / 2, $"four lanes took {together.Elapsed}, one walk {walk.Elapsed}");
    }

    [Fact]
    public async Task One_lane_asks_the_node_once_for_the_whole_range()
    {
        (List<LedgerTransaction> records, long from, long to) = Shape("dense");
        PagedSource source = new(records);

        Assert.Equal(records, await ReadAllAsync(new ConcurrentHistory(source, 1), from, to, Token));
        Assert.Equal([(from, to)], source.Calls);
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_the_nodes_to_refuse()
    {
        PagedSource source = new([]);

        XrplFileException refused = await Assert.ThrowsAsync<XrplFileException>(() => ReadAllAsync(new ConcurrentHistory(source, 4), 10, 9, Token));

        Assert.Equal(XrplFileError.NodeUnreachable, refused.Error);
        Assert.Equal([(10L, 9L)], source.Calls);
    }

    [Fact]
    public async Task A_failure_reaches_the_consumer_after_every_record_before_it_and_only_once()
    {
        (List<LedgerTransaction> records, long from, long to) = Shape("dense");
        XrplFileException dropped = new(XrplFileError.NodeUnreachable, "the connection dropped");

        // Any walk that covers ledger 1,020 fails on its second page; walks after it may already
        // have records in hand.
        PagedSource source = new(records) { Fails = (first, last, page) => first <= 1_020 && last >= 1_020 && page == 1 ? dropped : null };
        ConcurrentHistory history = new(source, 4);
        List<LedgerTransaction> seen = [];

        XrplFileException thrown = await Assert.ThrowsAsync<XrplFileException>(async () =>
        {
            await foreach (LedgerTransaction tx in history.ReadAsync(Writer, from, to, Token)) seen.Add(tx);
        });

        Assert.Same(dropped, thrown);
        Assert.Equal(records.Take(seen.Count), seen);
        Assert.Contains(seen, tx => tx.LedgerIndex < 1_020);
        Assert.DoesNotContain(seen, tx => tx.LedgerIndex > 1_020);
        Assert.Equal(0, source.InFlight);
    }

    [Fact]
    public async Task A_later_range_that_fails_at_once_does_not_overtake_what_the_consumer_finds_earlier()
    {
        (List<LedgerTransaction> records, long from, long to) = Shape("dense");
        XrplFileException dropped = new(XrplFileError.NodeUnreachable, "the connection dropped");
        PagedSource source = new(records) { Fails = (first, _, _) => first >= 1_010 ? dropped : null };
        LedgerTransaction conflict = records.First(tx => tx.LedgerIndex == 1_005);

        InvalidOperationException found = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReadAllAsync(new ConcurrentHistory(source, 4), from, to, Token, tx => tx == conflict ? throw new InvalidOperationException("conflict") : Task.CompletedTask));

        Assert.Equal("conflict", found.Message);
        Assert.Equal(0, source.InFlight);
    }

    [Fact]
    public async Task A_consumer_that_stops_or_is_cancelled_leaves_nothing_in_flight()
    {
        (List<LedgerTransaction> records, long from, long to) = Shape("dense");
        PagedSource stopped = new(records) { PageDelay = TimeSpan.FromMilliseconds(5) };
        int taken = 0;

        await foreach (LedgerTransaction _ in new ConcurrentHistory(stopped, 4).ReadAsync(Writer, from, to, Token))
        {
            if (++taken == 300) break;
        }

        Assert.Equal(0, stopped.InFlight);
        Assert.True(stopped.PeakInFlight > 1);

        PagedSource cancelled = new(records) { PageDelay = TimeSpan.FromMilliseconds(5) };
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        taken = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAllAsync(new ConcurrentHistory(cancelled, 4), from, to, cancel.Token, _ =>
        {
            if (++taken == 300) cancel.Cancel();
            return Task.CompletedTask;
        }));

        Assert.Equal(0, cancelled.InFlight);
    }

    [Fact]
    public async Task A_load_warning_halves_the_lanes_and_a_read_that_starts_at_one_lane_is_one_walk()
    {
        (List<LedgerTransaction> records, long from, long to) = Shape("dense");
        PagedSource warned = new(records) { WarnsOnCall = 2 };
        ConcurrentHistory history = new(warned, 4);

        Assert.Equal(records, await ReadAllAsync(history, from, to, Token));
        Assert.Equal(2, history.Lanes);

        PagedSource again = new(records) { WarnsOnCall = 1 };
        ConcurrentHistory halving = new(again, 2);
        Assert.Equal(records, await ReadAllAsync(halving, from, to, Token));
        Assert.Equal(1, halving.Lanes);

        int callsBefore = again.Calls.Count;
        Assert.Equal(records, await ReadAllAsync(halving, from, to, Token));
        Assert.Equal([(from, to)], again.Calls.Skip(callsBefore));
    }

    [Fact]
    public async Task A_read_the_node_failed_halves_the_lanes_for_the_next()
    {
        (List<LedgerTransaction> records, long from, long to) = Shape("dense");
        bool failing = true;
        PagedSource source = new(records)
        {
            Fails = (first, _, _) => failing && first >= 1_015 ? new XrplFileException(XrplFileError.NodeUnreachable, "dropped") : null,
        };
        ConcurrentHistory history = new(source, 4);

        await Assert.ThrowsAsync<XrplFileException>(() => ReadAllAsync(history, from, to, Token));
        Assert.Equal(2, history.Lanes);

        failing = false;
        Assert.Equal(records, await ReadAllAsync(history, from, to, Token));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Lanes_let_go_mid_read_while_the_hold_is_full_still_finish_the_read(string shape)
    {
        (List<LedgerTransaction> records, long from, long to) = Shape(shape);

        foreach (int warnsOn in new[] { 1, 3, 6 })
        {
            PagedSource source = new(records) { WarnsOnCall = warnsOn, PageSize = 400 };
            ConcurrentHistory history = new(source, 8);
            int taken = 0;
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));

            List<LedgerTransaction> read = await ReadAllAsync(history, from, to, deadline.Token, async _ =>
            {
                if (++taken % 512 == 0) await Task.Delay(1, deadline.Token);
            });

            Assert.Equal(records, read);
        }
    }
}
