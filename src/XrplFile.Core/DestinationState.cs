namespace XrplFile;

/// <summary>
/// What a node says about an account somebody is about to send everything to. The three facts a
/// deletion's preflight needs, and no more: <see cref="AccountState"/> answers none of them, and it
/// sits on the estimate's hot path, so widening it would make every publication pay for something
/// only the closing screen needs.
/// </summary>
/// <param name="Exists">
/// Whether the account is on the ledger at all. An <c>AccountDelete</c> to an account that is not
/// answers <c>tecNO_DST</c> — and a <c>tec</c> result claims the fee, which for this transaction is
/// one owner reserve.
/// </param>
/// <param name="RequiresDestinationTag">
/// Whether the account carries <c>lsfRequireDestTag</c>. Such an account refuses a payment that
/// names no destination tag, and an <c>AccountDelete</c> can carry one but this application never
/// guesses a tag on somebody's behalf.
/// </param>
/// <param name="AuthorisesDepositors">
/// Whether the account carries <c>lsfDepositAuth</c>. Such an account accepts only senders it has
/// preauthorised, which the sending account cannot find out about itself — so this is a warning the
/// screen shows rather than a certainty.
/// </param>
public sealed record DestinationState(bool Exists, bool RequiresDestinationTag, bool AuthorisesDepositors)
{
    /// <summary>No such account. The other two are false because there is nothing to carry a flag.</summary>
    public static DestinationState Absent { get; } = new(false, false, false);
}
