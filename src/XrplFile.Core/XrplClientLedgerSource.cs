using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Client.Json;
using Xrpl.Models.Common;
using Xrpl.Models.Ledger;
using Xrpl.Models.Methods;
using LedgerIndexType = Xrpl.Models.Ledger.LedgerIndexType;

namespace XrplFile;

/// <summary>
/// ILedgerSource over the SDK's client. Answers are read as raw JSON rather than through the
/// SDK's typed models, so both api_version shapes parse and a field the models lack is not lost.
/// Every failure leaves here as an <see cref="XrplFileException"/> or a cancellation: a dropped
/// connection, a request the node never answered and a node error other than <c>txnNotFound</c>
/// (for a tx lookup) or <c>actNotFound</c> (for anything asked about an account), and a
/// cancellation the caller did not ask for, are all
/// <see cref="XrplFileError.NodeUnreachable"/>, carrying what the node said. The browser mirror
/// raises the same error in the same places.
/// </summary>
public sealed class XrplClientLedgerSource : ILedgerSource
{
    private readonly IXrplClient? _client;
    private readonly int _pageSize;
    private readonly Func<BaseRequest, CancellationToken, Task<NodeAnswer>> _request;
    private long _loadWarnings;

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
        : this(async (sent, token) => new NodeAnswer(await request(sent, token).ConfigureAwait(false), null), pageSize)
    {
    }

    /// <summary>The same seam, for a scripted node that also says what the envelope's <c>warning</c> was.</summary>
    internal XrplClientLedgerSource(Func<BaseRequest, CancellationToken, Task<NodeAnswer>> request, int pageSize = 400)
    {
        _pageSize = pageSize;
        _request = request;
    }

    public long LoadWarnings => Interlocked.Read(ref _loadWarnings);

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
            NodeAnswer? page = await AskPageOrAbsentAsync(
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

            IReadOnlyList<LedgerTransaction> transactions;

            // The page's buffers go back to the pool before its records are handed out: the
            // records are plain strings by now, and the consumer may take as long as it likes.
            using (answer)
            {
                transactions = ParseTransactions(answer.Result);

                // The marker goes back to the node as it came; its shape is the node's business.
                // It is cloned because the page that holds it is released here.
                marker = answer.Result.TryGetProperty("marker", out JsonElement next) ? next.Clone() : null;
            }

            foreach (LedgerTransaction tx in transactions)
            {
                yield return tx;
            }

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

        // An account owning nothing may omit OwnerCount entirely, and one holding no tickets omits
        // TicketCount the same way; absent and zero mean the same for both.
        return new AccountState(
            drops, (int)(Number(data, "OwnerCount") ?? 0), (int)(Number(data, "TicketCount") ?? 0));
    }

    public async Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken)
    {
        List<LiveTicket> tickets = [];
        object? marker = null;
        string? previousMarker = null;

        // Validated, not current: a ticket a strategy picks up or a tidy-up spends has to be one
        // the ledger has committed to, and a ticket an open ledger shows may yet vanish.
        LedgerIndex ledger = new(LedgerIndexType.Validated);

        while (true)
        {
            JsonElement? page = await AskOrAbsentAsync(
                new AccountObjectsRequest(account)
                {
                    Type = Xrpl.Models.LedgerEntryType.Ticket,
                    LedgerIndex = ledger,
                    Limit = _pageSize,
                    Marker = marker,
                },
                "account_objects",
                XrplErrorCodes.ActNotFound,
                cancellationToken).ConfigureAwait(false);

            if (page is not { } answer) return tickets;

            tickets.AddRange(ParseTickets(answer));

            // Every later page comes from the ledger the first one was read from: "validated" moves
            // between pages, and a marker is only meaningful in the ledger that issued it, so pages
            // from two ledgers could skip a ticket or list one twice. An answer that does not say
            // which ledger it read leaves nothing to pin, and asking "validated" again is the best
            // this listing can do.
            if (ledger.Index is null && Number(answer, "ledger_index") is { } read and > 0 and <= uint.MaxValue)
            {
                ledger = new LedgerIndex((uint)read);
            }

            // The marker goes back to the node as it came; its shape is the node's business. One
            // present but null is the same as none.
            if (!answer.TryGetProperty("marker", out JsonElement next) || next.ValueKind == JsonValueKind.Null) return tickets;

            string raw = next.GetRawText();

            // A node that hands back the marker it was just given would page forever; a listing it
            // cannot finish is a node that cannot be read, not an empty one.
            if (string.Equals(raw, previousMarker, StringComparison.Ordinal))
            {
                throw new XrplFileException(
                    XrplFileError.NodeUnreachable,
                    "the node answered account_objects with the same marker twice; the ticket listing cannot be completed");
            }

            previousMarker = raw;
            marker = next.Clone();
        }
    }

    /// <summary>The Ticket entries of one account_objects page. Anything else, or a ticket missing a field this needs or carrying a sequence no ticket can have, is skipped.</summary>
    internal static IReadOnlyList<LiveTicket> ParseTickets(JsonElement answer)
    {
        if (!answer.TryGetProperty("account_objects", out JsonElement objects) || objects.ValueKind != JsonValueKind.Array) return [];

        List<LiveTicket> parsed = [];

        foreach (JsonElement entry in objects.EnumerateArray())
        {
            if (!string.Equals(Text(entry, "LedgerEntryType"), "Ticket", StringComparison.Ordinal)) continue;
            if (Number(entry, "TicketSequence") is not { } sequence || Text(entry, "PreviousTxnID") is not { } creator) continue;

            // A sequence is a 32-bit unsigned number; an unchecked cast would wrap anything else into
            // a ticket number the account may genuinely hold.
            if (sequence is < 0 or > uint.MaxValue) continue;

            parsed.Add(new LiveTicket((uint)sequence, creator.ToUpperInvariant()));
        }

        return parsed;
    }

    /// <summary>
    /// The node error codes that mean "no such destination" rather than "the node could not be
    /// read": actNotFound for an address the ledger genuinely does not carry, and actMalformed for
    /// one that could never be carried because it does not decode at all. A destination is the one
    /// address in this library a person types rather than reads off a key the application
    /// generated, and a classic address carries a checksum -- a typo almost never lands on a
    /// different real account, it fails to decode, and rippled answers actMalformed rather than
    /// actNotFound for it. Both read the same to a person deciding what to do next ("check the
    /// address"), and conflating either with NodeUnreachable would send them to check the node URL
    /// in Settings instead.
    /// </summary>
    private static readonly string[] DestinationAbsentCodes = [XrplErrorCodes.ActNotFound, XrplErrorCodes.ActMalformed];

    public async Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken)
    {
        JsonElement? answer = await AskOrAbsentAsync(
            new AccountInfoRequest(account), "account_info", DestinationAbsentCodes, cancellationToken).ConfigureAwait(false);

        if (answer is not { } found || !found.TryGetProperty("account_data", out JsonElement data))
        {
            return DestinationState.Absent;
        }

        // The flag bits are the SDK's own AccountRootFlags (Xrpl.Models.Ledger), which names them
        // after rippled's LedgerFormats.h: lsfRequireDestTag is 131072 and lsfDepositAuth is
        // 16777216. Named rather than written as numbers so a reader can check them against the
        // protocol rather than against this file.
        uint flags = (uint)(Number(data, "Flags") ?? 0);

        return new DestinationState(
            Exists: true,
            RequiresDestinationTag: (flags & (uint)AccountRootFlags.lsfRequireDestTag) != 0,
            AuthorisesDepositors: (flags & (uint)AccountRootFlags.lsfDepositAuth) != 0);
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

    public async Task<LedgerSnapshot> SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        long ledger = await CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        if (ledger is <= 0 or > uint.MaxValue)
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable, $"the node answered ledger index {ledger}, which no ledger has");
        }

        Dictionary<string, uint> sequences = new(StringComparer.Ordinal);

        foreach (string account in accounts)
        {
            // Pinned to the index just read (spec B6). An error other than actNotFound -- lgrNotFound
            // above all -- leaves here as NodeUnreachable, and the caller takes the snapshot again.
            JsonElement? answer = await AskOrAbsentAsync(
                new AccountInfoRequest(account) { LedgerIndex = new LedgerIndex((uint)ledger) },
                "account_info", XrplErrorCodes.ActNotFound, cancellationToken).ConfigureAwait(false);

            sequences[account] =
                answer is { } found && found.TryGetProperty("account_data", out JsonElement data) && UInt(data, "Sequence") is { } sequence
                    ? sequence
                    : 0;
        }

        return new LedgerSnapshot(ledger, sequences);
    }

    public async Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken)
    {
        JsonElement answer = await AskAsync(new FeeRequest(), "fee", cancellationToken).ConfigureAwait(false);

        if (!answer.TryGetProperty("drops", out JsonElement drops)
            || Drops(drops, "open_ledger_fee") is not { } open
            || Drops(drops, "base_fee") is not { } baseFee)
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable, "the node answered fee without its open-ledger and base fees");
        }

        return new OpenLedgerFee(open, baseFee);
    }

    public async Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken)
    {
        string id = Amendments.IdOf(name);

        // entryNotFound is an answer: a ledger with no Amendments object has none enabled (spec 4.3).
        JsonElement? answer = await AskOrAbsentAsync(
            new LedgerEntryRequest
            {
                Index = Amendments.SingletonIndex,
                LedgerIndex = new LedgerIndex(LedgerIndexType.Validated),
            },
            "ledger_entry", "entryNotFound", cancellationToken).ConfigureAwait(false);

        if (answer is not { } found
            || !found.TryGetProperty("node", out JsonElement node)
            || !node.TryGetProperty("Amendments", out JsonElement list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && string.Equals(entry.GetString(), id, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>A drops amount, sent as a string by rippled and as a number by some proxies.</summary>
    private static long? Drops(JsonElement json, string name) =>
        Text(json, name) is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ? parsed
        : Number(json, name);

    private static uint? UInt(JsonElement json, string name) =>
        Number(json, name) is { } value and >= 0 and <= uint.MaxValue ? (uint)value : null;

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

        JsonElement meta = record.TryGetProperty("meta", out JsonElement found) ? found : default;
        string? outcome = Text(meta, "TransactionResult");
        bool validated = record.TryGetProperty("validated", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;

        // An inner carries ParentBatchID in its metadata (measured on the stand); the transaction is
        // read as a fallback for a node that puts it there instead.
        string? parentBatch = Text(meta, "ParentBatchID") ?? Text(tx, "ParentBatchID");

        return new LedgerTransaction(
            Hash: (Text(record, "hash") ?? Text(tx, "hash") ?? string.Empty).ToUpperInvariant(),
            LedgerIndex: Number(record, "ledger_index") ?? Number(tx, "ledger_index") ?? 0,
            Account: Text(tx, "Account") ?? string.Empty,
            Succeeded: string.Equals(outcome, "tesSUCCESS", StringComparison.Ordinal),
            Validated: validated,
            Result: outcome,
            MemoTypeHex: memoType,
            MemoDataHex: memoData,
            MemoFormatHex: memoFormat)
        {
            TransactionType = Text(tx, "TransactionType"),
            Sequence = UInt(tx, "Sequence") ?? 0,
            TicketSequence = UInt(tx, "TicketSequence"),
            TicketCount = UInt(tx, "TicketCount"),
            Fee = Drops(tx, "Fee"),
            ParentBatchID = parentBatch?.ToUpperInvariant(),
            TransactionIndex = UInt(meta, "TransactionIndex") is { } position ? (int)position : null,
        };
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
        NodeAnswer raw;

        try
        {
            raw = await _request(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsNodeFailure(e, cancellationToken))
        {
            throw Unreachable(command, e);
        }

        using (raw)
        {
            if (string.Equals(raw.Warning, "load", StringComparison.Ordinal)) Interlocked.Increment(ref _loadWarnings);

            return raw.Result.TryGetProperty("error", out JsonElement error) ? throw Answered(command, error) : Detach(raw);
        }
    }

    /// <summary>The answer's element, copied out when a pooled document backs it, so it outlives the answer.</summary>
    private static JsonElement Detach(NodeAnswer answer) => answer.Owner is null ? answer.Result : answer.Result.Clone();

    /// <summary>
    /// One request where a single node error code is itself an answer -- "nothing here" -- rather
    /// than a failure to read. See the <see cref="IReadOnlyList{T}"/> overload for the two shapes
    /// that code can arrive in and why both branches have to agree on it.
    /// </summary>
    private Task<JsonElement?> AskOrAbsentAsync(BaseRequest request, string command, string absentCode, CancellationToken cancellationToken) =>
        AskOrAbsentAsync(request, command, [absentCode], cancellationToken);

    /// <summary>
    /// One request where any of several node error codes is itself an answer -- "nothing here" --
    /// rather than a failure to read, in whichever of the two shapes it can arrive: thrown by
    /// <see cref="RequestManager"/> before this class ever sees a JSON answer, or embedded as the
    /// <c>error</c> member of one it does see. Both branches must agree on <paramref name="absentCodes"/>
    /// or a caller polling for "does this exist yet" (a fresh account, a not-yet-validated tx) can
    /// see the live-node shape turn into NodeUnreachable instead of the null/zero the test-only
    /// shape returns -- which is exactly the regression this helper exists to make impossible to
    /// reintroduce by editing only one of the two.
    /// </summary>
    private async Task<JsonElement?> AskOrAbsentAsync(
        BaseRequest request, string command, IReadOnlyList<string> absentCodes, CancellationToken cancellationToken)
    {
        NodeAnswer? page = await AskPageOrAbsentAsync(request, command, absentCodes, cancellationToken).ConfigureAwait(false);

        if (page is not { } answer) return null;

        using (answer)
        {
            return Detach(answer);
        }
    }

    /// <summary>
    /// <see cref="AskOrAbsentAsync(BaseRequest, string, string, CancellationToken)"/> for a caller
    /// that reads a page-sized answer in place: the answer is returned still backed by its pooled
    /// buffers, with the same absent and failure rules, and the caller disposes it.
    /// </summary>
    private Task<NodeAnswer?> AskPageOrAbsentAsync(BaseRequest request, string command, string absentCode, CancellationToken cancellationToken) =>
        AskPageOrAbsentAsync(request, command, [absentCode], cancellationToken);

    private async Task<NodeAnswer?> AskPageOrAbsentAsync(
        BaseRequest request, string command, IReadOnlyList<string> absentCodes, CancellationToken cancellationToken)
    {
        NodeAnswer raw;

        try
        {
            raw = await _request(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && IsAbsent(e.Classify().RawError, absentCodes))
        {
            return null;
        }
        catch (Exception e) when (IsNodeFailure(e, cancellationToken))
        {
            throw Unreachable(command, e);
        }

        if (string.Equals(raw.Warning, "load", StringComparison.Ordinal)) Interlocked.Increment(ref _loadWarnings);

        if (raw.Result.TryGetProperty("error", out JsonElement error))
        {
            using (raw)
            {
                return IsAbsent(error.GetString(), absentCodes)
                    ? null
                    : throw Answered(command, error);
            }
        }

        return raw;
    }

    private static bool IsAbsent(string? code, IReadOnlyList<string> absentCodes)
    {
        if (code is null) return false;

        foreach (string absentCode in absentCodes)
        {
            if (string.Equals(code, absentCode, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// Everything the SDK raises when a request does not come back with an answer this library
    /// can use: a connection that was never made or was lost, a request that timed out, the node's
    /// own error responses (<see cref="RippledException"/>), and a cancellation the caller's token did
    /// not cause — the SDK cancels requests in flight when the network drops (spec I14). The caller's
    /// own cancellation passes through, as does an <see cref="XrplFileException"/> from further in.
    /// </summary>
    private static bool IsNodeFailure(Exception e, CancellationToken cancellationToken) =>
        e is not XrplFileException
        && (e is XrplException or System.TimeoutException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested));

    private static XrplFileException Unreachable(string command, Exception e) =>
        new(XrplFileError.NodeUnreachable, $"the node did not answer {command}: {e.Message}", inner: e);

    private static XrplFileException Answered(string command, JsonElement error) =>
        new(XrplFileError.NodeUnreachable, $"the node answered {error.GetString() ?? error.GetRawText()} to {command}");

    private async Task<NodeAnswer> SendAsync(BaseRequest request, CancellationToken cancellationToken)
    {
        XrplResponse<UnreadResult> response = await ClientKeeper.For(_client!)
            .SendAsync(() => Send(request, cancellationToken), cancellationToken).ConfigureAwait(false);

        return NodeAnswer.Parse(response.Raw.Span, response.Warning);
    }

    private Task<XrplResponse<UnreadResult>> Send(BaseRequest request, CancellationToken cancellationToken) =>
        request switch
        {
            TxRequest tx => _client!.GRequest<UnreadResult, TxRequest>(tx, cancellationToken),
            AccountTransactionsRequest transactions => _client!.GRequest<UnreadResult, AccountTransactionsRequest>(transactions, cancellationToken),
            ServerInfoRequest info => _client!.GRequest<UnreadResult, ServerInfoRequest>(info, cancellationToken),
            ServerStateRequest state => _client!.GRequest<UnreadResult, ServerStateRequest>(state, cancellationToken),
            AccountInfoRequest accountInfo => _client!.GRequest<UnreadResult, AccountInfoRequest>(accountInfo, cancellationToken),
            AccountObjectsRequest objects => _client!.GRequest<UnreadResult, AccountObjectsRequest>(objects, cancellationToken),
            LedgerRequest ledger => _client!.GRequest<UnreadResult, LedgerRequest>(ledger, cancellationToken),
            FeeRequest fee => _client!.GRequest<UnreadResult, FeeRequest>(fee, cancellationToken),
            LedgerEntryRequest entry => _client!.GRequest<UnreadResult, LedgerEntryRequest>(entry, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Command, "this source sends tx, account_tx, server_info, server_state, account_info, account_objects, ledger, fee and ledger_entry, nothing else"),
        };

    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
            ? result
            : null;
}

/// <summary>
/// The <c>T</c> of every request <see cref="XrplClientLedgerSource"/> sends. The source reads
/// <see cref="XrplResponse{T}.Raw"/> only, and with <see cref="object"/> the SDK built a
/// page-sized <see cref="JsonElement"/> per page that nothing used. Every member of the answer is
/// unknown to this type, and the SDK's deserializer skips unknown members, so it reads nothing.
/// </summary>
internal sealed class UnreadResult
{
}
