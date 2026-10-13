namespace XrplFile;

/// <summary>
/// What spending this library's leftover tickets came to (spec 4.6). Each ticket the tidy-up
/// found is in exactly one count.
/// </summary>
/// <param name="Released">Spent by the tidy-up's own transaction, validated with tesSUCCESS. Nothing short of validation counts.</param>
/// <param name="AlreadyGone">
/// No longer listed by the validated ledger when the results were checked, and the tidy-up's own
/// spend did not validate with tesSUCCESS: something else consumed it, or the spend validated with a
/// tec. Its reserve is free either way. A tefNO_TICKET answer alone does not count while the ledger
/// still lists the ticket.
/// </param>
/// <param name="NotReleased">Any other outcome, or no outcome before the tidy-up's time ran out. Still live and still locking an owner reserve.</param>
/// <remarks>
/// All three counts zero means nothing was found <em>or nothing was settled</em>: a tidy-up cut off
/// before its census finished, or one that failed (see <see cref="PublicationReport.TicketReleaseFailure"/>),
/// also reports zeros. Zeros alone do not say the account holds none of this library's tickets.
/// <see cref="Charged"/> and <see cref="InDoubt"/> are not part of that partition: they count the fees,
/// across the other three, that were certainly claimed and that may still be.
/// </remarks>
public sealed record TicketRelease(int Released, int AlreadyGone, int NotReleased)
{
    /// <summary>Nothing found, or nothing tried.</summary>
    public static TicketRelease None { get; } = new(0, 0, 0);

    /// <summary>
    /// Tickets one of whose spends validated, with tesSUCCESS or a tec: a fee was certainly claimed
    /// (spec 4.6, I17). Every released ticket is charged; an already-gone one is when its own spend's
    /// tec consumed it. At most one fee per ticket, however often its spend was sent.
    /// </summary>
    public int Charged { get; init; }

    /// <summary>
    /// Tickets not charged with a submission still open: taken by the node and not settled, never
    /// answered, cancelled in flight, or refused only after an unanswered transmission of the same
    /// blob. Such a spend may still apply, and claim its fee, until its LastLedgerSequence passes.
    /// </summary>
    public int InDoubt { get; init; }
}
