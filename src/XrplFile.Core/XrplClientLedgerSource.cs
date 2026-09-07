using System.Runtime.CompilerServices;
using System.Text.Json;
using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Models.Common;
using Xrpl.Models.Methods;
using LedgerIndexType = Xrpl.Models.Ledger.LedgerIndexType;

namespace XrplFile;

/// <summary>
/// ILedgerSource over the SDK's client. Answers are read as raw JSON rather than through the
/// SDK's typed models, so both api_version shapes parse and a field the models lack is not lost.
/// Every failure leaves here as an <see cref="XrplFileException"/> or a cancellation: a dropped
/// connection, a request the node never answered and a node error other than <c>txnNotFound</c>
/// (for a tx lookup) or <c>actNotFound</c> (for anything asked about an account) are all
/// <see cref="XrplFileError.NodeUnreachable"/>, carrying what the node said. The browser mirror
/// raises the same error in the same places.
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
        // A transaction the node does not have is the one node error that is an answer rather than
        // a failure; anything else it says about a tx request is a failure to read.
        JsonElement? answer = await AskOrAbsentAsync(
            new TxRequest(hash), "tx", XrplErrorCodes.TxnNotFound, cancellationToken).ConfigureAwait(false);

        return answer is { } found ? ParseRecord(found) : null;
    }

    public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
        string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        object? marker = null;

        while (true)
        {
            // An account the node has never heard of has no transactions, which is an answer and
            // not a failure to read -- the same rule the balance lookup below follows. It is the
            // state a throwaway account is in until somebody funds it, and it is precisely then
            // that a caller asks who paid.
            JsonElement? page = await AskOrAbsentAsync(
                new AccountTransactionsRequest(account)
                {
                    LedgerIndexMin = (int)fromLedger,
                    LedgerIndexMax = (int)toLedger,
                    Limit = _pageSize,
                    Forward = forward,
                    Marker = marker,
                },
                "account_tx",
                XrplErrorCodes.ActNotFound,
                cancellationToken).ConfigureAwait(false);

            if (page is not { } answer) yield break;

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

    public async Task<NetworkFees> FeesAsync(CancellationToken cancellationToken)
    {
        JsonElement answer = await AskAsync(new ServerStateRequest(), "server_state", cancellationToken).ConfigureAwait(false);

        if (!answer.TryGetProperty("state", out JsonElement state)
            || !state.TryGetProperty("validated_ledger", out JsonElement ledger))
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable, "the node answered server_state without a validated ledger");
        }

        long baseFee = Number(ledger, "base_fee") ?? throw new XrplFileException(XrplFileError.NodeUnreachable, "the node did not say what it charges");
        long baseReserve = Number(ledger, "reserve_base") ?? throw new XrplFileException(XrplFileError.NodeUnreachable, "the node did not say its base reserve");
        long ownerReserve = Number(ledger, "reserve_inc") ?? throw new XrplFileException(XrplFileError.NodeUnreachable, "the node did not say its owner reserve");

        // A busy node multiplies its base fee by load_factor over load_base. Ignoring that is how
        // an estimate comes out right on a quiet network and short on a busy one.
        long loadFactor = Number(answer, "load_factor") ?? Number(state, "load_factor") ?? 256;
        long loadBase = Number(answer, "load_base") ?? Number(state, "load_base") ?? 256;
        if (loadBase <= 0) loadBase = 256;

        // Integer division, matching rippled's own scaling: it truncates, not rounds up.
        long fee = baseFee * loadFactor / loadBase;

        return new NetworkFees(Math.Max(fee, baseFee), baseReserve, ownerReserve);
    }

    public async Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) =>
        (await AccountStateAsync(account, cancellationToken).ConfigureAwait(false)).BalanceDrops;

    public async Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken)
    {
        // An account nobody funded is not a failure to answer: it is an answer, and it is empty.
        // Anything else the node says about an account_info request is a failure to read.
        JsonElement? answer = await AskOrAbsentAsync(
            new AccountInfoRequest(account), "account_info", XrplErrorCodes.ActNotFound, cancellationToken).ConfigureAwait(false);

        if (answer is not { } found || !found.TryGetProperty("account_data", out JsonElement data)) return AccountState.Absent;

        long drops = Text(data, "Balance") is { } balance && long.TryParse(balance, out long parsed) ? parsed : 0;

        // An account owning nothing may omit OwnerCount entirely; absent and zero mean the same.
        return new AccountState(drops, (int)(Number(data, "OwnerCount") ?? 0));
    }

    public async Task<long> CurrentLedgerAsync(CancellationToken cancellationToken)
    {
        JsonElement answer = await AskAsync(
            new LedgerRequest { LedgerIndex = new LedgerIndex(LedgerIndexType.Validated) }, "ledger", cancellationToken).ConfigureAwait(false);

        // The index sits at the top of the answer, and inside the ledger object as a string.
        if (Number(answer, "ledger_index") is { } top) return top;

        if (answer.TryGetProperty("ledger", out JsonElement ledger) && Text(ledger, "ledger_index") is { } text
            && long.TryParse(text, out long parsed))
        {
            return parsed;
        }

        throw new XrplFileException(XrplFileError.NodeUnreachable, "the node answered ledger without an index");
    }

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

        string? outcome = record.TryGetProperty("meta", out JsonElement meta) ? Text(meta, "TransactionResult") : null;
        bool validated = record.TryGetProperty("validated", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;

        return new LedgerTransaction(
            Hash: (Text(record, "hash") ?? Text(tx, "hash") ?? string.Empty).ToUpperInvariant(),
            LedgerIndex: Number(record, "ledger_index") ?? Number(tx, "ledger_index") ?? 0,
            Account: Text(tx, "Account") ?? string.Empty,
            Succeeded: string.Equals(outcome, "tesSUCCESS", StringComparison.Ordinal),
            Validated: validated,
            Result: outcome,
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
    /// One request where a single node error code is itself an answer -- "nothing here" -- rather
    /// than a failure to read, in whichever of the two shapes it can arrive: thrown by
    /// <see cref="RequestManager"/> before this class ever sees a JSON answer, or embedded as the
    /// <c>error</c> member of one it does see. Both branches must agree on <paramref name="absentCode"/>
    /// or a caller polling for "does this exist yet" (a fresh account, a not-yet-validated tx) can
    /// see the live-node shape turn into NodeUnreachable instead of the null/zero the test-only
    /// shape returns -- which is exactly the regression this helper exists to make impossible to
    /// reintroduce by editing only one of the two.
    /// </summary>
    private async Task<JsonElement?> AskOrAbsentAsync(BaseRequest request, string command, string absentCode, CancellationToken cancellationToken)
    {
        JsonElement answer;

        try
        {
            answer = await _request(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException
            && string.Equals(e.Classify().RawError, absentCode, StringComparison.Ordinal))
        {
            return null;
        }
        catch (Exception e) when (IsNodeFailure(e))
        {
            throw Unreachable(command, e);
        }

        if (answer.TryGetProperty("error", out JsonElement error))
        {
            return string.Equals(error.GetString(), absentCode, StringComparison.Ordinal)
                ? null
                : throw Answered(command, error);
        }

        return answer;
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
            ServerStateRequest state => _client!.GRequest<object, ServerStateRequest>(state, cancellationToken),
            AccountInfoRequest accountInfo => _client!.GRequest<object, AccountInfoRequest>(accountInfo, cancellationToken),
            LedgerRequest ledger => _client!.GRequest<object, LedgerRequest>(ledger, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Command, "this source sends tx, account_tx, server_info, server_state, account_info and ledger, nothing else"),
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
