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
    /// Finds this library's tickets afresh from the validated ledger, spends each on an AccountSet
    /// that sets nothing, and waits for the outcomes (spec 4.6). Cancellation is the deadline: it
    /// ends the wait and leaves every unsettled ticket counted as not released.
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

        ConcurrentDictionary<uint, string> waiting = new();
        ConcurrentDictionary<uint, bool> gone = new();
        HashSet<uint> released = [];

        try
        {
            await Parallel.ForEachAsync(
                census.Ours,
                new ParallelOptions { MaxDegreeOfParallelism = MaxInFlight, CancellationToken = cancellationToken },
                async (ticket, token) =>
                {
                    SubmitOutcome outcome = await sink
                        .SubmitAsync(Spend(account, signer.PublicKey, ticket, feeDrops), signer, token).ConfigureAwait(false);

                    if (string.Equals(outcome.EngineResult, "tefNO_TICKET", StringComparison.Ordinal)) gone[ticket] = true;
                    else if (Reached(outcome.EngineResult)) waiting[ticket] = outcome.TxHash;
                }).ConfigureAwait(false);

            // One listing a round rather than one lookup per ticket: a spend settles when its ticket
            // leaves the validated ledger, and only then is its own transaction asked what it did.
            // A spend that loses its ticket to a straggler and never validates still settles — as
            // gone — instead of holding the report until the deadline, and a public node sees one
            // account_objects a round, not two hundred and fifty tx requests.
            while (true)
            {
                IReadOnlyList<LiveTicket> still = await source.LiveTicketsAsync(account, cancellationToken).ConfigureAwait(false);
                HashSet<uint> listed = [.. still.Select(ticket => ticket.Sequence)];

                foreach ((uint ticket, string hash) in waiting.ToArray())
                {
                    if (listed.Contains(ticket)) continue;

                    LedgerTransaction? tx = await source.TransactionAsync(hash, cancellationToken).ConfigureAwait(false);
                    waiting.TryRemove(ticket, out _);

                    // Nothing short of its own validated tesSUCCESS is a release; a ticket that left
                    // the ledger any other way — a tec on the spend, a straggler first — is gone,
                    // and its reserve is free either way.
                    if (tx is { Validated: true, Succeeded: true }) released.Add(ticket);
                    else gone[ticket] = true;
                }

                if (waiting.IsEmpty)
                {
                    // A spend the node refused left its ticket alone — unless something else took
                    // it in the meantime, which the same listing shows.
                    foreach (uint ticket in census.Ours)
                    {
                        if (!released.Contains(ticket) && !listed.Contains(ticket)) gone[ticket] = true;
                    }

                    break;
                }

                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The deadline, or the caller's own cancellation: the counts stand as they are, and
            // everything not yet settled is not released (spec 4.6).
        }

        int alreadyGone = gone.Keys.Count(ticket => !released.Contains(ticket));

        return new TicketRelease(released.Count, alreadyGone, census.Ours.Count - released.Count - alreadyGone);
    }

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
