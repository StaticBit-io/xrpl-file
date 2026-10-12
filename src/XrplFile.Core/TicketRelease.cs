namespace XrplFile;

/// <summary>
/// What spending this library's leftover tickets came to (spec 4.6). Each ticket the tidy-up
/// found is in exactly one count.
/// </summary>
/// <param name="Released">Spent by the tidy-up's own transaction, validated with tesSUCCESS. Nothing short of validation counts.</param>
/// <param name="AlreadyGone">
/// Consumed by something else: the node answered tefNO_TICKET, or the ledger no longer listed the
/// ticket when the results were checked. Its reserve is free either way.
/// </param>
/// <param name="NotReleased">Any other outcome, or no outcome before the tidy-up's time ran out. Still live and still locking an owner reserve.</param>
public sealed record TicketRelease(int Released, int AlreadyGone, int NotReleased)
{
    /// <summary>Nothing found, or nothing tried.</summary>
    public static TicketRelease None { get; } = new(0, 0, 0);
}
