using System.Text.Json;
using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Models.Common;
using Xrpl.Models.Methods;
// Xrpl.Models.Ledger also declares a LedgerTransaction; only the enum is wanted from it.
using LedgerIndexType = Xrpl.Models.Ledger.LedgerIndexType;

namespace XrplFile;

public sealed class XrplClientLedgerSink : ILedgerSink
{
    private const int MaxSubmitAttempts = 3;

    /// <summary>What the node says about a transaction it holds but has not judged yet.</summary>
    private const string Queued = "terQUEUED";

    private readonly IXrplClient? _client;
    private readonly Func<BaseRequest, CancellationToken, Task<JsonElement>> _request;

    public XrplClientLedgerSink(IXrplClient client)
    {
        _client = client;
        _request = SendAsync;
    }

    /// <summary>
    /// The seam this class's own tests drive. Everything <c>submit</c> and <c>tx</c> do goes
    /// through one delegate that answers raw JSON, so a scripted node can stand in for a whole
    /// <see cref="IXrplClient"/>. <see cref="SequenceAsync"/> stays on the SDK's typed
    /// <c>account_info</c> path and is not available on a sink built this way.
    /// </summary>
    internal XrplClientLedgerSink(Func<BaseRequest, CancellationToken, Task<JsonElement>> request)
    {
        _request = request;
    }

    public async Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("a sink built over a request delegate submits and looks up, it does not read sequences");
        }

        AccountInfo info = await _client.AccountInfo(
            new AccountInfoRequest(account)
            {
                LedgerIndex = new LedgerIndex(validated ? LedgerIndexType.Validated : LedgerIndexType.Current),
            },
            cancellationToken).Typed().ConfigureAwait(false);

        return info.AccountData.Sequence ?? 0;
    }

    /// <summary>
    /// Signs once and submits up to three times. A closing ledger holding hundreds of one
    /// account's queued transactions can make the node take longer than the SDK's request
    /// timeout to answer <c>submit</c> — that alone must not fail the publication, because the
    /// transaction may have gone through anyway. On a timeout the hash is looked up and the
    /// node's own result for it is returned; a lookup that answers <c>txnNotFound</c>, or that
    /// times out in its turn, means "not known yet" and the identical blob is resubmitted.
    /// Resubmission is safe because it is idempotent by <c>Sequence</c>, or by
    /// <c>TicketSequence</c> for a ticketed transaction — a copy that did land answers
    /// <c>tefPAST_SEQ</c>/<c>tefALREADY</c>, or <c>tefNO_TICKET</c> when it rode a ticket, and each
    /// is resolved by the same lookup of its own hash rather than reported as a refusal. When all
    /// three attempts pass without the node ever answering, the node is unreachable and the caller
    /// is told so.
    /// </summary>
    public async Task<SubmitOutcome> SubmitAsync(
        Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
    {
        SignedTransaction signed = await signer.SignAsync(transaction, cancellationToken).ConfigureAwait(false);
        string hash = signed.Hash;

        for (int attempt = 1; attempt <= MaxSubmitAttempts; attempt++)
        {
            string outcome;

            try
            {
                JsonElement answer = await _request(new SubmitRequest { TxBlob = signed.TxBlob }, cancellationToken).ConfigureAwait(false);

                outcome =
                    answer.TryGetProperty("engine_result", out JsonElement code) ? code.GetString() ?? string.Empty
                    : answer.TryGetProperty("error", out JsonElement error) ? "error:" + error.GetString()
                    : answer.GetRawText();
            }
            catch (Xrpl.Client.Exceptions.TimeoutException)
            {
                // The node did not answer in time; it may still have the transaction.
                string? landed = await ResultOfAsync(hash, cancellationToken).ConfigureAwait(false);
                if (landed is not null) return new SubmitOutcome(landed, hash);

                continue;
            }

            // The sequence is spent: an earlier copy of this very blob landed. What matters is not
            // the refusal but what that copy did, so the node is asked about the hash. A ticketed
            // copy that already landed is answered tefNO_TICKET rather than tefALREADY — the ticket
            // is checked first — so that answer is asked about too; a ticket somebody else consumed
            // leaves the hash unknown and the answer stands.
            if (outcome is "tefPAST_SEQ" or "tefALREADY" or "tefNO_TICKET")
            {
                string? landed = await ResultOfAsync(hash, cancellationToken).ConfigureAwait(false);
                if (landed is not null) return new SubmitOutcome(landed, hash);
            }

            return new SubmitOutcome(outcome, hash);
        }

        throw new XrplFileException(XrplFileError.NodeUnreachable,
            $"the node answered neither submit nor tx for transaction {hash} in {MaxSubmitAttempts} attempts");
    }

    /// <summary>
    /// What the node says happened to this transaction, or null when it does not have it — which
    /// a lookup that times out also means, since a node that did not answer said nothing.
    /// A validated transaction reports its final result, so a <c>tec</c>/<c>tef</c> code is
    /// reported as such and never mistaken for acceptance; one still in the open ledger reports
    /// its provisional result, or merely that the node holds it.
    /// </summary>
    private async Task<string?> ResultOfAsync(string hash, CancellationToken cancellationToken)
    {
        JsonElement answer;

        try
        {
            answer = await _request(new TxRequest(hash), cancellationToken).ConfigureAwait(false);
        }
        catch (Xrpl.Client.Exceptions.TimeoutException)
        {
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException
            && string.Equals(e.Classify().RawError, XrplErrorCodes.TxnNotFound, StringComparison.Ordinal))
        {
            return null;
        }

        if (answer.TryGetProperty("error", out JsonElement error)
            && string.Equals(error.GetString(), XrplErrorCodes.TxnNotFound, StringComparison.Ordinal))
        {
            return null;
        }

        return TransactionResult(answer) ?? Queued;
    }

    /// <summary>The <c>meta.TransactionResult</c> of a <c>tx</c> answer, in either api_version's shape.</summary>
    private static string? TransactionResult(JsonElement answer)
    {
        JsonElement meta =
            answer.TryGetProperty("meta", out JsonElement inline) ? inline
            : answer.TryGetProperty("metaData", out JsonElement stored) ? stored
            : default;

        return meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("TransactionResult", out JsonElement result)
            && result.ValueKind == JsonValueKind.String
                ? result.GetString()
                : null;
    }

    private async Task<JsonElement> SendAsync(BaseRequest request, CancellationToken cancellationToken)
    {
        Task<XrplResponse<object>> pending = request switch
        {
            SubmitRequest submit => _client!.GRequest<object, SubmitRequest>(submit, cancellationToken),
            TxRequest tx => _client!.GRequest<object, TxRequest>(tx, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Command, "this sink sends submit and tx, nothing else"),
        };

        XrplResponse<object> response = await pending.ConfigureAwait(false);

        return response.Raw.ToJsonElement();
    }
}
