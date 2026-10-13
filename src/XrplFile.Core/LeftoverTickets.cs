using System.Globalization;

namespace XrplFile;

/// <summary>An account's live tickets, split into the ones this library minted and everybody else's.</summary>
/// <param name="Ours">This library's tickets, ascending.</param>
/// <param name="Others">Every other live ticket: unmarked, minted by another account, or minted by a transaction the node cannot show.</param>
internal sealed record TicketCensus(IReadOnlyList<uint> Ours, int Others)
{
    public static TicketCensus Empty { get; } = new([], 0);
}

/// <summary>
/// Finding the tickets this library left on an account (spec 4.2), shared by the ticket
/// strategy's pick-up, the estimate and the tidy-up so the three cannot disagree about which
/// tickets are whose.
/// </summary>
internal static class LeftoverTickets
{
    /// <summary>
    /// Lists the account's live tickets and keeps the ones whose creating transaction carries this
    /// library's mark. One lookup per creator, so one per mint of up to 250 tickets.
    /// </summary>
    /// <param name="verdicts">
    /// Creator hash → whether it is this library's mint, kept by a caller that looks more than once.
    /// Null for a caller that wants every creator checked afresh.
    /// </param>
    public static async Task<TicketCensus> FindAsync(
        ILedgerSource source, string account, IDictionary<string, bool>? verdicts, CancellationToken cancellationToken)
    {
        IReadOnlyList<LiveTicket> live = await source.LiveTicketsAsync(account, cancellationToken).ConfigureAwait(false);

        if (live.Count == 0) return TicketCensus.Empty;

        List<uint> ours = [];
        int others = 0;

        foreach (IGrouping<string, LiveTicket> mint in live.GroupBy(ticket => ticket.CreatedBy, StringComparer.OrdinalIgnoreCase))
        {
            if (verdicts is null || !verdicts.TryGetValue(mint.Key, out bool isOurs))
            {
                LedgerTransaction? creator = await source.TransactionAsync(mint.Key, cancellationToken).ConfigureAwait(false);
                isOurs = IsOurMint(creator, account);

                if (verdicts is not null) verdicts[mint.Key] = isOurs;
            }

            if (isOurs) ours.AddRange(mint.Select(ticket => ticket.Sequence));
            else others += mint.Count();
        }

        ours.Sort();

        return new TicketCensus(ours, others);
    }

    /// <summary>
    /// A creator the node cannot return, or cannot show as a validated success signed by this
    /// account and carrying the mark, makes its tickets somebody else's (spec I3).
    /// </summary>
    private static bool IsOurMint(LedgerTransaction? creator, string account) =>
        creator is { Validated: true, Succeeded: true }
        && string.Equals(creator.Account, account, StringComparison.Ordinal)
        && string.Equals(creator.MemoTypeHex, MemoLayout.TicketMarkerHex, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How many times one ticket's spend is submitted before a passing refusal is taken as final. A
    /// burst of spends is what a loaded node answers with <c>telCAN_NOT_QUEUE</c>; five rounds give
    /// its queue room to drain without resending a hopeless spend until the deadline. A submission is
    /// one sink call; the sink's own retransmissions of the same blob are not further submissions.
    /// </summary>
    private const int MaxSpendAttempts = 5;

    /// <summary>
    /// How far past the validated ledger a spend may still apply (spec I16): the node's queue refuses
    /// anything closer than two past the open ledger, and ten — some thirty-five seconds — gives a
    /// spend three chances inside the two-minute deadline.
    /// </summary>
    internal const uint LastLedgerOffset = 10;

    /// <summary>
    /// How old the validated index may be when a spend takes its <c>LastLedgerSequence</c> from it
    /// (spec 4.6, I16). A throttled round outlasts ten ledgers; ten seconds is about three of them,
    /// and leaves at least half the window even at two seconds a ledger.
    /// </summary>
    internal static readonly TimeSpan IndexFreshFor = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Finds this library's tickets afresh from the validated ledger, spends each on an AccountSet
    /// that sets nothing, and waits for the outcomes (spec 4.6). Works in rounds — read the validated
    /// index, send, list once, settle, pause — and sends again a spend the node refused for a passing
    /// reason or one whose <c>LastLedgerSequence</c> expired. Cancellation is the deadline: it ends
    /// the wait and leaves every unsettled ticket counted as not released.
    /// </summary>
    /// <param name="time">The clock the validated index's age is measured against.</param>
    public static async Task<TicketRelease> ReleaseAsync(
        ILedgerSink sink, ILedgerSource source, ITransactionSigner signer, long feeDrops, TimeSpan poll, TimeProvider time,
        CancellationToken cancellationToken)
    {
        string account = signer.Account;
        Spends? spends = null;
        TidyUpPace pace = new(poll);

        try
        {
            while (true)
            {
                long warningsBefore = Warnings(sink, source);
                bool strained = false;

                try
                {
                    if (spends is null)
                    {
                        // Inside the round, so one dropped answer before anything was sent is retried
                        // rather than leaving every ticket locked (spec 4.6).
                        TicketCensus census = await FindAsync(source, account, null, cancellationToken).ConfigureAwait(false);

                        if (census.Ours.Count == 0) return TicketRelease.None;

                        spends = new Spends(census.Ours);
                    }

                    // Before the listing, so the listing comes from this ledger or a later one, which
                    // is what makes a spend's expiry provable (spec 4.6).
                    DateTimeOffset asked = time.GetUtcNow();
                    long validated = await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
                    using LedgerClock clock = new(source, time, validated, asked);

                    strained = await SendRoundAsync(sink, signer, account, feeDrops, clock, pace.InFlight, spends, cancellationToken)
                        .ConfigureAwait(false);
                    await SettleRoundAsync(source, account, clock.Validated, spends, cancellationToken).ConfigureAwait(false);

                    if (spends.Finished) break;
                }
                catch (XrplFileException failure) when (failure.Error == XrplFileError.NodeUnreachable)
                {
                    // A node that could not answer this round has decided nothing: what is settled
                    // stays settled, and the next round reads it again instead of losing every count.
                    // Anything else — a signer refusing, a locked vault — still ends the tidy-up.
                    strained = true;
                }

                // A warning anywhere in the round — a submit's answer, the listing, a lookup — counts:
                // rippled attaches it to whichever answer comes next, at most once a second (spec I15).
                if (strained || Warnings(sink, source) > warningsBefore) pace.Strained();

                await Task.Delay(pace.Pause, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The deadline, or the caller's own cancellation: the counts stand as they are, and
            // everything not yet settled is not released (spec 4.6).
        }

        return spends?.Result() ?? TicketRelease.None;
    }

    /// <summary>
    /// Sends one spend for every ticket still to be sent, at most <paramref name="inFlight"/> at once,
    /// each carrying the deadline <paramref name="clock"/> gives it, and records every answer against
    /// its ticket as it arrives, so a round cut short keeps what it already heard (spec 4.6). Once a
    /// re-read of the index goes unanswered, the spends not yet sent wait for the next round.
    /// </summary>
    /// <returns>
    /// True when any answer was a busy error answer, or the node could not be reached for a submission
    /// or a re-read (spec 4.6).
    /// </returns>
    private static async Task<bool> SendRoundAsync(
        ILedgerSink sink, ITransactionSigner signer, string account, long feeDrops, LedgerClock clock, int inFlight,
        Spends spends, CancellationToken cancellationToken)
    {
        uint[] round = spends.ToSend();

        if (round.Length == 0) return false;

        int strained = 0;

        await Parallel.ForEachAsync(
            round,
            new ParallelOptions { MaxDegreeOfParallelism = inFlight, CancellationToken = cancellationToken },
            async (ticket, token) =>
            {
                if (await clock.DeadlineAsync(token).ConfigureAwait(false) is not { } lastLedger) return;

                spends.Opened(ticket, lastLedger);
                SubmitOutcome? outcome;

                try
                {
                    outcome = await sink
                        .SubmitAsync(Spend(account, signer.PublicKey, ticket, feeDrops, lastLedger), signer, token).ConfigureAwait(false);
                }
                catch (XrplFileException failure) when (failure.Error == XrplFileError.NodeUnreachable)
                {
                    // Null: the node could not be reached, which says nothing about the ticket.
                    outcome = null;
                }

                if (outcome is null || Busy(outcome.EngineResult)) Interlocked.Exchange(ref strained, 1);

                spends.Answered(ticket, lastLedger, outcome);
            }).ConfigureAwait(false);

        return strained != 0 || clock.Unanswered;
    }

    /// <summary>
    /// The validated index a round's spends take their <c>LastLedgerSequence</c> from, read again
    /// before a spend once it is <see cref="IndexFreshFor"/> old (spec 4.6). Spends in flight at once
    /// share one re-read.
    /// </summary>
    private sealed class LedgerClock(ILedgerSource source, TimeProvider time, long validated, DateTimeOffset asked) : IDisposable
    {
        private readonly SemaphoreSlim _reading = new(1, 1);
        private long _validated = validated;

        /// <summary>When the index in use was asked for: its age runs from the question, not the answer.</summary>
        private DateTimeOffset _asked = asked;

        private volatile bool _unanswered;

        /// <summary>The last index this round read: the one its listing is judged against (spec 4.6).</summary>
        public long Validated => Interlocked.Read(ref _validated);

        /// <summary>A re-read went unanswered: nothing more is sent this round.</summary>
        public bool Unanswered => _unanswered;

        /// <summary>The <c>LastLedgerSequence</c> for the next spend, or null once a re-read has gone unanswered.</summary>
        public async Task<uint?> DeadlineAsync(CancellationToken cancellationToken)
        {
            await _reading.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (_unanswered) return null;

                DateTimeOffset now = time.GetUtcNow();

                if (now - _asked >= IndexFreshFor)
                {
                    try
                    {
                        Interlocked.Exchange(ref _validated, await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false));
                        _asked = now;
                    }
                    catch (XrplFileException failure) when (failure.Error == XrplFileError.NodeUnreachable)
                    {
                        // No deadline can be set from an index the node did not give; the rest of
                        // the round waits for the next round's own read.
                        _unanswered = true;

                        return null;
                    }
                }

                return (uint)(Validated + LastLedgerOffset);
            }
            finally
            {
                _reading.Release();
            }
        }

        public void Dispose() => _reading.Dispose();
    }

    /// <summary>
    /// Reads the listing once and settles what it can: a waiting spend whose ticket left the ledger
    /// is asked what it did, a spend whose ticket is still listed past its deadline has expired, and
    /// any other ticket the listing no longer shows is gone.
    /// </summary>
    private static async Task SettleRoundAsync(
        ILedgerSource source, string account, long validated, Spends spends, CancellationToken cancellationToken)
    {
        // One listing a round rather than one lookup per ticket: a public node sees one
        // account_objects a round, not two hundred and fifty tx requests.
        IReadOnlyList<LiveTicket> still = await source.LiveTicketsAsync(account, cancellationToken).ConfigureAwait(false);
        HashSet<uint> listed = [.. still.Select(ticket => ticket.Sequence)];

        foreach ((uint ticket, string hash) in spends.WaitingNotIn(listed))
        {
            LedgerTransaction? tx = await source.TransactionAsync(hash, cancellationToken).ConfigureAwait(false);
            spends.Settled(ticket, tx);
        }

        spends.Listed(listed, validated);
    }

    /// <summary>
    /// A refusal before any ledger saw the spend, for a reason that can pass: a <c>tel</c>; a
    /// <c>ter</c> other than <c>terQUEUED</c>; <c>tefMAX_LEDGER</c>, which rippled checks only after
    /// finding the ticket live; or an error answer naming a passing server condition (spec I13). The
    /// ticket is intact and no fee was claimed.
    /// </summary>
    private static bool Passing(string engineResult) =>
        engineResult.StartsWith("tel", StringComparison.Ordinal)
        || (engineResult.StartsWith("ter", StringComparison.Ordinal)
            && !string.Equals(engineResult, "terQUEUED", StringComparison.Ordinal))
        || string.Equals(engineResult, "tefMAX_LEDGER", StringComparison.Ordinal)
        || Busy(engineResult);

    /// <summary>Every load warning the node has given this client so far, through either half of the connection.</summary>
    private static long Warnings(ILedgerSink sink, ILedgerSource source) => sink.LoadWarnings + source.LoadWarnings;

    /// <summary>
    /// The universal errors that name a passing server condition (spec I13). An API v2 client — which
    /// is what this library's client is — receives <c>notSynced</c> for all of the node being not
    /// synced, without a current ledger and without a closed one; an API v1 node answers
    /// <c>noNetwork</c>, <c>noCurrent</c> and <c>noClosed</c> for the same three conditions.
    /// </summary>
    private static bool Busy(string engineResult) =>
        engineResult is "error:tooBusy" or "error:notSynced" or "error:noCurrent" or "error:noNetwork" or "error:noClosed";

    /// <summary>The answers that put a submission in front of a ledger: it may yet spend its ticket.</summary>
    private static bool Reached(string engineResult) =>
        engineResult.StartsWith("tes", StringComparison.Ordinal)
        || engineResult.StartsWith("tec", StringComparison.Ordinal)
        || string.Equals(engineResult, "terQUEUED", StringComparison.Ordinal);

    /// <summary>
    /// An AccountSet that sets nothing, riding one ticket and applying no later than
    /// <paramref name="lastLedger"/>: the cheapest transaction that frees its owner reserve.
    /// </summary>
    private static Dictionary<string, object> Spend(string account, string publicKey, uint ticket, long feeDrops, uint lastLedger) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = account,
        ["Sequence"] = 0u,
        ["TicketSequence"] = ticket,
        ["LastLedgerSequence"] = lastLedger,
        ["Fee"] = feeDrops.ToString(CultureInfo.InvariantCulture),
        ["SigningPubKey"] = publicKey,
    };

    /// <summary>
    /// The tidy-up's bookkeeping for one call, per ticket (spec 4.6). Guarded: a round's answers
    /// arrive in parallel.
    /// </summary>
    private sealed class Spends(IReadOnlyList<uint> ours)
    {
        private readonly Lock _gate = new();
        private readonly HashSet<uint> _toSend = [.. ours];
        private readonly Dictionary<uint, int> _submissions = [];
        private readonly Dictionary<uint, (string Hash, uint LastLedger)> _waiting = [];
        private readonly HashSet<uint> _gone = [];
        private readonly HashSet<uint> _released = [];

        /// <summary>Per ticket: the LastLedgerSequence of every submission not yet shown unable to apply.</summary>
        private readonly Dictionary<uint, List<uint>> _open = [];
        private readonly HashSet<uint> _charged = [];

        private List<uint> Open(uint ticket)
        {
            if (!_open.TryGetValue(ticket, out List<uint>? open))
            {
                open = [];
                _open[ticket] = open;
            }

            return open;
        }

        /// <summary>Nothing left to send and nothing waiting: every ticket is settled or given up on.</summary>
        public bool Finished
        {
            get { lock (_gate) return _toSend.Count == 0 && _waiting.Count == 0; }
        }

        public uint[] ToSend()
        {
            lock (_gate) return [.. _toSend.Order()];
        }

        /// <summary>A submission is about to be sent: it counts against the ticket's five.</summary>
        public void Opened(uint ticket, uint lastLedger)
        {
            lock (_gate)
            {
                _submissions[ticket] = _submissions.GetValueOrDefault(ticket) + 1;
                Open(ticket).Add(lastLedger);
            }
        }

        /// <summary>
        /// Sorts one answer: a spend that reached a ledger — or was refused only after an earlier
        /// copy of the same blob went unanswered — waits for the listing under its hash; a passing
        /// refusal, or a node that could not be reached, stays to be sent again until the ticket has
        /// had its five submissions; anything else is not sent again.
        /// </summary>
        public void Answered(uint ticket, uint lastLedger, SubmitOutcome? outcome)
        {
            lock (_gate)
            {
                bool spent = _submissions[ticket] >= MaxSpendAttempts;

                if (outcome is not null && (Reached(outcome.EngineResult) || outcome.FollowsUnansweredAttempt))
                {
                    // Either may still apply: a new spend would only compete with it, and the listing
                    // or its expiry says which it was (spec 4.6).
                    _toSend.Remove(ticket);
                    _waiting[ticket] = (outcome.TxHash, lastLedger);

                    return;
                }

                // Refused before any ledger saw it: this submission can never apply. Unanswered or
                // cancelled stays open, and a refusal after an unanswered transmission waited above.
                if (outcome is not null) Open(ticket).Remove(lastLedger);

                if (outcome is null || Passing(outcome.EngineResult))
                {
                    if (spent) _toSend.Remove(ticket);

                    return;
                }

                // tefNO_TICKET is not a verdict: the node may have seen the ticket consumed in an open
                // ledger by a transaction that never validates, which leaves it live. Only a listing
                // settles such a ticket, and it is never sent again (I5). A tem, or any other answer,
                // would be refused the same way every time.
                _toSend.Remove(ticket);
            }
        }

        /// <summary>The waiting spends whose ticket this listing no longer shows: each is asked what it did.</summary>
        public IReadOnlyList<(uint Ticket, string Hash)> WaitingNotIn(IReadOnlySet<uint> listed)
        {
            lock (_gate)
            {
                return [.. _waiting.Where(pair => !listed.Contains(pair.Key)).Select(pair => (pair.Key, pair.Value.Hash))];
            }
        }

        /// <summary>
        /// A waiting spend whose ticket left the ledger: nothing short of its own validated tesSUCCESS
        /// is a release; a ticket that left any other way — a tec on the spend, a straggler first —
        /// is gone, and its reserve is free either way.
        /// </summary>
        public void Settled(uint ticket, LedgerTransaction? tx)
        {
            lock (_gate)
            {
                (string Hash, uint LastLedger) waiting = _waiting[ticket];
                _waiting.Remove(ticket);

                // A validated spend claimed its fee and closes everything for the ticket; one found
                // not validated closes only itself, since something else consumed the ticket in a
                // validated ledger. A lookup that finds nothing has shown neither, so the submission
                // stays open until its LastLedgerSequence passes.
                if (tx is { Validated: true })
                {
                    _charged.Add(ticket);
                    Open(ticket).Clear();
                }
                else if (tx is { Validated: false })
                {
                    Open(ticket).Remove(waiting.LastLedger);
                }

                if (tx is { Validated: true, Succeeded: true }) _released.Add(ticket);
                else _gone.Add(ticket);
            }
        }

        /// <summary>
        /// Applies one listing read at or after <paramref name="validated"/>. A waiting spend whose
        /// ticket is still listed and whose <c>LastLedgerSequence</c> that index has reached has
        /// expired: it never applied and cannot, so its ticket goes back to be sent again while
        /// submissions remain (spec I16). A ticket the listing no longer shows and whose own spend is
        /// not waiting is gone.
        /// </summary>
        public void Listed(IReadOnlySet<uint> listed, long validated)
        {
            lock (_gate)
            {
                foreach (uint ticket in ours)
                {
                    if (listed.Contains(ticket))
                    {
                        Open(ticket).RemoveAll(lastLedger => validated >= lastLedger);

                        if (_waiting.TryGetValue(ticket, out (string Hash, uint LastLedger) waiting) && validated >= waiting.LastLedger)
                        {
                            _waiting.Remove(ticket);
                            if (_submissions.GetValueOrDefault(ticket) < MaxSpendAttempts) _toSend.Add(ticket);
                        }

                        continue;
                    }

                    if (_released.Contains(ticket) || _waiting.ContainsKey(ticket)) continue;

                    _gone.Add(ticket);
                    _toSend.Remove(ticket);
                }
            }
        }

        public TicketRelease Result()
        {
            lock (_gate)
            {
                int alreadyGone = _gone.Count(ticket => !_released.Contains(ticket));
                int inDoubt = ours.Count(ticket => !_charged.Contains(ticket) && _open.TryGetValue(ticket, out List<uint>? open) && open.Count > 0);

                return new TicketRelease(_released.Count, alreadyGone, ours.Count - _released.Count - alreadyGone)
                {
                    Charged = _charged.Count,
                    InDoubt = inDoubt,
                };
            }
        }
    }
}
