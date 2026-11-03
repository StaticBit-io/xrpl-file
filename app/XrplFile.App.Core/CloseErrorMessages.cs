using XrplFile.App.Core.Localization;

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
    public static UiText DeletionRefused(bool feeClaimed) => feeClaimed
        ? CloseText.RefusedFeeClaimed
        : CloseText.RefusedNothingCharged;

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

    public static UiText For(DeletionOutcome outcome) => outcome switch
    {
        DeletionOutcome.Ready => CloseText.OutcomeReady,
        DeletionOutcome.NotYet => CloseText.OutcomeNotYet,
        DeletionOutcome.DestinationMissing => CloseText.OutcomeDestinationMissing,
        DeletionOutcome.DestinationRefuses => CloseText.OutcomeDestinationRefuses,
        DeletionOutcome.Submitted => CloseText.OutcomeSubmitted,
        DeletionOutcome.Queued => CloseText.OutcomeQueued,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "no sentence for this outcome"),
    };
}
