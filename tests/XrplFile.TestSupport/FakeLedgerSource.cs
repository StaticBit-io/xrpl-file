using System.Runtime.CompilerServices;

namespace XrplFile.Tests;

/// <summary>One recorded call to <see cref="ILedgerSource.AccountTransactionsAsync"/>, in call order.</summary>
public sealed record AccountTransactionsCall(string Account, long FromLedger, long ToLedger, bool Forward);

/// <summary>
/// An in-memory ledger: insertion order is canonical order within a ledger.
///
/// Everything it holds is guarded, because the sink writes into it from whichever task is
/// submitting and a sweep reads it at the same time. A sweep enumerates a snapshot taken when it
/// starts, which is also what a node's account_tx answer is.
/// </summary>
public sealed class FakeLedgerSource : ILedgerSource
{
    private readonly Lock _gate = new();
    private readonly List<LedgerTransaction> _transactions = [];

    /// <summary>
    /// Transactions held back from a sweep's view: revealed once this many more
    /// AccountTransactionsAsync calls ("sweeps") have happened. Backs FakeLedgerSink's
    /// QueuedUntilSweep, which needs a landing that a sweep in progress cannot see yet.
    /// </summary>
    private readonly List<(int RemainingSweeps, LedgerTransaction Tx)> _delayed = [];

    /// <summary>Transactions applied but not yet validated, revealed by <see cref="AddUnvalidated"/>'s count of ledger reads.</summary>
    private readonly List<(int RemainingReads, LedgerTransaction Tx)> _unvalidated = [];

    /// <summary>
    /// Accounts a transaction touches besides the one that signed it, by transaction hash.
    ///
    /// A node's account_tx answers with every transaction that affects the account, not only the
    /// ones it sent: an incoming Payment is in that answer with the sender in its Account field.
    /// Without this the fake could hold no payment made *to* an account, which is the only kind a
    /// throwaway account ever receives before it has published anything.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _alsoAffects = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IReadOnlyList<LiveTicket>> _liveTickets = new(StringComparer.Ordinal);

    private int _transactionLookups;

    private long _loadWarnings;

    /// <summary>How many <c>"warning": "load"</c> answers this fake has given, as a node nearing a client's rate limit gives them.</summary>
    public long LoadWarnings => Interlocked.Read(ref _loadWarnings);

    /// <summary>How many of the next <see cref="LiveTicketsAsync"/> answers carry <c>"warning": "load"</c>.</summary>
    public int ListingWarnings
    {
        get { lock (_gate) return _listingWarnings; }
        set { lock (_gate) _listingWarnings = value; }
    }

    private int _listingWarnings;

    /// <summary>
    /// How far the validated ledger moves before each <see cref="CurrentLedgerAsync"/> answer — a
    /// network closing ledgers while the caller works. Zero, the default, keeps the index where the
    /// transactions put it.
    /// </summary>
    public long LedgerAdvancePerRead { get; set; }

    /// <summary>
    /// How far <see cref="LedgerAdvancePerRead"/> has moved the validated ledger so far;
    /// <see cref="FakeLedgerSink"/> adds it to the ledger a submission would land in, so a deadline
    /// the clock has passed is refused as a node refuses it.
    /// </summary>
    public long LedgerAdvanced
    {
        get { lock (_gate) return _ledgerAdvanced; }
    }

    private long _ledgerAdvanced;

    /// <summary>
    /// Moves the validated ledger on by <paramref name="ledgers"/> without anyone reading it — a
    /// network closing ledgers while a caller waits for an answer. Counts toward
    /// <see cref="LedgerAdvanced"/>, so <see cref="FakeLedgerSink"/> refuses a deadline it has passed.
    /// </summary>
    public void AdvanceLedger(long ledgers)
    {
        lock (_gate)
        {
            _currentLedger += ledgers;
            _ledgerAdvanced += ledgers;
        }
    }

    private long _currentLedger = 1;

    public long? EarliestLedger { get; set; } = 1;

    public List<AccountTransactionsCall> Calls { get; } = [];

    public int AccountTransactionsCalls
    {
        get { lock (_gate) return Calls.Count; }
    }

    public FakeLedgerSource Add(LedgerTransaction tx)
    {
        lock (_gate)
        {
            _transactions.Add(tx);
            if (tx.LedgerIndex > _currentLedger) _currentLedger = tx.LedgerIndex;
        }

        return this;
    }

    /// <summary>
    /// Adds a transaction that a second account is also a party to — a Payment's destination,
    /// say — so that account_tx for that account answers with it the way a node's would.
    /// </summary>
    public FakeLedgerSource AddAffecting(LedgerTransaction tx, params string[] accounts)
    {
        lock (_gate)
        {
            if (!_alsoAffects.TryGetValue(tx.Hash, out HashSet<string>? touched))
            {
                touched = new HashSet<string>(StringComparer.Ordinal);
                _alsoAffects[tx.Hash] = touched;
            }

            foreach (string account in accounts) touched.Add(account);
        }

        return Add(tx);
    }

    /// <summary>
    /// Holds a transaction back from AccountTransactionsAsync until the given number of further
    /// sweeps have passed, then reveals it — a queued submission that lands only later.
    /// </summary>
    public FakeLedgerSource AddDelayed(LedgerTransaction tx, int sweepsUntilVisible)
    {
        lock (_gate) _delayed.Add((Math.Max(sweepsUntilVisible, 0), tx));

        return this;
    }

    /// <summary>
    /// Holds a transaction out of the validated ledger until the given number of further
    /// <see cref="CurrentLedgerAsync"/> answers have gone by, then validates it: neither account_tx
    /// nor the validated index shows it before then. A submission the node applied to its open
    /// ledger, read by a caller before that ledger validates.
    /// </summary>
    public FakeLedgerSource AddUnvalidated(LedgerTransaction tx, int readsUntilValidated)
    {
        lock (_gate) _unvalidated.Add((Math.Max(readsUntilValidated, 0), tx));

        return this;
    }

    public FakeLedgerSource AddRange(IEnumerable<LedgerTransaction> transactions)
    {
        lock (_gate) _transactions.AddRange(transactions);

        return this;
    }

    public FakeLedgerSource Remove(Func<LedgerTransaction, bool> which)
    {
        lock (_gate) _transactions.RemoveAll(tx => which(tx));

        return this;
    }

    public FakeLedgerSource Replace(Func<LedgerTransaction, bool> which, Func<LedgerTransaction, LedgerTransaction> with)
    {
        lock (_gate)
        {
            for (int i = 0; i < _transactions.Count; i++)
            {
                if (which(_transactions[i])) _transactions[i] = with(_transactions[i]);
            }
        }

        return this;
    }

    public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _transactionLookups);

        lock (_gate)
        {
            return Task.FromResult(_transactions.FirstOrDefault(tx => string.Equals(tx.Hash, hash, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>
    /// Raised by <see cref="AccountTransactionsAsync"/> instead of answering: a node that could
    /// not be read at all.
    ///
    /// A double that can only succeed leaves every caller's failure path untested, and that is not
    /// a gap but a defect — a funder lookup that threw where it was asked to propose nobody sat
    /// behind a green suite precisely because this fake could not produce what a node produces.
    /// Absence itself is not a failure and is not this: an account the node has never heard of
    /// answers with no transactions, which is what the real source does with <c>actNotFound</c>
    /// and what this fake does for an account it holds nothing for.
    /// </summary>
    public Exception? AccountTransactionsFailure { get; set; }

    public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
        string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (AccountTransactionsFailure is { } failure) throw failure;

        // rippled answers invalidLgrRange to a window that ends before it starts — min one past
        // the validated ledger and max at it, measured on the stand — and the real source turns
        // that into NodeUnreachable. A fake that answered such a window with nothing hid a sweep
        // that asked for it before the next ledger validated.
        if (fromLedger > toLedger)
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable,
                $"fake: the node answered invalidLgrRange to account_tx from {fromLedger} to {toLedger}");
        }

        List<LedgerTransaction> ordered;

        lock (_gate)
        {
            Calls.Add(new AccountTransactionsCall(account, fromLedger, toLedger, forward));

            for (int i = _delayed.Count - 1; i >= 0; i--)
            {
                (int remaining, LedgerTransaction tx) = _delayed[i];

                if (remaining <= 0)
                {
                    _transactions.Add(tx);
                    if (tx.LedgerIndex > _currentLedger) _currentLedger = tx.LedgerIndex;
                    _delayed.RemoveAt(i);
                }
                else
                {
                    _delayed[i] = (remaining - 1, tx);
                }
            }

            IEnumerable<(LedgerTransaction Tx, int Position)> matching = _transactions
                .Select((tx, position) => (Tx: tx, Position: position))
                .Where(pair => Touches(pair.Tx, account)
                    && pair.Tx.LedgerIndex >= fromLedger && pair.Tx.LedgerIndex <= toLedger);

            ordered = forward
                ? [.. matching.OrderBy(pair => pair.Tx.LedgerIndex).ThenBy(pair => pair.Position).Select(pair => pair.Tx)]
                : [.. matching.OrderByDescending(pair => pair.Tx.LedgerIndex).ThenByDescending(pair => pair.Position).Select(pair => pair.Tx)];
        }

        foreach (LedgerTransaction tx in ordered)
        {
            await Task.Yield();
            yield return tx;
        }
    }

    /// <summary>Whether account_tx for this account would answer with this transaction. Call under the gate.</summary>
    private bool Touches(LedgerTransaction tx, string account) =>
        string.Equals(tx.Account, account, StringComparison.Ordinal)
        || (_alsoAffects.TryGetValue(tx.Hash, out HashSet<string>? touched) && touched.Contains(account));

    public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => Task.FromResult(EarliestLedger);

    /// <summary>What this fake node charges and reserves. Ten drops, one XRP, a fifth of an XRP.</summary>
    public NetworkFees Fees { get; set; } = new(10, 1_000_000, 200_000);

    public Dictionary<string, long> Balances { get; } = [];

    /// <summary>
    /// Ledger objects each account owns, and so how many owner reserves are locked out of its
    /// balance. Tickets an aborted run left live are the case this exists for.
    /// </summary>
    public Dictionary<string, int> OwnerCounts { get; } = [];

    /// <summary>
    /// Live tickets each account holds, as AccountRoot's own TicketCount field reports them.
    /// <see cref="FakeLedgerSink"/> keeps this in step with the tickets it mints and spends, so a
    /// strategy asking the ledger how much room is left gets the answer a node would give.
    /// </summary>
    public Dictionary<string, int> TicketCounts { get; } = [];

    /// <summary>
    /// How many times <see cref="TransactionAsync"/> was asked. Lets a test hold the library to one
    /// creator lookup per mint rather than one per ticket.
    /// </summary>
    public int TransactionLookups => Volatile.Read(ref _transactionLookups);

    /// <summary>
    /// Replaces what <see cref="LiveTicketsAsync"/> answers for an account. <see cref="FakeLedgerSink"/>
    /// calls it whenever it mints or spends a ticket; a test that has no sink sets it directly.
    /// </summary>
    public void SetLiveTickets(string account, IReadOnlyList<LiveTicket> tickets)
    {
        lock (_gate) _liveTickets[account] = tickets;
    }

    private int _liveTicketsCalls;

    /// <summary>
    /// How many <see cref="LiveTicketsAsync"/> calls, from the <see cref="LiveTicketsFailFromCall"/>'th
    /// onward, fail with <see cref="XrplFileError.NodeUnreachable"/> — a node that drops one
    /// account_objects answer and then recovers. Each failing call takes one off this count.
    /// </summary>
    public int LiveTicketsFailures
    {
        get { lock (_gate) return _liveTicketsFailures; }
        set { lock (_gate) _liveTicketsFailures = value; }
    }

    private int _liveTicketsFailures;

    /// <summary>
    /// The first <see cref="LiveTicketsAsync"/> call (1-based, counting every call this fake ever
    /// receives) that may fail. Lets a test keep a census that lists first, then fail a later read.
    /// </summary>
    public int LiveTicketsFailFromCall { get; set; } = 1;

    public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _liveTicketsCalls++;

            if (_listingWarnings > 0)
            {
                _listingWarnings--;
                Interlocked.Increment(ref _loadWarnings);
            }

            if (_liveTicketsFailures > 0 && _liveTicketsCalls >= LiveTicketsFailFromCall)
            {
                _liveTicketsFailures--;

                return Task.FromException<IReadOnlyList<LiveTicket>>(
                    new XrplFileException(XrplFileError.NodeUnreachable, "fake: account_objects did not answer"));
            }

            return Task.FromResult(_liveTickets.TryGetValue(account, out IReadOnlyList<LiveTicket>? tickets) ? tickets : []);
        }
    }

    public long CurrentLedger
    {
        get { lock (_gate) return _currentLedger; }
        set { lock (_gate) _currentLedger = value; }
    }

    public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => Task.FromResult(Fees);

    public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) =>
        Task.FromResult(Balances.TryGetValue(account, out long drops) ? drops : 0);

    public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(new AccountState(
                Balances.TryGetValue(account, out long drops) ? drops : 0,
                OwnerCounts.TryGetValue(account, out int owned) ? owned : 0,
                TicketCounts.TryGetValue(account, out int tickets) ? tickets : 0));
        }
    }

    private int _currentLedgerCalls;

    /// <summary>
    /// How many of the next <see cref="CurrentLedgerAsync"/> calls fail with
    /// <see cref="XrplFileError.NodeUnreachable"/> before it recovers — a node that drops one ledger
    /// answer. Each failing call takes one off this count.
    /// </summary>
    public int CurrentLedgerFailures
    {
        get { lock (_gate) return _currentLedgerFailures; }
        set { lock (_gate) _currentLedgerFailures = value; }
    }

    private int _currentLedgerFailures;

    /// <summary>Thrown by <see cref="CurrentLedgerAsync"/> from its <see cref="CurrentLedgerFailsFromCall"/>'th
    /// call onward (1-based, counting every call this fake ever receives). Lets a test make an
    /// earlier caller succeed -- <c>ThrowawayAccount.PreviewDeletionAsync</c>'s own threshold read,
    /// say -- while a later, independent caller's own read fails, rather than failing every call the
    /// way a single always-throw switch would.</summary>
    public Exception? CurrentLedgerFailure { get; set; }

    public int CurrentLedgerFailsFromCall { get; set; } = int.MaxValue;

    public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _currentLedgerCalls++;

            if (_currentLedgerFailures > 0)
            {
                _currentLedgerFailures--;

                return Task.FromException<long>(new XrplFileException(XrplFileError.NodeUnreachable, "fake: ledger did not answer"));
            }

            for (int i = _unvalidated.Count - 1; i >= 0; i--)
            {
                (int remaining, LedgerTransaction tx) = _unvalidated[i];

                if (remaining <= 0)
                {
                    _transactions.Add(tx);
                    if (tx.LedgerIndex > _currentLedger) _currentLedger = tx.LedgerIndex;
                    _unvalidated.RemoveAt(i);
                }
                else
                {
                    _unvalidated[i] = (remaining - 1, tx);
                }
            }

            _currentLedger += LedgerAdvancePerRead;
            _ledgerAdvanced += LedgerAdvancePerRead;

            if (CurrentLedgerFailure is { } failure && _currentLedgerCalls >= CurrentLedgerFailsFromCall)
            {
                return Task.FromException<long>(failure);
            }

            return Task.FromResult(_currentLedger);
        }
    }

    /// <summary>
    /// Explicit overrides for what <see cref="DestinationAsync"/> answers -- the only way to give a
    /// destination its flags (<see cref="DestinationState.RequiresDestinationTag"/>,
    /// <see cref="DestinationState.AuthorisesDepositors"/>), and still honoured ahead of
    /// <see cref="Balances"/> below when a test needs an account explicitly absent regardless of
    /// what it holds. Most tests need neither and can rely on <see cref="Balances"/> alone.
    /// </summary>
    public Dictionary<string, DestinationState> Destinations { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// What this fake answers a destination check with: an explicit entry in
    /// <see cref="Destinations"/> when a test put one there, otherwise derived from
    /// <see cref="Balances"/> the same way <see cref="AccountStateAsync"/> already treats a zero
    /// balance as absent -- an account cannot hold XRP without existing, and a fake that let those
    /// two dictionaries disagree let a test describe a ledger no real node could ever answer with:
    /// a funded account this method calls nonexistent, or the reverse. Every close test used to keep
    /// them in step by hand; this makes that bookkeeping the fake's job instead of the test's.
    /// </summary>
    public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Destinations.TryGetValue(account, out DestinationState? state)) return Task.FromResult(state);

            bool funded = Balances.TryGetValue(account, out long drops) && drops > 0;

            return Task.FromResult(funded
                ? new DestinationState(Exists: true, RequiresDestinationTag: false, AuthorisesDepositors: false)
                : DestinationState.Absent);
        }
    }
}
