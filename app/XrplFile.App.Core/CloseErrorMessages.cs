namespace XrplFile.App.Core;

/// <summary>
/// What a person is told about a deletion that did not happen (design 6). The three refusals have
/// nothing in common in what to do next — wait, check the address, or send somewhere else — so they
/// never share a sentence.
/// </summary>
public static class CloseErrorMessages
{
    /// <summary>
    /// The sentence for a deletion the node refused after it was signed and submitted —
    /// <see cref="XrplFileError.SubmitRefused"/>, as <see cref="ThrowawayAccount.DeleteAsync"/> throws
    /// it. Deliberately not <see cref="PublishErrorMessages"/>'s own sentence for the same error code:
    /// that one is written for the publishing tab, where a refusal costs the open-ledger fee — a few
    /// drops. A refused <c>AccountDelete</c> can cost one owner reserve instead, and the difference is
    /// worth its own sentence rather than borrowing one written for a much smaller number.
    ///
    /// <paramref name="feeClaimed"/> decides which of the two this actually is: only a <c>tec*</c>
    /// engine result applies to the ledger and keeps its fee, and <see cref="ClaimsFee"/> is what
    /// reads that off the node's own answer. <c>tem</c>, <c>tef</c>, <c>tel</c> and <c>ter</c> results
    /// never reach the ledger at all — <c>temDST_IS_SRC</c> above all, the result of closing an
    /// account to itself — and telling somebody a fee was burned when nothing was ever charged is as
    /// wrong as the reverse.
    /// </summary>
    public static string DeletionRefused(bool feeClaimed) => feeClaimed
        ? "The node refused the deletion. The attempt still claimed the fee — one owner reserve."
        : "The node refused the deletion. Nothing was charged.";

    /// <summary>
    /// Whether a node's engine result for a refused submission actually claimed a fee. Only a
    /// <c>tec*</c> result applies to the ledger and keeps the fee it was charged; <c>tem</c>
    /// (malformed), <c>tef</c> (failed before applying), <c>tel</c> (a local check) and <c>ter</c>
    /// (retry) never reach the ledger and never cost anything. A null result — an exception raised
    /// for a reason other than <see cref="XrplFileError.SubmitRefused"/> — is treated as not having
    /// claimed a fee, since nothing here has evidence that it did.
    /// </summary>
    public static bool ClaimsFee(string? engineResult) =>
        engineResult is not null && engineResult.StartsWith("tec", StringComparison.Ordinal);

    public static string For(DeletionOutcome outcome) => outcome switch
    {
        DeletionOutcome.Ready =>
            "This account can be closed now.",
        DeletionOutcome.NotYet =>
            "This account cannot be closed yet. The ledger refuses to delete an account until 255 ledgers have closed since its last transaction, and it must hold enough to cover the fee.",
        DeletionOutcome.DestinationMissing =>
            "There is no such account on this network, and the remainder would have nowhere to go. Check the address.",
        DeletionOutcome.DestinationRefuses =>
            "That destination will not accept this payment: it is the account being closed, it requires a destination tag, or it only accepts senders it has authorised. Send the remainder somewhere else.",
        DeletionOutcome.Submitted =>
            "The account was closed and its remainder is on its way.",
        DeletionOutcome.Queued =>
            "The node queued the deletion rather than applying it. Until a ledger takes it the account still exists and still holds everything, so check again in a moment.",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "no sentence for this outcome"),
    };
}
