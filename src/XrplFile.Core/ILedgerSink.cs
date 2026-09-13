namespace XrplFile;

public sealed record SubmitOutcome(string EngineResult, string TxHash);

/// <summary>The two things a writer needs from a node: the next sequence and a way to submit a signed transaction.</summary>
public interface ILedgerSink
{
    /// <summary>The account's next sequence, from the open ledger or from the last validated one.</summary>
    Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken);

    /// <summary>Signs the transaction through the signer, submits the blob, and returns what the node said.</summary>
    Task<SubmitOutcome> SubmitAsync(
        Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken);
}
