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
/// </summary>
public sealed class XrplClientLedgerSource(IXrplClient client, int pageSize = 400) : ILedgerSource
{
    private readonly IXrplClient _client = client;
    private readonly int _pageSize = pageSize;

    public async Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken)
    {
        JsonElement answer;

        try
        {
            XrplResponse<object> response = await _client.GRequest<object, TxRequest>(new TxRequest(hash), cancellationToken).ConfigureAwait(false);
            answer = response.Raw.ToJsonElement();
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

        return ParseRecord(answer);
    }

    public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
        string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        object? marker = null;

        while (true)
        {
            XrplResponse<object> response = await _client.GRequest<object, AccountTransactionsRequest>(
                new AccountTransactionsRequest(account)
                {
                    LedgerIndexMin = (int)fromLedger,
                    LedgerIndexMax = (int)toLedger,
                    Limit = _pageSize,
                    Forward = forward,
                    Marker = marker,
                },
                cancellationToken).ConfigureAwait(false);

            JsonElement answer = response.Raw.ToJsonElement();

            foreach (LedgerTransaction tx in ParseTransactions(answer))
            {
                yield return tx;
            }

            // The marker goes back to the node as it came; its shape is the node's business.
            marker = answer.TryGetProperty("marker", out JsonElement next) ? next.Clone() : null;

            if (marker is null) yield break;
        }
    }

    public async Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken)
    {
        XrplResponse<object> response = await _client.GRequest<object, ServerInfoRequest>(new ServerInfoRequest(), cancellationToken).ConfigureAwait(false);
        return EarliestAvailableLedger(response.Raw.ToJsonElement());
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

    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
            ? result
            : null;
}
