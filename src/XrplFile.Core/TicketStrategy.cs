using System.Collections.Concurrent;
using Xrpl.Wallet;

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
/// lives. So the queue is normally empty when a call returns, and a second call mints again
/// rather than inheriting anything.
///
/// One instance serves one caller at a time: SubmitAllAsync is not re-entrant, and two concurrent
/// calls on the same instance would fight over one ticket queue. The queue is guarded all the
/// same, because within a single call the batch is sent in parallel.
/// </summary>
public sealed class TicketStrategy(
    ILedgerSink sink, ILedgerSource source, XrplWallet wallet, TicketStrategyOptions? options = null) : ISubmissionStrategy
{
    /// <summary>rippled's kMaxValidCount: the most tickets one TicketCreate may mint.</summary>
    public const int MaxTicketsPerMint = 250;

    /// <summary>rippled's kMaxTicketThreshold: the most an account may hold at once.</summary>
    public const int MaxLiveTickets = 250;

    private readonly ILedgerSink _sink = sink;
    private readonly ILedgerSource _source = source;
    private readonly XrplWallet _wallet = wallet;
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

    /// <summary>What a sweep learned about one batch: which markers wrote, and which reached a ledger at all.</summary>
    private sealed record SweepResult(IReadOnlyDictionary<string, string> Landed, IReadOnlySet<string> Seen);

    public long FeeDropsPerTransaction => _options.FeeDrops;

    public string Account => _wallet.ClassicAddress;

    public async Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
    {
        HashSet<string> markers = new(memos.Count, StringComparer.Ordinal);

        foreach (MemoSubmission memo in memos)
        {
            // A memo is recognised in the ledger by its marker and nothing else, so two memos
            // wearing the same one cannot be told apart afterwards. Refusing here keeps that
            // ambiguity off the ledger — and keeps a raw ArgumentException out of the contract.
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
                    + $"the {MaxLiveTickets} live tickets the protocol allows and none of them came back");
            }

            Dictionary<string, int> wanted = new(batch.Count, StringComparer.Ordinal);

            foreach (int index in batch)
            {
                wanted[memos[index].MemoTypeHex] = index;
                attempts[index] = attempts.GetValueOrDefault(index) + 1;
            }

            IReadOnlyDictionary<int, uint> ridden = await SendBatchAsync(memos, batch, cancellationToken).ConfigureAwait(false);

            SweepResult swept = await SweepAsync(wanted.Keys, sweepFrom.Value, batchFrom, cancellationToken).ConfigureAwait(false);

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

    public ValueTask DisposeAsync()
    {
        // Dropping the queue is all this can do, and it is worth being plain about what that
        // means: a ticket still in it stays live on the account afterwards, costing an owner
        // reserve, because no transaction cancels a ticket and nothing re-adopts one. It is a
        // corner rather than the rule — minting is exact, so the queue is normally already empty
        // — and the only ways out are spending it on a transaction of your own, or deleting the
        // account, which removes its tickets with it.
        lock (_ticketLock)
        {
            _tickets.Clear();
            _dispatched.Clear();
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

        while (TicketsInHand < need)
        {
            int room = Math.Min(MaxLiveTickets - LiveTicketCount, MaxTicketsPerMint);
            int mint = Math.Min(need - TicketsInHand, room);

            if (mint <= 0) return firstMintLedger;

            string account = _wallet.ClassicAddress;
            uint sequence = await _sink.SequenceAsync(account, validated: false, cancellationToken).ConfigureAwait(false);

            Dictionary<string, object> transaction = new()
            {
                ["TransactionType"] = "TicketCreate",
                ["Account"] = account,
                ["Sequence"] = sequence,
                ["TicketCount"] = (uint)mint,
                ["Fee"] = _options.FeeDrops.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SigningPubKey"] = _wallet.PublicKey,
            };

            SubmitOutcome outcome = await _sink.SubmitAsync(transaction, _wallet, cancellationToken).ConfigureAwait(false);

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

    /// <summary>Sends one batch in parallel and reports which ticket each memo rode.</summary>
    private async Task<IReadOnlyDictionary<int, uint>> SendBatchAsync(
        IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> batch, CancellationToken cancellationToken)
    {
        using SemaphoreSlim gate = new(_options.MaxInFlight, _options.MaxInFlight);
        ConcurrentDictionary<int, uint> ridden = new();

        IEnumerable<Task> sending = batch.Select(async index =>
        {
            if (!TryTakeTicket(out uint ticket)) return;

            bool spent = false;
            bool held = false;

            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                held = true;

                spent = await SendOneAsync(memos[index], ticket, cancellationToken).ConfigureAwait(false);

                if (spent) ridden[index] = ticket;
            }
            finally
            {
                if (held) gate.Release();

                // Everything between the dequeue and a submission the node actually took is
                // covered here: a cancellation while waiting for the gate, a throw out of the node
                // call, a refusal the node made before any ledger saw it. In every one of those
                // the ticket is intact and alive on the account, and nothing on the XRPL will ever
                // cancel it, so the queue is the only place it can go.
                if (!spent) ReturnTicket(ticket);
            }
        });

        await Task.WhenAll(sending).ConfigureAwait(false);

        return ridden;
    }

    /// <summary>
    /// Sends one memo on one ticket. True when the node took the submission and the ticket must be
    /// presumed spent; false when the node refused it before any ledger saw it, which leaves the
    /// ticket alive and reusable.
    /// </summary>
    private async Task<bool> SendOneAsync(MemoSubmission memo, uint ticket, CancellationToken cancellationToken)
    {
        Dictionary<string, object> fields = new() { ["MemoType"] = memo.MemoTypeHex, ["MemoData"] = memo.MemoDataHex };
        if (memo.MemoFormatHex is not null) fields["MemoFormat"] = memo.MemoFormatHex;

        Dictionary<string, object> transaction = new()
        {
            ["TransactionType"] = "AccountSet",
            ["Account"] = _wallet.ClassicAddress,
            ["Sequence"] = 0u,
            ["TicketSequence"] = ticket,
            ["Fee"] = _options.FeeDrops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SigningPubKey"] = _wallet.PublicKey,
            ["Memos"] = new List<object> { new Dictionary<string, object> { ["Memo"] = fields } },
        };

        SubmitOutcome outcome = await _sink.SubmitAsync(transaction, _wallet, cancellationToken).ConfigureAwait(false);

        // terPRE_TICKET means the ticket is not in a ledger yet, which can only happen if the mint
        // was not awaited. That is a bug here, not a network condition, and it is worth saying so.
        // The node refused it outright, so the ticket is certainly intact and the caller, which
        // hands back anything this method did not report as spent, gives it up to the queue again.
        if (string.Equals(outcome.EngineResult, "terPRE_TICKET", StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"ticket {ticket} was spent before it was in a validated ledger; the mint was not awaited");
        }

        // Three answers leave the ticket in doubt, and every other answer settles it. A tes may
        // still not make it, so it is not given back. A tec is charged against a ledger and really
        // does spend the ticket — it is invisible here, arriving only when the ledger closes, but
        // the sweep sees it. A terQUEUED is holding the ticket pending and may yet land.
        //
        // Everything else — tem, tef, tel, and every ter that is not terQUEUED — never reached a
        // ledger, so the ticket is untouched and still ours. A telINSUF_FEE_P or a
        // telCAN_NOT_QUEUE_FULL is exactly what a hundred-wide burst gets from a loaded node, and
        // treating one as a spent ticket loses an owner reserve per refusal, permanently.
        return outcome.EngineResult.StartsWith("tes", StringComparison.Ordinal)
            || outcome.EngineResult.StartsWith("tec", StringComparison.Ordinal)
            || string.Equals(outcome.EngineResult, "terQUEUED", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the account forward from this call's first possible ledger until every marker has
    /// been seen or the batch's deadline passes. <paramref name="batchFrom"/> is the same window
    /// narrowed to this batch, and it decides only whether a ticket was spent: an earlier
    /// attempt's tec sits below it and was accounted for when that attempt was swept.
    /// </summary>
    private async Task<SweepResult> SweepAsync(
        IReadOnlyCollection<string> markers, long from, long batchFrom, CancellationToken cancellationToken)
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> wanted = new(markers, StringComparer.Ordinal);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.BatchTimeout;

        while (true)
        {
            long to = await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

            await foreach (LedgerTransaction tx in _source
                .AccountTransactionsAsync(_wallet.ClassicAddress, from, to, forward: true, cancellationToken)
                .ConfigureAwait(false))
            {
                if (!tx.Validated || tx.MemoTypeHex is null) continue;
                if (!wanted.Contains(tx.MemoTypeHex)) continue;

                if (tx.LedgerIndex >= batchFrom) seen.Add(tx.MemoTypeHex);

                if (!tx.Succeeded) continue;

                // The first copy in canonical order is the one the format's readers will use, so
                // it is the one whose hash goes in the report.
                found.TryAdd(tx.MemoTypeHex, tx.Hash);
            }

            if (found.Count == wanted.Count) return new SweepResult(found, seen);
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
