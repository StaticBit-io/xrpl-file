using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xrpl.BinaryCodec;
using Xrpl.Utils.Hashes;

namespace XrplFile.MemoryProbe;

/// <summary>
/// A Batch node for the memory test (spec S27). The rules are <c>FakeBatchLedgerSink</c>'s; the
/// storage is not. A landed record is a 16-byte row, and a signed transaction adds its hash and fee
/// to flat arrays sized up front. A chunk's memo is checked against the source when it lands and
/// written back from the source when history is read, because the general fake keeps every memo and
/// would measure itself. Memos that are not chunks are kept as text: a few hundred at most.
/// </summary>
internal sealed class CompactLedger : ILedgerSink, ILedgerSource
{
    private const int OuterKind = 0;
    private const int InnerKind = 1;
    private const int SoloKind = 2;
    private const int NoMemo = -1;

    /// <summary>The tec a scripted retry ends in; the strategy sends the chunk again.</summary>
    private const string RetriedResult = "tecINSUFFICIENT_RESERVE";

    /// <summary>History trails the validated ledger by this many ledgers.</summary>
    private const int HistoryLagLedgers = 2;

    private const long Balance = 1_000_000_000_000;

    private readonly Lock _gate = new();
    private readonly IPublicationSource _file;
    private readonly string _writer;
    private readonly string _payer;
    private readonly HashSet<long> _failOnce;
    private readonly Dictionary<string, uint> _validated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _open = new(StringComparer.Ordinal);
    private readonly List<Pending> _openLedger = [];
    private readonly List<Pending> _held = [];
    private readonly List<(string? Type, string? Data, string? Format)> _otherMemos = [];

    private Row[] _rows;
    private int _rowCount;
    private byte[] _hashes;
    private int[] _fees;
    private int _slotCount;
    private long _current = 2;

    public CompactLedger(IPublicationSource file, string writer, uint writerNext, string payer, uint payerNext, IEnumerable<long> failOnce)
    {
        _file = file;
        _writer = writer;
        _payer = payer;
        _failOnce = [.. failOnce];
        _validated[writer] = _open[writer] = writerNext;
        _validated[payer] = _open[payer] = payerNext;

        // Sized once for the whole run, retries and the resume's re-sends included, so growth never
        // doubles an array in the middle of the measurement.
        long chunks = FileTree.ChunkCount(file.Length, MemoLayout.ChunkBytes);
        _rows = new Row[checked((int)(chunks + chunks / 4 + 65_536))];
        int slots = checked((int)(chunks / 6 + 16_384));
        _hashes = new byte[slots * 32];
        _fees = new int[slots];
    }

    /// <summary>The bytes this fake's own record arrays hold, which the probe takes off its peak.</summary>
    public long ReservedBytes
    {
        get
        {
            lock (_gate) return (long)_rows.Length * Unsafe.SizeOf<Row>() + _hashes.Length + (long)_fees.Length * sizeof(int);
        }
    }

    /// <param name="Packed">Kind in bits 0–1, failed in bit 2, and from bit 3 the link: an outer or solo's hash slot, an inner's outer row.</param>
    private readonly record struct Row(int Ledger, int Memo, uint Sequence, int Packed)
    {
        public int Kind => Packed & 3;

        public bool Failed => (Packed & 4) != 0;

        public int Link => Packed >> 3;

        public static Row Of(int ledger, int memo, uint sequence, int kind, bool failed, int link) =>
            new(ledger, memo, sequence, kind | (failed ? 4 : 0) | (link << 3));
    }

    private readonly record struct InnerMemo(int Memo, uint Sequence);

    private sealed record Pending(byte[] Hash, int Kind, string Account, uint Sequence, uint LastLedger, int Fee, int Memo, InnerMemo[] Inners);

    public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult((validated ? _validated : _open).GetValueOrDefault(account, 1u));
    }

    public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
    {
        SignedTransaction signed = await signer.SignAsync(transaction, cancellationToken).ConfigureAwait(false);
        string answer = await SubmitBlobAsync(signed.TxBlob, cancellationToken).ConfigureAwait(false);
        return new SubmitOutcome(answer, signed.Hash);
    }

    public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
    {
        // Concurrent senders overlap, as on a node.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        Pending pending = Decode(txBlob);

        lock (_gate) return Apply(pending);
    }

    public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken)
    {
        byte[] wanted = Convert.FromHexString(hash);

        lock (_gate)
        {
            for (int slot = 0; slot < _slotCount; slot++)
            {
                if (!_hashes.AsSpan(slot * 32, 32).SequenceEqual(wanted)) continue;

                for (int row = 0; row < _rowCount; row++)
                {
                    if (_rows[row].Kind != InnerKind && _rows[row].Link == slot) return Task.FromResult<LedgerTransaction?>(Record(_rows, _rowCount, row));
                }
            }
        }

        return Task.FromResult<LedgerTransaction?>(null);
    }

    public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
        string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (fromLedger > toLedger)
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable, $"probe: invalidLgrRange from {fromLedger} to {toLedger}");
        }

        Row[] rows;
        int count;
        long visible;

        lock (_gate)
        {
            rows = _rows;
            count = _rowCount;
            visible = _current - HistoryLagLedgers;
        }

        bool payer = string.Equals(account, _payer, StringComparison.Ordinal);
        bool writer = string.Equals(account, _writer, StringComparison.Ordinal);
        long to = Math.Min(toLedger, visible);
        if ((!payer && !writer) || fromLedger > to) yield break;

        // Rows are appended in ledger order and never changed, so the slice is read without the lock.
        int first = FirstRowAt(rows, count, fromLedger);
        int end = FirstRowAt(rows, count, to + 1);

        for (int step = 0; step < end - first; step++)
        {
            int index = forward ? first + step : end - 1 - step;
            if ((rows[index].Kind == OuterKind) != payer) continue;

            if (step % 256 == 0)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }

            yield return Record(rows, count, index);
        }
    }

    public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => Task.FromResult<long?>(1);

    public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => Task.FromResult(new NetworkFees(10, 1_000_000, 200_000));

    public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => Task.FromResult(Balance);

    public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) => Task.FromResult(new AccountState(Balance, 0, 0));

    public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LiveTicket>>([]);

    public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CloseLedger();
            return Task.FromResult(_current);
        }
    }

    public Task<LedgerSnapshot> SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CloseLedger();

            Dictionary<string, uint> sequences = new(StringComparer.Ordinal);
            foreach (string account in accounts) sequences[account] = _validated.GetValueOrDefault(account);

            return Task.FromResult(new LedgerSnapshot(_current, sequences));
        }
    }

    public Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken) => Task.FromResult(new OpenLedgerFee(10, 10));

    public Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(string.Equals(name, Amendments.Batch, StringComparison.Ordinal));

    public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) =>
        Task.FromResult(new DestinationState(true, false, false));

    private Pending Decode(string txBlob)
    {
        JsonObject tx = XrplBinaryCodec.Decode(txBlob).AsObject();
        byte[] hash = Convert.FromHexString(HashLedger.HashSignedTx(txBlob));
        string type = (string)tx["TransactionType"]!;
        string account = (string)tx["Account"]!;

        if (tx["TicketSequence"] is not null) throw new NotSupportedException("probe: the Batch path sends no ticketed transaction");

        uint sequence = (uint)Number(tx["Sequence"]);
        uint lastLedger = (uint)Number(tx["LastLedgerSequence"]);
        int fee = checked((int)Number(tx["Fee"]));

        if (type != "Batch") return new Pending(hash, SoloKind, account, sequence, lastLedger, fee, MemoOf(tx), []);

        JsonArray raw = (JsonArray)tx["RawTransactions"]!;
        InnerMemo[] inners = new InnerMemo[raw.Count];

        for (int i = 0; i < raw.Count; i++)
        {
            JsonObject inner = raw[i]!["RawTransaction"]!.AsObject();
            if (!string.Equals((string?)inner["Account"], _writer, StringComparison.Ordinal)) throw new NotSupportedException("probe: an inner of another account");

            inners[i] = new InnerMemo(MemoOf(inner), (uint)Number(inner["Sequence"]));
        }

        return new Pending(hash, OuterKind, account, sequence, lastLedger, fee, NoMemo, inners);
    }

    /// <summary>A chunk memo that matches the source becomes its index; anything else is kept as text.</summary>
    private int MemoOf(JsonObject tx)
    {
        if (tx["Memos"] is not JsonArray { Count: > 0 } memos || memos[0]?["Memo"] is not JsonObject memo) return NoMemo;

        string? type = (string?)memo["MemoType"];
        string? data = (string?)memo["MemoData"];
        string? format = (string?)memo["MemoFormat"];

        if (format is null && MemoLayout.ChunkIndexOf(type) is long index && index <= int.MaxValue
            && string.Equals(data, ChunkHex(index), StringComparison.OrdinalIgnoreCase))
        {
            return (int)index;
        }

        lock (_gate)
        {
            _otherMemos.Add((type, data, format));
            return -2 - (_otherMemos.Count - 1);
        }
    }

    private string ChunkHex(long index)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MemoLayout.ChunkBytes);

        try
        {
            int read = _file.ReadChunk(index, buffer);
            return MemoLayout.Hex(buffer.AsSpan(0, read));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Called under the gate.</summary>
    private string Apply(Pending pending)
    {
        long openLedger = _current + 1;
        uint next = _open.GetValueOrDefault(pending.Account, 1u);

        if (pending.Sequence < next) return "tefPAST_SEQ";
        if (pending.LastLedger != 0 && pending.LastLedger < openLedger) return "tefMAX_LEDGER";

        if (pending.Sequence > next)
        {
            if (!_held.Exists(held => held.Hash.AsSpan().SequenceEqual(pending.Hash))) _held.Add(pending);
            return "terPRE_SEQ";
        }

        _openLedger.Add(pending);
        _open[pending.Account] = pending.Sequence + 1;

        while (_held.Find(held => held.Account == pending.Account && held.Sequence == _open[pending.Account]) is { } ready)
        {
            _held.Remove(ready);
            _openLedger.Add(ready);
            _open[ready.Account] = ready.Sequence + 1;
        }

        return "tesSUCCESS";
    }

    /// <summary>Called under the gate.</summary>
    private void CloseLedger()
    {
        int ledger = checked((int)(_current + 1));

        foreach (Pending pending in _openLedger) Write(pending, ledger);
        _openLedger.Clear();

        // Nothing held can apply in a ledger past its deadline.
        _held.RemoveAll(held => held.LastLedger != 0 && held.LastLedger < ledger + 1);

        foreach ((string account, uint validated) in _validated) _open[account] = Math.Max(_open.GetValueOrDefault(account, 1u), validated);

        _current = ledger;
    }

    /// <summary>Called under the gate.</summary>
    private void Write(Pending pending, int ledger)
    {
        // Applied in a ledger whose earlier transactions already took this sequence: no record.
        if (pending.Sequence != _validated.GetValueOrDefault(pending.Account, 1u)) return;

        _validated[pending.Account] = pending.Sequence + 1;
        int slot = AddSlot(pending.Hash, pending.Fee);

        if (pending.Kind == SoloKind)
        {
            AddRow(Row.Of(ledger, pending.Memo, pending.Sequence, SoloKind, failed: false, slot));
            return;
        }

        int outer = AddRow(Row.Of(ledger, NoMemo, pending.Sequence, OuterKind, failed: false, slot));

        foreach (InnerMemo inner in pending.Inners)
        {
            uint expected = _validated.GetValueOrDefault(_writer, 1u);
            if (inner.Sequence != expected) continue;

            _validated[_writer] = expected + 1;
            bool failed = inner.Memo >= 0 && _failOnce.Remove(inner.Memo);
            AddRow(Row.Of(ledger, inner.Memo, inner.Sequence, InnerKind, failed, outer));
        }
    }

    /// <summary>Called under the gate.</summary>
    private int AddRow(Row row)
    {
        if (_rowCount == _rows.Length) Array.Resize(ref _rows, _rows.Length + _rows.Length / 2);

        _rows[_rowCount] = row;
        return _rowCount++;
    }

    /// <summary>Called under the gate.</summary>
    private int AddSlot(byte[] hash, int fee)
    {
        if (_slotCount == _fees.Length)
        {
            Array.Resize(ref _fees, _fees.Length + _fees.Length / 2);
            Array.Resize(ref _hashes, _fees.Length * 32);
        }

        hash.CopyTo(_hashes.AsSpan(_slotCount * 32, 32));
        _fees[_slotCount] = fee;
        return _slotCount++;
    }

    private LedgerTransaction Record(Row[] rows, int count, int index)
    {
        Row row = rows[index];
        int position = index - FirstRowAt(rows, count, row.Ledger);
        string hash;
        string account;
        string type;
        long fee;
        string? parent = null;

        lock (_gate)
        {
            switch (row.Kind)
            {
                case OuterKind:
                    (hash, account, type, fee) = (HashHex(row.Link), _payer, "Batch", _fees[row.Link]);
                    break;
                case SoloKind:
                    (hash, account, type, fee) = (HashHex(row.Link), _writer, "AccountSet", _fees[row.Link]);
                    break;
                default:
                    int outerSlot = rows[row.Link].Link;
                    parent = HashHex(outerSlot);
                    hash = InnerHash(outerSlot, index);
                    (account, type, fee) = (_writer, "AccountSet", 0);
                    break;
            }
        }

        (string? memoType, string? memoData, string? memoFormat) = MemoText(row.Memo);
        string result = row.Failed ? RetriedResult : "tesSUCCESS";

        return new LedgerTransaction(hash, row.Ledger, account, !row.Failed, true, result, memoType, memoData, memoFormat)
        {
            TransactionType = type,
            Sequence = row.Sequence,
            Fee = fee,
            ParentBatchID = parent,
            TransactionIndex = position,
        };
    }

    private (string? Type, string? Data, string? Format) MemoText(int memo)
    {
        if (memo == NoMemo) return (null, null, null);
        if (memo >= 0) return (MemoLayout.ChunkMarkerHex(memo), ChunkHex(memo), null);

        lock (_gate) return _otherMemos[-2 - memo];
    }

    /// <summary>Called under the gate.</summary>
    private string HashHex(int slot) => Convert.ToHexString(_hashes, slot * 32, 32);

    /// <summary>Called under the gate. An inner's hash is not the outer's business; any stable, distinct value will do.</summary>
    private string InnerHash(int outerSlot, int row)
    {
        Span<byte> input = stackalloc byte[36];
        _hashes.AsSpan(outerSlot * 32, 32).CopyTo(input);
        BitConverter.TryWriteBytes(input[32..], row);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    /// <summary>The first row at or after <paramref name="ledger"/>; <paramref name="count"/> when there is none.</summary>
    private static int FirstRowAt(Row[] rows, int count, long ledger)
    {
        int low = 0;
        int high = count;

        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (rows[middle].Ledger < ledger) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    private static long Number(JsonNode? node) =>
        node is null ? 0 : long.Parse(node.ToJsonString().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture);
}
