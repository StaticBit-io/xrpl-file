namespace XrplFile;

/// <summary>What the node said to one submission, and the hash it is known by.</summary>
public sealed record SubmitOutcome(string EngineResult, string TxHash)
{
    /// <summary>
    /// True when this answer is a refusal of a later transmission of the same blob, after an earlier
    /// transmission went unanswered (spec 4.8); never on an answer that took the transaction. The
    /// refusal is about the later copy only; the earlier one may still be in the node and apply.
    /// </summary>
    public bool FollowsUnansweredAttempt { get; init; }
}

/// <summary>The two things a writer needs from a node: the next sequence and a way to submit a signed transaction.</summary>
public interface ILedgerSink
{
    /// <summary>The account's next sequence, from the open ledger or from the last validated one.</summary>
    Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken);

    /// <summary>Signs the transaction through the signer, submits the blob, and returns what the node said.</summary>
    Task<SubmitOutcome> SubmitAsync(
        Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken);

    /// <summary>
    /// One transmission of a blob already signed (spec B24): the node's <c>engine_result</c>, or
    /// <c>error:&lt;code&gt;</c> for an error answer. No retry and no lookup -- the caller owns both.
    /// A transmission the node did not answer throws <see cref="XrplFileException"/> with
    /// <see cref="XrplFileError.NodeUnreachable"/>, and the next call on this sink reconnects.
    /// </summary>
    Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} does not submit signed blobs");

    /// <summary>
    /// How many of the node's answers so far carried <c>"warning": "load"</c> — the client is nearing
    /// the node's rate limit (spec 4.8). Only grows. Zero for an implementation that does not talk to
    /// a rate-limited node; a decorator that wraps one should forward it.
    /// </summary>
    long LoadWarnings => 0;
}
