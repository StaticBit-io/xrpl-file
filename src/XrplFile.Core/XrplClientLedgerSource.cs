using System.Runtime.CompilerServices;
using System.Text.Json;
using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Models.Methods;

namespace XrplFile;

/// <summary>
/// ILedgerSource over the SDK's client. Answers are read as raw JSON rather than through the
/// SDK's typed models, so both api_version shapes parse and a field the models lack is not lost.
/// Every failure leaves here as an <see cref="XrplFileException"/> or a cancellation: a dropped
/// connection, a request the node never answered and a node error other than <c>txnNotFound</c>
/// are all <see cref="XrplFileError.NodeUnreachable"/>, carrying what the node said. The browser
/// mirror raises the same error in the same three places.
/// </summary>
public sealed class XrplClientLedgerSource : ILedgerSource
{
    private readonly IXrplClient? _client;
    private readonly int _pageSize;
    private readonly Func<BaseRequest, CancellationToken, Task<JsonElement>> _request;

    public XrplClientLedgerSource(IXrplClient client, int pageSize = 400)
    {
        _client = client;
        _pageSize = pageSize;
        _request = SendAsync;
    }

    /// <summary>
    /// The seam this class's own tests drive, the same one <see cref="XrplClientLedgerSink"/>
    /// has: every request goes through one delegate answering raw JSON, so a scripted node —
    /// including one that fails the way a dropped connection does — can stand in for a whole
    /// <see cref="IXrplClient"/>.
    /// </summary>
    internal XrplClientLedgerSource(Func<BaseRequest, CancellationToken, Task<JsonElement>> request, int pageSize = 400)
    {
        _pageSize = pageSize;
        _request = request;
    }

    public async Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken)
    {
        JsonElement answer;

        try
        {
            answer = await _request(new TxRequest(hash), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException
            && string.Equals(e.Classify().RawError, XrplErrorCodes.TxnNotFound, StringComparison.Ordinal))
        {
            return null;
        }
        catch (Exception e) when (IsNodeFailure(e))
        {
            throw Unreachable("tx", e);
        }

        if (answer.TryGetProperty("error", out JsonElement error))
        {
            // A transaction the node does not have is the one node error that is an answer rather
            // than a failure; anything else it says about a tx request is a failure to read.
            return string.Equals(error.GetString(), XrplErrorCodes.TxnNotFound, StringComparison.Ordinal)
                ? null
                : throw Answered("tx", error);
        }

        return ParseRecord(answer);
    }

    public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
        string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        object? marker = null;

        while (true)
        {
            JsonElement answer = await AskAsync(
                new AccountTransactionsRequest(account)
                {
                    LedgerIndexMin = (int)fromLedger,
                    LedgerIndexMax = (int)toLedger,
                    Limit = _pageSize,
                    Forward = forward,
                    Marker = marker,
                },
                "account_tx",
                cancellationToken).ConfigureAwait(false);

            foreach (LedgerTransaction tx in ParseTransactions(answer))
            {
                yield return tx;
            }

            // The marker goes back to the node as it came; its shape is the node's business.
            marker = answer.TryGetProperty("marker", out JsonElement next) ? next.Clone() : null;

            if (marker is null) yield break;
        }
    }

    public async Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) =>
        EarliestAvailableLedger(await AskAsync(new ServerInfoRequest(), "server_info", cancellationToken).ConfigureAwait(false));

    public static IReadOnlyList<LedgerTransaction> ParseTransactions(JsonElement answer)
    {
        if (!answer.TryGetProperty("transactions", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return [];

        List<LedgerTransaction> parsed = [];
        foreach (JsonElement record in list.EnumerateArray())
        {
            parsed.Add(ParseRecord(record));
        }

        return parsed;
    }

    /// <summary>One transaction as a node describes it: an account_tx record, or a tx answer, which has the same top level.</summary>
    public static LedgerTransaction ParseRecord(JsonElement record)
    {
        JsonElement tx =
            record.TryGetProperty("tx_json", out JsonElement json) ? json
            : record.TryGetProperty("tx", out JsonElement legacy) ? legacy
            : record;

        string? memoType = null, memoData = null, memoFormat = null;
        if (tx.TryGetProperty("Memos", out JsonElement memos) && memos.ValueKind == JsonValueKind.Array && memos.GetArrayLength() > 0
            && memos[0].TryGetProperty("Memo", out JsonElement memo))
        {
            memoType = Text(memo, "MemoType");
            memoData = Text(memo, "MemoData");
            memoFormat = Text(memo, "MemoFormat");
        }

        string outcome = record.TryGetProperty("meta", out JsonElement meta) ? Text(meta, "TransactionResult") ?? string.Empty : string.Empty;
        bool validated = record.TryGetProperty("validated", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;

        return new LedgerTransaction(
            Hash: (Text(record, "hash") ?? Text(tx, "hash") ?? string.Empty).ToUpperInvariant(),
            LedgerIndex: Number(record, "ledger_index") ?? Number(tx, "ledger_index") ?? 0,
            Account: Text(tx, "Account") ?? string.Empty,
            Succeeded: string.Equals(outcome, "tesSUCCESS", StringComparison.Ordinal),
            Validated: validated,
            MemoTypeHex: memoType,
            MemoDataHex: memoData,
            MemoFormatHex: memoFormat);
    }

    /// <summary>
    /// The low bound of the first range in complete_ledgers: "32570-106738922", or after a gap
    /// "32570-100,5000-106738922". A node with nothing answers "empty", which parses to null —
    /// zero would read as "has everything".
    /// </summary>
    public static long? EarliestAvailableLedger(JsonElement answer)
    {
        if (!answer.TryGetProperty("info", out JsonElement info)) return null;

        string? ranges = Text(info, "complete_ledgers");
        if (string.IsNullOrWhiteSpace(ranges)) return null;

        string firstRange = ranges.Split(',')[0];
        int dash = firstRange.IndexOf('-');
        string lowest = dash > 0 ? firstRange.Substring(0, dash) : firstRange;

        return long.TryParse(lowest.Trim(), out long result) ? result : null;
    }

    /// <summary>One request, with every way it can fail turned into NodeUnreachable.</summary>
    private async Task<JsonElement> AskAsync(BaseRequest request, string command, CancellationToken cancellationToken)
    {
        JsonElement answer;

        try
        {
            answer = await _request(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsNodeFailure(e))
        {
            throw Unreachable(command, e);
        }

        return answer.TryGetProperty("error", out JsonElement error) ? throw Answered(command, error) : answer;
    }

    /// <summary>
    /// Everything the SDK raises when a request does not come back with an answer this library
    /// can use: a connection that was never made or was lost, a request that timed out, and the
    /// node's own error responses (<see cref="RippledException"/>). A cancellation is none of
    /// these and passes through, as does an <see cref="XrplFileException"/> from further in.
    /// </summary>
    private static bool IsNodeFailure(Exception e) =>
        e is not OperationCanceledException and not XrplFileException
        && e is XrplException or System.TimeoutException;

    private static XrplFileException Unreachable(string command, Exception e) =>
        new(XrplFileError.NodeUnreachable, $"the node did not answer {command}: {e.Message}", inner: e);

    private static XrplFileException Answered(string command, JsonElement error) =>
        new(XrplFileError.NodeUnreachable, $"the node answered {error.GetString() ?? error.GetRawText()} to {command}");

    private async Task<JsonElement> SendAsync(BaseRequest request, CancellationToken cancellationToken)
    {
        Task<XrplResponse<object>> pending = request switch
        {
            TxRequest tx => _client!.GRequest<object, TxRequest>(tx, cancellationToken),
            AccountTransactionsRequest transactions => _client!.GRequest<object, AccountTransactionsRequest>(transactions, cancellationToken),
            ServerInfoRequest info => _client!.GRequest<object, ServerInfoRequest>(info, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Command, "this source sends tx, account_tx and server_info, nothing else"),
        };

        XrplResponse<object> response = await pending.ConfigureAwait(false);

        return response.Raw.ToJsonElement();
    }

    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
            ? result
            : null;
}
