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
}
