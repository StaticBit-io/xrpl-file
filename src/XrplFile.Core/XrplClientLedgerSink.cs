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
    private readonly Func<BaseRequest, CancellationToken, Task<NodeAnswer>> _request;
    private long _loadWarnings;

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
        : this(async (sent, token) => new NodeAnswer(await request(sent, token).ConfigureAwait(false), null))
    {
    }

    /// <summary>The same seam, for a scripted node that also says what the envelope's <c>warning</c> was.</summary>
    internal XrplClientLedgerSink(Func<BaseRequest, CancellationToken, Task<NodeAnswer>> request)
    {
        _request = request;
    }

    public long LoadWarnings => Interlocked.Read(ref _loadWarnings);

    public async Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("a sink built over a request delegate submits and looks up, it does not read sequences");
        }

        IXrplClient client = _client;
        AccountInfo info = await ClientKeeper.For(client).SendAsync(
            () => client.AccountInfo(
                new AccountInfoRequest(account)
                {
                    LedgerIndex = new LedgerIndex(validated ? LedgerIndexType.Validated : LedgerIndexType.Current),
                },
                cancellationToken).Typed(),
            cancellationToken).ConfigureAwait(false);

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
    /// A node's error answer comes back as <c>error:&lt;code&gt;</c> whichever shape it arrives in, and a
    /// connection that dropped is handled as a timeout is (spec 4.8). A refusal that follows an
    /// unanswered transmission of the same blob says so on
    /// <see cref="SubmitOutcome.FollowsUnansweredAttempt"/>.
    /// </summary>
    public async Task<SubmitOutcome> SubmitAsync(
        Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
    {
        SignedTransaction signed = await signer.SignAsync(transaction, cancellationToken).ConfigureAwait(false);
        string hash = signed.Hash;
        bool unanswered = false;

        for (int attempt = 1; attempt <= MaxSubmitAttempts; attempt++)
        {
            string outcome;

            try
            {
                SubmitRequest request = new() { TxBlob = signed.TxBlob };
                JsonElement result = ObjectResult(request, await AskAsync(request, cancellationToken).ConfigureAwait(false));

                outcome =
                    result.TryGetProperty("engine_result", out JsonElement code) ? code.GetString() ?? string.Empty
                    : result.TryGetProperty("error", out JsonElement error) ? "error:" + error.GetString()
                    : result.GetRawText();
            }
            catch (RippledException e)
            {
                // A real node's error answer: the SDK raises status "error" before any JSON reaches
                // here. The same outcome the embedded shape above gives, so both agree (spec I13).
                outcome = "error:" + e.Classify().RawError;
            }
            catch (Exception e) when (DidNotAnswer(e, cancellationToken))
            {
                // The node did not answer; it may still have the transaction (spec I14).
                unanswered = true;
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

            return new SubmitOutcome(outcome, hash) { FollowsUnansweredAttempt = unanswered && !TookIt(outcome) };
        }

        throw new XrplFileException(XrplFileError.NodeUnreachable,
            $"the node answered neither submit nor tx for transaction {hash} in {MaxSubmitAttempts} attempts");
    }

    public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(txBlob);

        try
        {
            SubmitRequest request = new() { TxBlob = txBlob };
            JsonElement result = ObjectResult(request, await AskAsync(request, cancellationToken).ConfigureAwait(false));

            return result.TryGetProperty("engine_result", out JsonElement code) ? code.GetString() ?? string.Empty
                : result.TryGetProperty("error", out JsonElement error) ? "error:" + error.GetString()
                : result.GetRawText();
        }
        catch (RippledException e)
        {
            return "error:" + e.Classify().RawError;
        }
        catch (Exception e) when (DidNotAnswer(e, cancellationToken))
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable, $"the node did not answer a submit: {e.Message}", inner: e);
        }
    }

    /// <summary>The answers that mean the node took the transaction: it may yet apply, and is not a refusal.</summary>
    private static bool TookIt(string outcome) =>
        outcome.StartsWith("tes", StringComparison.Ordinal)
        || outcome.StartsWith("tec", StringComparison.Ordinal)
        || string.Equals(outcome, Queued, StringComparison.Ordinal);

    /// <summary>
    /// A request the node did not answer (spec I14): a connection closed, lost or never made, a
    /// timeout, a response the SDK could not read, a reconnect that the <see cref="ClientKeeper"/>
    /// gave up on (it reports <see cref="XrplFileError.NodeUnreachable"/>), or a cancellation the
    /// caller's token did not cause — the SDK cancels requests in flight when the network drops. A
    /// node's own error answer is a <see cref="RippledException"/> and is not this.
    /// </summary>
    private static bool DidNotAnswer(Exception e, CancellationToken cancellationToken) =>
        e is XrplException and not RippledException
        || e is XrplFileException { Error: XrplFileError.NodeUnreachable }
        || e is System.TimeoutException
        || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>
    /// What the node says happened to this transaction, or null when it does not say — which a lookup
    /// that times out or loses its connection also means, and so does any error answer to <c>tx</c>
    /// in either shape: <c>txnNotFound</c> above all, but a busy node's <c>tooBusy</c> says nothing
    /// about the transaction either (spec 4.8). A validated transaction reports its final result, so
    /// a <c>tec</c>/<c>tef</c> code is reported as such and never mistaken for acceptance; one still
    /// in the open ledger reports its provisional result, or merely that the node holds it.
    /// </summary>
    private async Task<string?> ResultOfAsync(string hash, CancellationToken cancellationToken)
    {
        JsonElement result;

        try
        {
            TxRequest request = new(hash);
            result = ObjectResult(request, await AskAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (RippledException)
        {
            return null;
        }
        catch (Exception e) when (DidNotAnswer(e, cancellationToken))
        {
            return null;
        }

        if (result.TryGetProperty("error", out _)) return null;

        return TransactionResult(result) ?? Queued;
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

    /// <summary>
    /// The node's <c>result</c>, which must be an object to be probed for <c>engine_result</c> or
    /// <c>error</c>: an array or a scalar would make <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
    /// throw <see cref="InvalidOperationException"/>, which no caller treats as the node's doing.
    /// It is reported as <see cref="XrplFileError.NodeUnreachable"/>, so every caller handles it as
    /// it does any other answer the node failed to give (<see cref="DidNotAnswer"/>).
    /// </summary>
    private static JsonElement ObjectResult(BaseRequest request, NodeAnswer answer)
    {
        if (answer.Result.ValueKind != JsonValueKind.Object)
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable,
                $"the node answered {request.Command} with a result that is {answer.Result.ValueKind}, not an object");
        }

        return answer.Result;
    }

    /// <summary>Sends one request and counts the envelope's load warning before the answer is read.</summary>
    private async Task<NodeAnswer> AskAsync(BaseRequest request, CancellationToken cancellationToken)
    {
        NodeAnswer answer = await _request(request, cancellationToken).ConfigureAwait(false);

        if (string.Equals(answer.Warning, "load", StringComparison.Ordinal)) Interlocked.Increment(ref _loadWarnings);

        return answer;
    }

    private async Task<NodeAnswer> SendAsync(BaseRequest request, CancellationToken cancellationToken)
    {
        XrplResponse<object> response = await ClientKeeper.For(_client!)
            .SendAsync(() => Send(request, cancellationToken), cancellationToken).ConfigureAwait(false);

        return new NodeAnswer(response.Raw.ToJsonElement(), response.Warning);
    }

    private Task<XrplResponse<object>> Send(BaseRequest request, CancellationToken cancellationToken) =>
        request switch
        {
            SubmitRequest submit => _client!.GRequest<object, SubmitRequest>(submit, cancellationToken),
            TxRequest tx => _client!.GRequest<object, TxRequest>(tx, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Command, "this sink sends submit and tx, nothing else"),
        };
}
