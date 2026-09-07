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
    private readonly Queue<uint> _tickets = new();

    /// <summary>The earliest ledger any of this strategy's transactions could be in.</summary>
    private long _sweepFrom;

    public async Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
    {
        Dictionary<int, string> landed = [];
        Dictionary<int, int> attempts = [];
        List<int> pending = [.. Enumerable.Range(0, memos.Count)];

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int take = Math.Min(pending.Count, _options.TicketsPerBatch);
            await EnsureTicketsAsync(take, cancellationToken).ConfigureAwait(false);

            List<int> batch = [.. pending.Take(Math.Min(take, _tickets.Count))];
            Dictionary<string, int> wanted = batch.ToDictionary(index => memos[index].MemoTypeHex, index => index);

            foreach (int index in batch)
            {
                attempts[index] = attempts.GetValueOrDefault(index) + 1;
            }

            await SendBatchAsync(memos, batch, cancellationToken).ConfigureAwait(false);

            IReadOnlyDictionary<string, string> seen = await SweepAsync(wanted.Keys, cancellationToken).ConfigureAwait(false);

            foreach ((string marker, string hash) in seen)
            {
                landed[wanted[marker]] = hash;
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
        // Unspent tickets are deliberately left alone. They cost an owner reserve each and there
        // is no transaction that cancels one, so the only ways out are spending them on the next
        // publication or deleting the account, which removes them with it.
        _tickets.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>Mints until at least this many tickets are in hand, never past what the protocol allows.</summary>
    private async Task EnsureTicketsAsync(int need, CancellationToken cancellationToken)
    {
        while (_tickets.Count < need)
        {
            int room = Math.Min(MaxLiveTickets - _tickets.Count, MaxTicketsPerMint);
            int mint = Math.Min(need - _tickets.Count, room);

            if (mint <= 0) return;

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
            await WaitForSequenceAsync(account, sequence + (uint)mint + 1, cancellationToken).ConfigureAwait(false);

            // Exactly rippled's arithmetic: a TicketCreate at S with count N mints S+1 through S+N.
            for (uint ticket = sequence + 1; ticket <= sequence + (uint)mint; ticket++)
            {
                _tickets.Enqueue(ticket);
            }

            if (_sweepFrom == 0)
            {
                LedgerTransaction? mintTx = await _source.TransactionAsync(outcome.TxHash, cancellationToken).ConfigureAwait(false);
                _sweepFrom = mintTx?.LedgerIndex ?? await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SendBatchAsync(IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> batch, CancellationToken cancellationToken)
    {
        using SemaphoreSlim gate = new(_options.MaxInFlight, _options.MaxInFlight);

        IEnumerable<Task> sending = batch.Select(async index =>
        {
            uint ticket;
            lock (_tickets)
            {
                if (!_tickets.TryDequeue(out ticket)) return;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await SendOneAsync(memos[index], ticket, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(sending).ConfigureAwait(false);
    }

    private async Task SendOneAsync(MemoSubmission memo, uint ticket, CancellationToken cancellationToken)
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
        if (string.Equals(outcome.EngineResult, "terPRE_TICKET", StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"ticket {ticket} was spent before it was in a validated ledger; the mint was not awaited");
        }

        // A tem or a tef never reached a ledger, so the ticket is untouched and still ours. Giving
        // it back is the difference between one wasted owner reserve per malformed submission and
        // none. A tec is the opposite case and the ticket really is gone, but a tec is not visible
        // here: it arrives when the ledger closes, and only the sweep sees it.
        if (outcome.EngineResult.StartsWith("tem", StringComparison.Ordinal)
            || outcome.EngineResult.StartsWith("tef", StringComparison.Ordinal))
        {
            lock (_tickets) _tickets.Enqueue(ticket);
            return;
        }

        // Everything else is left to the sweep. A tesSUCCESS may still not make it and a terQUEUED
        // has spent nothing and may yet land, so neither is believed here. The sweep decides, and
        // a memo it does not find comes round again on a fresh ticket.
    }

    /// <summary>
    /// Reads the account forward from the batch's first possible ledger until every marker has
    /// been seen or the batch's deadline passes.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> SweepAsync(
        IReadOnlyCollection<string> markers, CancellationToken cancellationToken)
    {
        Dictionary<string, string> found = [];
        HashSet<string> wanted = [.. markers];
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.BatchTimeout;

        while (true)
        {
            long to = await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

            await foreach (LedgerTransaction tx in _source
                .AccountTransactionsAsync(_wallet.ClassicAddress, _sweepFrom, to, forward: true, cancellationToken)
                .ConfigureAwait(false))
            {
                if (!tx.Succeeded || !tx.Validated || tx.MemoTypeHex is null) continue;
                if (!wanted.Contains(tx.MemoTypeHex)) continue;

                // The first copy in canonical order is the one the format's readers will use, so
                // it is the one whose hash goes in the report.
                found.TryAdd(tx.MemoTypeHex, tx.Hash);
            }

            if (found.Count == wanted.Count) return found;
            if (DateTimeOffset.UtcNow >= deadline) return found;

            await Task.Delay(_options.SweepPoll, cancellationToken).ConfigureAwait(false);
        }
    }

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
