using System.Collections.Concurrent;
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

    /// <summary>How many tidy-up spends are in the air at once, the same ceiling the ticket strategy's own batches use by default.</summary>
    private const int MaxInFlight = 25;

    /// <summary>
    /// How many times one ticket's spend is sent before a passing refusal is taken as final. A
    /// burst of spends is what a loaded node answers with <c>telCAN_NOT_QUEUE</c>; five rounds give
    /// its queue room to drain without resending a hopeless spend until the deadline.
    /// </summary>
    private const int MaxSpendAttempts = 5;

    /// <summary>
    /// Finds this library's tickets afresh from the validated ledger, spends each on an AccountSet
    /// that sets nothing, and waits for the outcomes (spec 4.6). Works in rounds — send, list once,
    /// settle, wait — and sends again a spend the node refused for a passing reason. Cancellation is
    /// the deadline: it ends the wait and leaves every unsettled ticket counted as not released.
    /// </summary>
    public static async Task<TicketRelease> ReleaseAsync(
        ILedgerSink sink, ILedgerSource source, ITransactionSigner signer, long feeDrops, TimeSpan poll, CancellationToken cancellationToken)
    {
        string account = signer.Account;
        TicketCensus census;

        try
        {
            census = await FindAsync(source, account, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TicketRelease.None;
        }

        if (census.Ours.Count == 0) return TicketRelease.None;

        HashSet<uint> toSend = [.. census.Ours];
        Dictionary<uint, int> attempts = [];
        Dictionary<uint, string> waiting = [];
        HashSet<uint> gone = [];
        HashSet<uint> released = [];

        try
        {
            while (true)
            {
                await SendRoundAsync(sink, signer, account, feeDrops, toSend, attempts, waiting, cancellationToken).ConfigureAwait(false);

                try
                {
                    await SettleRoundAsync(source, account, census.Ours, toSend, waiting, gone, released, cancellationToken)
                        .ConfigureAwait(false);

                    if (waiting.Count == 0 && toSend.Count == 0) break;
                }
                catch (XrplFileException failure) when (failure.Error == XrplFileError.NodeUnreachable)
                {
                    // A node that could not answer this round has decided nothing: what is settled
                    // stays settled, and the next round reads it again instead of losing every count.
                    // Anything else — a signer refusing, a locked vault — still ends the tidy-up.
                }

                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The deadline, or the caller's own cancellation: the counts stand as they are, and
            // everything not yet settled is not released (spec 4.6).
        }

        int alreadyGone = gone.Count(ticket => !released.Contains(ticket));

        return new TicketRelease(released.Count, alreadyGone, census.Ours.Count - released.Count - alreadyGone);
    }

    /// <summary>
    /// Sends one spend for every ticket still to be sent and sorts each by the node's answer: a
    /// spend that reached a ledger waits for the listing, a passing refusal — or a node that could
    /// not be reached — stays to be sent again until it has had <see cref="MaxSpendAttempts"/>
    /// tries, and anything else is not sent again.
    /// </summary>
    private static async Task SendRoundAsync(
        ILedgerSink sink, ITransactionSigner signer, string account, long feeDrops,
        HashSet<uint> toSend, Dictionary<uint, int> attempts, Dictionary<uint, string> waiting, CancellationToken cancellationToken)
    {
        if (toSend.Count == 0) return;

        // Null: the node could not be reached, which says nothing about the ticket.
        ConcurrentDictionary<uint, SubmitOutcome?> answers = new();

        await Parallel.ForEachAsync(
            toSend.Order().ToArray(),
            new ParallelOptions { MaxDegreeOfParallelism = MaxInFlight, CancellationToken = cancellationToken },
            async (ticket, token) =>
            {
                try
                {
                    answers[ticket] = await sink
                        .SubmitAsync(Spend(account, signer.PublicKey, ticket, feeDrops), signer, token).ConfigureAwait(false);
                }
                catch (XrplFileException failure) when (failure.Error == XrplFileError.NodeUnreachable)
                {
                    answers[ticket] = null;
                }
            }).ConfigureAwait(false);

        foreach ((uint ticket, SubmitOutcome? outcome) in answers)
        {
            int sent = attempts.GetValueOrDefault(ticket) + 1;
            attempts[ticket] = sent;

            if (outcome is null || Passing(outcome.EngineResult))
            {
                if (sent >= MaxSpendAttempts) toSend.Remove(ticket);

                continue;
            }

            toSend.Remove(ticket);

            // tefNO_TICKET is not a verdict: the node may have seen the ticket consumed in an open
            // ledger by a transaction that never validates, which leaves it live. Only a listing
            // settles such a ticket, and it is never sent again (I5). A tem, or any other answer,
            // would be refused the same way every time.
            if (Reached(outcome.EngineResult)) waiting[ticket] = outcome.TxHash;
        }
    }

    /// <summary>
    /// Reads the listing once and settles what it can: a waiting spend whose ticket left the ledger
    /// is asked what it did, and any other ticket the listing no longer shows is gone.
    /// </summary>
    private static async Task SettleRoundAsync(
        ILedgerSource source, string account, IReadOnlyList<uint> ours, HashSet<uint> toSend,
        Dictionary<uint, string> waiting, HashSet<uint> gone, HashSet<uint> released, CancellationToken cancellationToken)
    {
        // One listing a round rather than one lookup per ticket: a spend settles when its ticket
        // leaves the validated ledger, and only then is its own transaction asked what it did. A
        // spend that loses its ticket to a straggler and never validates still settles — as gone —
        // instead of holding the report until the deadline, and a public node sees one
        // account_objects a round, not two hundred and fifty tx requests.
        IReadOnlyList<LiveTicket> still = await source.LiveTicketsAsync(account, cancellationToken).ConfigureAwait(false);
        HashSet<uint> listed = [.. still.Select(ticket => ticket.Sequence)];

        foreach ((uint ticket, string hash) in waiting.ToArray())
        {
            if (listed.Contains(ticket)) continue;

            LedgerTransaction? tx = await source.TransactionAsync(hash, cancellationToken).ConfigureAwait(false);
            waiting.Remove(ticket);

            // Nothing short of its own validated tesSUCCESS is a release; a ticket that left the
            // ledger any other way — a tec on the spend, a straggler first — is gone, and its
            // reserve is free either way.
            if (tx is { Validated: true, Succeeded: true }) released.Add(ticket);
            else gone.Add(ticket);
        }

        // A ticket this listing no longer shows and whose own spend is not waiting is gone: a
        // tefNO_TICKET answer, a refused spend, one given up on. One it still shows holds its
        // reserve, whatever the node said.
        foreach (uint ticket in ours)
        {
            if (released.Contains(ticket) || waiting.ContainsKey(ticket) || listed.Contains(ticket)) continue;

            gone.Add(ticket);
            toSend.Remove(ticket);
        }
    }

    /// <summary>
    /// A refusal before any ledger saw the spend, for a reason that can pass: a <c>tel</c>, or a
    /// <c>ter</c> other than <c>terQUEUED</c>. The ticket is intact and no fee was claimed.
    /// </summary>
    private static bool Passing(string engineResult) =>
        engineResult.StartsWith("tel", StringComparison.Ordinal)
        || (engineResult.StartsWith("ter", StringComparison.Ordinal)
            && !string.Equals(engineResult, "terQUEUED", StringComparison.Ordinal));

    /// <summary>The answers that put a submission in front of a ledger: it may yet spend its ticket.</summary>
    private static bool Reached(string engineResult) =>
        engineResult.StartsWith("tes", StringComparison.Ordinal)
        || engineResult.StartsWith("tec", StringComparison.Ordinal)
        || string.Equals(engineResult, "terQUEUED", StringComparison.Ordinal);

    /// <summary>An AccountSet that sets nothing, riding one ticket: the cheapest transaction that frees its owner reserve.</summary>
    private static Dictionary<string, object> Spend(string account, string publicKey, uint ticket, long feeDrops) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = account,
        ["Sequence"] = 0u,
        ["TicketSequence"] = ticket,
        ["Fee"] = feeDrops.ToString(CultureInfo.InvariantCulture),
        ["SigningPubKey"] = publicKey,
    };
}
