namespace XrplFile.App.Core;

/// <summary>
/// What a person is told about a deletion that did not happen (design 6). The three refusals have
/// nothing in common in what to do next — wait, check the address, or send somewhere else — so they
/// never share a sentence.
/// </summary>
public static class CloseErrorMessages
{
    public static string For(DeletionOutcome outcome) => outcome switch
    {
        DeletionOutcome.Ready =>
            "This account can be closed now.",
        DeletionOutcome.NotYet =>
            "This account cannot be closed yet. The ledger refuses to delete an account until 255 ledgers have closed since its last transaction, and it must hold enough to cover the fee.",
        DeletionOutcome.DestinationMissing =>
            "There is no such account on this network, and the remainder would have nowhere to go. Check the address.",
        DeletionOutcome.DestinationRefuses =>
            "That account will not accept this payment: it either requires a destination tag or only accepts senders it has authorised. Send the remainder somewhere else.",
        DeletionOutcome.Submitted =>
            "The account was closed and its remainder is on its way.",
        DeletionOutcome.Queued =>
            "The node queued the deletion rather than applying it. Until a ledger takes it the account still exists and still holds everything, so check again in a moment.",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "no sentence for this outcome"),
    };
}
