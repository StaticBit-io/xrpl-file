using System.Collections.Concurrent;

namespace XrplFile;

public sealed class TicketStrategyOptions
{
    public int FeeDrops { get; init; } = 12;

    /// <summary>
    /// Tickets minted at a time, and so the most transactions in flight at once. Every live
    /// ticket locks an owner reserve until it is spent, so this is money as much as speed. It is
    /// capped at what the protocol allows, and it doubles as the open-ledger guard: a node was
    /// measured refusing one account beyond about a thousand queued transactions.
    /// </summary>
    public int TicketsPerBatch { get; init; } = 100;

    /// <summary>How many submissions are in the air at once. The node answers each one.</summary>
    public int MaxInFlight { get; init; } = 25;

    /// <summary>How long a batch may take to appear in the ledger before its stragglers are retried.</summary>
    public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan SweepPoll { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan ValidationPoll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often one memo may be attempted before the publication is called off.</summary>
    public int MaxAttemptsPerMemo { get; init; } = 3;
}

/// <summary>
/// Many transactions at once, each on its own ticket.
///
/// A ticket frees a transaction from the account's sequence, so they no longer have to be sent
/// one after another. The price is a lifecycle: tickets are minted in batches, each live one
/// locks an owner reserve, and a transaction that reaches the ledger with a tec code has spent
/// its ticket without writing anything, so its retry needs a new one.
///
/// The batch loop is mint, submit, reconcile, carry over. Reconciliation is a sweep of
/// account_tx for the batch's markers, and it exists because the usual way of waiting for
/// validation cannot work here: a ticketed transaction does not move the account's sequence, so
/// there is nothing to watch. The sweep answers the better question anyway — not "has time
/// passed" but "which of these actually landed".
///
/// Minting is exact: a batch asks for one ticket per memo it is about to send, because nothing on
/// the XRPL cancels a ticket and an unspent one locks an owner reserve for as long as the account
/// lives. Before minting, a batch takes this library's leftover tickets already on the account —
/// all of them, not only as many as it needs — so the queue can carry a surplus from one call to
/// the next; whatever is still unspent when the publication succeeds goes in the tidy-up.
///
/// One instance serves one caller at a time: SubmitAllAsync is not re-entrant, and two concurrent
/// calls on the same instance would fight over one ticket queue. The queue is guarded all the
/// same, because within a single call the batch is sent in parallel.
/// </summary>
public sealed class TicketStrategy(
    ILedgerSink sink, ILedgerSource source, ITransactionSigner signer, TicketStrategyOptions? options = null) : ISubmissionStrategy
{
    /// <summary>rippled's kMaxValidCount: the most tickets one TicketCreate may mint.</summary>
    public const int MaxTicketsPerMint = 250;

    /// <summary>rippled's kMaxTicketThreshold: the most an account may hold at once.</summary>
    public const int MaxLiveTickets = 250;

    private readonly ILedgerSink _sink = sink;
    private readonly ILedgerSource _source = source;
    private readonly ITransactionSigner _signer = signer;
    private readonly TicketStrategyOptions _options = options ?? new TicketStrategyOptions();

    /// <summary>Guards both ticket collections. A batch is sent in parallel; the queue is not.</summary>
    private readonly Lock _ticketLock = new();

    /// <summary>Tickets in hand: minted, or handed back by a submission that never reached a ledger.</summary>
    private readonly Queue<uint> _tickets = new();

    /// <summary>
    /// Tickets that left the queue on a submission and are not yet known to be spent. They are
    /// alive on the account exactly as much as the ones in hand, and the protocol counts them, so
    /// the mint has to count them too.
    /// </summary>
    private readonly HashSet<uint> _dispatched = [];

    /// <summary>
    /// Tickets the node answered <c>tefNO_TICKET</c> for. They never go back in the queue, and a
    /// later look never picks them up again, even while the validated ledger still lists them
    /// (spec I5): that is what keeps the strategy from cycling on a ticket consumed in an open
    /// ledger by a transaction that has not validated. The tidy-up decides what became of them.
    /// </summary>
    private readonly HashSet<uint> _gone = [];

    /// <summary>Which mints this instance has already judged ours or somebody else's, so a second look costs no lookups.</summary>
    private readonly Dictionary<string, bool> _creatorVerdicts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What one submission did to the ticket it rode.</summary>
    private enum TicketFate
    {
        /// <summary>The node refused it before any ledger saw it: the ticket is untouched and still ours.</summary>
        Intact,

        /// <summary>The node took it: the ticket is presumed spent until the sweep says otherwise.</summary>
        Spent,

        /// <summary>
        /// The node answered <c>tefNO_TICKET</c>: another transaction has the ticket — a stopped
        /// run's straggler, or this strategy's own earlier submission whose answer was lost.
        /// </summary>
        Gone,
    }

    /// <summary>Which ticket each memo of a batch rode, and which memos met a ticket that was gone.</summary>
    private sealed record BatchSend(IReadOnlyDictionary<int, uint> Ridden, IReadOnlyCollection<int> Gone);

    /// <summary>What a sweep learned about one batch: which markers wrote, and which reached a ledger at all.</summary>
    private sealed record SweepResult(IReadOnlyDictionary<string, string> Landed, IReadOnlySet<string> Seen)
    {
        public static SweepResult Empty { get; } = new(new Dictionary<string, string>(), new HashSet<string>());
    }

    public long FeeDropsPerTransaction => _options.FeeDrops;

    public string Account => _signer.Account;

    public async Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
    {
        HashSet<string> markers = new(memos.Count, StringComparer.Ordinal);

        foreach (MemoSubmission memo in memos)
        {
            // A memo is recognised in the ledger by its marker and its data, so two memos wearing
            // the same marker could still be told apart by data — but not if their data is equal,
            // and the writer never sends two such memos. Refusing here keeps the ambiguity off the
            // ledger — and keeps a raw ArgumentException out of the contract.
            if (!markers.Add(memo.MemoTypeHex))
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"two memos carry the marker {memo.MemoTypeHex}, which the sweep could not tell apart");
            }
        }

        Dictionary<int, string> landed = [];
        Dictionary<int, int> attempts = [];
        List<int> pending = [.. Enumerable.Range(0, memos.Count)];

        // The sweep window belongs to this call and not to the instance. Chunk markers carry no
        // publication scope — xf/c/0 is xf/c/0 for every file this account ever published — so a
        // window opened before an earlier publication matches that publication's transactions and
        // reports their hashes for memos this call never landed.
        long? sweepFrom = null;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int take = Math.Min(pending.Count, _options.TicketsPerBatch);
            long? mintedAt = await EnsureTicketsAsync(take, cancellationToken).ConfigureAwait(false);

            // Nothing this batch sends can land before the mint that paid for it. With no mint,
            // the floor falls back to one past the ledger already open — see
            // LedgerAfterTheOneAlreadyOpenAsync for why one past it, not the open ledger itself.
            long batchFrom = mintedAt ?? await LedgerAfterTheOneAlreadyOpenAsync(cancellationToken).ConfigureAwait(false);
            sweepFrom ??= batchFrom;

            List<int> batch = [.. pending.Take(Math.Min(take, TicketsInHand))];

            if (batch.Count == 0)
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"no ticket is left for the {pending.Count} memos still waiting: the account already holds "
                    + $"the {MaxLiveTickets} live tickets the protocol allows. They are either this run's own, "
                    + "dispatched and never seen in a ledger, or an interrupted run's leftovers — nothing "
                    + "cancels a ticket, so they have to be spent before this file can go on");
            }

            Dictionary<string, int> wanted = new(batch.Count, StringComparer.Ordinal);

            foreach (int index in batch)
            {
                wanted[memos[index].MemoTypeHex] = index;
                attempts[index] = attempts.GetValueOrDefault(index) + 1;
            }

            BatchSend sent = await SendBatchAsync(memos, batch, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<int, uint> ridden = sent.Ridden;

            // A memo whose ticket was gone was never applied: there is nothing of it to sweep for,
            // and it did nothing to deserve an attempt (spec I5). It stays pending and goes out
            // again on the next ticket.
            foreach (int index in sent.Gone)
            {
                attempts[index]--;
                wanted.Remove(memos[index].MemoTypeHex);
            }

            SweepResult swept = wanted.Count == 0
                ? SweepResult.Empty
                : await SweepAsync(
                    wanted.ToDictionary(pair => pair.Key, pair => memos[pair.Value].MemoDataHex, StringComparer.Ordinal),
                    sweepFrom.Value, batchFrom, cancellationToken).ConfigureAwait(false);

            foreach ((string marker, string hash) in swept.Landed)
            {
                landed[wanted[marker]] = hash;
            }

            // Reaching a ledger is what spends a ticket, whether the transaction wrote its memo or
            // died with a tec. A submission the sweep cannot find anywhere may still be in the
            // node's queue holding its ticket, so that one keeps counting as alive.
            foreach (string marker in swept.Seen)
            {
                if (ridden.TryGetValue(wanted[marker], out uint spent)) ForgetTicket(spent);
            }

            submitted?.Report(landed.Count);

            pending = [.. pending.Where(index => !landed.ContainsKey(index))];

            if (pending.FirstOrDefault(index => attempts.GetValueOrDefault(index) >= _options.MaxAttemptsPerMemo, -1) is int stuck and >= 0)
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"the memo marked {memos[stuck].MemoTypeHex} did not reach the ledger in {_options.MaxAttemptsPerMemo} attempts");
            }
        }

        return [.. landed.OrderBy(pair => pair.Key).Select(pair => new SubmittedMemo(pair.Key, pair.Value))];
    }

    public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken)
    {
        // Whatever this instance still holds is about to be spent by the tidy-up, so nothing here
        // may ride it afterwards.
        lock (_ticketLock) _tickets.Clear();

        return LeftoverTickets.ReleaseAsync(_sink, _source, _signer, _options.FeeDrops, _options.ValidationPoll, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        // Dropping the queue is all this can do. A ticket still in it stays live on the account,
        // costing an owner reserve, because no transaction cancels a ticket. It is not lost: it
        // carries this library's mark, so the next publication from this account picks it up or
        // spends it in its tidy-up (spec I4, I6) — and deleting the account removes it regardless.
        lock (_ticketLock)
        {
            _tickets.Clear();
            _dispatched.Clear();
            _gone.Clear();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Tickets in hand, ready to be spent.</summary>
    private int TicketsInHand
    {
        get { lock (_ticketLock) return _tickets.Count; }
    }

    /// <summary>
    /// Tickets alive on the account: the ones in hand plus the ones dispatched and not yet known
    /// to have reached a ledger. Counting only the ones in hand is how two batches of 250 put 500
    /// tickets on one account and the next mint comes back tecDIR_FULL.
    /// </summary>
    private int LiveTicketCount
    {
        get { lock (_ticketLock) return _tickets.Count + _dispatched.Count; }
    }

    private bool TryTakeTicket(out uint ticket)
    {
        lock (_ticketLock)
        {
            if (!_tickets.TryDequeue(out ticket)) return false;

            _dispatched.Add(ticket);
            return true;
        }
    }

    /// <summary>Hands a ticket back to the queue: it was never spent, and nothing else can free it.</summary>
    private void ReturnTicket(uint ticket)
    {
        lock (_ticketLock)
        {
            _dispatched.Remove(ticket);
            _tickets.Enqueue(ticket);
        }
    }

    /// <summary>Drops a ticket that reached a ledger — spent, and no longer alive on the account.</summary>
    private void ForgetTicket(uint ticket)
    {
        lock (_ticketLock) _dispatched.Remove(ticket);
    }

    /// <summary>Drops a ticket the node said is gone, for good (spec I5).</summary>
    private void DropGoneTicket(uint ticket)
    {
        lock (_ticketLock)
        {
            _dispatched.Remove(ticket);
            _gone.Add(ticket);
        }
    }

    /// <summary>
    /// Takes into the queue this library's live tickets that this instance does not already hold,
    /// has not dispatched and has not found gone (spec I4), lowest first. Returns how many it took.
    /// </summary>
    private async Task<int> AdoptLeftoversAsync(string account, CancellationToken cancellationToken)
    {
        TicketCensus census = await LeftoverTickets.FindAsync(_source, account, _creatorVerdicts, cancellationToken).ConfigureAwait(false);

        lock (_ticketLock)
        {
            HashSet<uint> known = [.. _tickets, .. _dispatched, .. _gone];
            int adopted = 0;

            foreach (uint ticket in census.Ours)
            {
                if (known.Contains(ticket)) continue;

                _tickets.Enqueue(ticket);
                adopted++;
            }

            return adopted;
        }
    }

    private void AddTickets(uint first, uint last)
    {
        lock (_ticketLock)
        {
            for (uint ticket = first; ticket <= last; ticket++) _tickets.Enqueue(ticket);
        }
    }

    /// <summary>
    /// Mints until at least this many tickets are in hand, never past what the protocol allows.
    /// Returns the ledger of the first mint it made — the earliest ledger anything sent afterwards
    /// can be in — or null when it minted nothing.
    /// </summary>
    private async Task<long?> EnsureTicketsAsync(int need, CancellationToken cancellationToken)
    {
        long? firstMintLedger = null;
        string account = _signer.Account;

        while (TicketsInHand < need)
        {
            // Headroom belongs to the account, not to this instance. A cancelled run leaks its
            // live tickets (see DisposeAsync), and a strategy counting only what it minted itself
            // asks for tickets the account has no room for: the mint is answered tecDIR_FULL, and
            // so is every retry. AccountRoot's TicketCount is the ledger's own number; the
            // instance's own count is the guard against a node whose validated view has not yet
            // caught up with a mint this loop has just made.
            AccountState state = await _source.AccountStateAsync(account, cancellationToken).ConfigureAwait(false);

            // The ledger holds more live tickets than this instance accounts for: some of them may be
            // this library's, left by a run that stopped, or minted by one whose mint validated after
            // this instance last looked (spec I4). Taking them first means the mint below covers only
            // the shortfall. Nothing new taken means the rest are somebody else's, and the mint goes on.
            if (state.TicketCount > LiveTicketCount
                && await AdoptLeftoversAsync(account, cancellationToken).ConfigureAwait(false) > 0)
            {
                continue;
            }

            int live = Math.Max(state.TicketCount, LiveTicketCount);

            int room = Math.Min(MaxLiveTickets - live, MaxTicketsPerMint);
            int mint = Math.Min(need - TicketsInHand, room);

            if (mint <= 0) return firstMintLedger;

            uint sequence = await _sink.SequenceAsync(account, validated: false, cancellationToken).ConfigureAwait(false);

            Dictionary<string, object> transaction = new()
            {
                ["TransactionType"] = "TicketCreate",
                ["Account"] = account,
                ["Sequence"] = sequence,
                ["TicketCount"] = (uint)mint,
                ["Fee"] = _options.FeeDrops.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SigningPubKey"] = _signer.PublicKey,
                // The mark is how a later publication — or this one's tidy-up — knows these
                // tickets are this library's to spend (spec I2). It carries no data.
                ["Memos"] = new List<object>
                {
                    new Dictionary<string, object> { ["Memo"] = new Dictionary<string, object> { ["MemoType"] = MemoLayout.TicketMarkerHex } },
                },
            };

            SubmitOutcome outcome = await _sink.SubmitAsync(transaction, _signer, cancellationToken).ConfigureAwait(false);

            if (outcome.EngineResult is not ("tesSUCCESS" or "terQUEUED"))
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"the node answered {outcome.EngineResult} to a request for {mint} tickets");
            }

            // The mint is the only transaction here that moves the sequence, so it is the only one
            // whose validation can be awaited by watching it. Everything after rides a ticket and
            // leaves the sequence alone.
            //
            // One step is the whole wait, not mint + 1: a TicketCreate takes the sequence whether
            // it succeeds or fails, and one that lands with a tec — tecINSUFFICIENT_RESERVE is
            // fifty XRP of owner reserve for 250 tickets — moves it by exactly one and stops
            // there. Waiting for mint + 1 would spin out the whole validation timeout and report a
            // validation timeout for something that validated and failed.
            await WaitForSequenceAsync(account, sequence + 1, cancellationToken).ConfigureAwait(false);

            LedgerTransaction? minted = await _source.TransactionAsync(outcome.TxHash, cancellationToken).ConfigureAwait(false);

            if (minted is not { Validated: true })
            {
                // The node cannot yet show the mint as validated — either it does not have the
                // transaction at all, or it is holding it unvalidated, in which case meta (and so
                // Succeeded) is absent and must not be read as a failure. Its arithmetic says the
                // same thing either way: only a TicketCreate that succeeded moves the sequence by
                // the ticket count and one more.
                await WaitForSequenceAsync(account, sequence + (uint)mint + 1, cancellationToken).ConfigureAwait(false);
            }
            else if (!minted.Succeeded)
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"the mint {outcome.TxHash} for {mint} tickets validated in ledger {minted.LedgerIndex} and failed with "
                    + $"{minted.Result ?? "an unnamed result"}; an owner reserve too small for the tickets is the usual cause");
            }

            // Exactly rippled's arithmetic: a TicketCreate at S with count N mints S+1 through S+N.
            AddTickets(sequence + 1, sequence + (uint)mint);

            // minted.LedgerIndex is only trustworthy once the node calls the mint validated: an
            // unvalidated tx answer can carry no ledger index at all (it reads back as the default,
            // 0), and anchoring the sweep there would open the window from the start of the chain.
            // The fallback is the same floor SubmitAllAsync's own no-mint case uses, and for the
            // same reason — see LedgerAfterTheOneAlreadyOpenAsync.
            firstMintLedger ??= minted is { Validated: true } validatedMint
                ? validatedMint.LedgerIndex
                : await LedgerAfterTheOneAlreadyOpenAsync(cancellationToken).ConfigureAwait(false);
        }

        return firstMintLedger;
    }

    /// <summary>Sends one batch in parallel and reports which ticket each memo rode, and which memos met a gone ticket.</summary>
    private async Task<BatchSend> SendBatchAsync(
        IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> batch, CancellationToken cancellationToken)
    {
        using SemaphoreSlim gate = new(_options.MaxInFlight, _options.MaxInFlight);
        ConcurrentDictionary<int, uint> ridden = new();
        ConcurrentBag<int> gone = [];

        IEnumerable<Task> sending = batch.Select(async index =>
        {
            if (!TryTakeTicket(out uint ticket)) return;

            TicketFate fate = TicketFate.Intact;
            bool held = false;

            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                held = true;

                fate = await SendOneAsync(memos[index], ticket, cancellationToken).ConfigureAwait(false);

                if (fate == TicketFate.Spent) ridden[index] = ticket;
                else if (fate == TicketFate.Gone) gone.Add(index);
            }
            finally
            {
                if (held) gate.Release();

                // Everything between the dequeue and a submission the node actually took is
                // covered here: a cancellation while waiting for the gate, a throw out of the node
                // call, a refusal the node made before any ledger saw it. In every one of those
                // the ticket is intact and alive on the account, and nothing on the XRPL will ever
                // cancel it, so the queue is the only place it can go. A gone ticket is the one
                // exception: returning it would circulate it for ever (spec I5).
                if (fate == TicketFate.Gone) DropGoneTicket(ticket);
                else if (fate == TicketFate.Intact) ReturnTicket(ticket);
            }
        });

        await Task.WhenAll(sending).ConfigureAwait(false);

        return new BatchSend(ridden, [.. gone]);
    }

    /// <summary>
    /// Sends one memo on one ticket and says what became of the ticket: spent when the node took
    /// the submission, gone when the node says another transaction already has it, intact when the
    /// node refused it before any ledger saw it.
    /// </summary>
    private async Task<TicketFate> SendOneAsync(MemoSubmission memo, uint ticket, CancellationToken cancellationToken)
    {
        Dictionary<string, object> fields = new() { ["MemoType"] = memo.MemoTypeHex, ["MemoData"] = memo.MemoDataHex };
        if (memo.MemoFormatHex is not null) fields["MemoFormat"] = memo.MemoFormatHex;

        Dictionary<string, object> transaction = new()
        {
            ["TransactionType"] = "AccountSet",
            ["Account"] = _signer.Account,
            ["Sequence"] = 0u,
            ["TicketSequence"] = ticket,
            ["Fee"] = _options.FeeDrops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SigningPubKey"] = _signer.PublicKey,
            ["Memos"] = new List<object> { new Dictionary<string, object> { ["Memo"] = fields } },
        };

        SubmitOutcome outcome = await _sink.SubmitAsync(transaction, _signer, cancellationToken).ConfigureAwait(false);

        // terPRE_TICKET means the ticket is not in a ledger yet, which can only happen if the mint
        // was not awaited. That is a bug here, not a network condition, and it is worth saying so.
        // The node refused it outright, so the ticket is certainly intact and the caller, which
        // hands back anything this method did not report as spent, gives it up to the queue again.
        if (string.Equals(outcome.EngineResult, "terPRE_TICKET", StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"ticket {ticket} was spent before it was in a validated ledger; the mint was not awaited");
        }

        // The ticket is not in the ledger and lies below the account's sequence: some other
        // transaction consumed it — a stopped run's straggler, or this strategy's own earlier
        // submission whose answer was lost. A tef is never applied, so this costs no fee (spec I5).
        if (string.Equals(outcome.EngineResult, "tefNO_TICKET", StringComparison.Ordinal)) return TicketFate.Gone;

        // Three answers leave the ticket in doubt, and every other answer settles it. A tes may
        // still not make it, so it is not given back. A tec is charged against a ledger and really
        // does spend the ticket — it is invisible here, arriving only when the ledger closes, but
        // the sweep sees it. A terQUEUED is holding the ticket pending and may yet land.
        //
        // Everything else — tem, every tef but tefNO_TICKET, tel, and every ter that is not
        // terQUEUED — never reached a ledger, so the ticket is untouched and still ours. A
        // telINSUF_FEE_P or a telCAN_NOT_QUEUE_FULL is exactly what a hundred-wide burst gets from
        // a loaded node, and treating one as a spent ticket loses an owner reserve per refusal,
        // permanently.
        return outcome.EngineResult.StartsWith("tes", StringComparison.Ordinal)
            || outcome.EngineResult.StartsWith("tec", StringComparison.Ordinal)
            || string.Equals(outcome.EngineResult, "terQUEUED", StringComparison.Ordinal)
            ? TicketFate.Spent
            : TicketFate.Intact;
    }

    /// <summary>
    /// Reads the account forward from this call's first possible ledger until every marker has
    /// been seen or the batch's deadline passes. A transaction counts for a memo only when it
    /// carries both the memo's marker and its data (spec I12): a marker names an index, not a
    /// file, and a straggler of another file's publication can wear it. <paramref name="batchFrom"/>
    /// is the same window narrowed to this batch, and it decides only whether a ticket was spent:
    /// an earlier attempt's tec sits below it and was accounted for when that attempt was swept.
    /// </summary>
    /// <param name="markers">Marker → the data the memo wearing it carries.</param>
    private async Task<SweepResult> SweepAsync(
        IReadOnlyDictionary<string, string> markers, long from, long batchFrom, CancellationToken cancellationToken)
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.BatchTimeout;

        while (true)
        {
            long to = await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

            await foreach (LedgerTransaction tx in _source
                .AccountTransactionsAsync(_signer.Account, from, to, forward: true, cancellationToken)
                .ConfigureAwait(false))
            {
                if (!tx.Validated || tx.MemoTypeHex is null) continue;
                if (!markers.TryGetValue(tx.MemoTypeHex, out string? data)) continue;
                if (!string.Equals(tx.MemoDataHex, data, StringComparison.OrdinalIgnoreCase)) continue;

                if (tx.LedgerIndex >= batchFrom) seen.Add(tx.MemoTypeHex);

                if (!tx.Succeeded) continue;

                // The first copy in canonical order is the one the format's readers will use, so
                // it is the one whose hash goes in the report.
                found.TryAdd(tx.MemoTypeHex, tx.Hash);
            }

            if (found.Count == markers.Count) return new SweepResult(found, seen);
            if (DateTimeOffset.UtcNow >= deadline) return new SweepResult(found, seen);

            await Task.Delay(_options.SweepPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One past the ledger already open when read: the floor to anchor a sweep at when there is no
    /// mint (or no validated mint) to anchor it to instead. Nothing this call submits afterwards
    /// can land at or before the ledger that was already open — everything it sends lands strictly
    /// later — so the floor excludes that ledger rather than including it. Anchoring at the open
    /// ledger itself would let a retried publication reusing the same chunk markers match an
    /// earlier attempt's transaction sitting in that one-ledger window instead of its own.
    /// </summary>
    private async Task<long> LedgerAfterTheOneAlreadyOpenAsync(CancellationToken cancellationToken) =>
        await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false) + 1;

    private async Task WaitForSequenceAsync(string account, uint expected, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.ValidationTimeout;

        while (true)
        {
            uint validated;

            try
            {
                validated = await _sink.SequenceAsync(account, validated: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Xrpl.Client.Exceptions.TimeoutException)
            {
                validated = 0;
            }

            if (validated >= expected) return;

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new XrplFileException(XrplFileError.ValidationTimeout,
                    $"the mint for {account} did not validate within {_options.ValidationTimeout}");
            }

            await Task.Delay(_options.ValidationPoll, cancellationToken).ConfigureAwait(false);
        }
    }
}
