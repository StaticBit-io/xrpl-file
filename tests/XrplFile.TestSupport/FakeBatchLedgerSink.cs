using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xrpl.BinaryCodec;
using Xrpl.Utils.Hashes;

namespace XrplFile.Tests;

/// <summary>
/// A node for the Batch path, modelled on what the spike measured (spec section 1, 7.3).
///
/// A submitted blob is decoded with the SDK's own codec. An outer is applied to the open ledger at
/// submit -- it takes the payer's sequence there -- and its inners are applied when the ledger
/// closes, against the writer's validated sequence, in the order the outers were accepted. A
/// <c>tfIndependent</c> inner that ends in tec consumes its sequence; an inner whose sequence is not
/// the writer's next fails with no record. A later outer that arrives first answers terPRE_SEQ and
/// is held until its turn or its deadline. Foreign transactions queued with <see cref="Foreign"/>
/// apply first at the next close. With <see cref="AutoClose"/> every ledger read closes a ledger.
/// </summary>
public sealed class FakeBatchLedgerSink : ILedgerSink
{
    private readonly Lock _gate = new();
    private readonly FakeLedgerSource _ledger;
    private readonly Dictionary<string, uint> _validated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _open = new(StringComparer.Ordinal);
    private readonly List<Pending> _openLedger = [];
    private readonly List<Pending> _held = [];
    private readonly List<Transmission> _transmissions = [];
    private readonly List<(string Account, string Kind, uint Count)> _foreign = [];
    private readonly Dictionary<string, Dictionary<uint, string>> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Account, uint Sequence), int> _copies = [];
    private int _submits;
    private int _inFlight;
    private int _peakInFlight;
    private int _closes;

    public FakeBatchLedgerSink(FakeLedgerSource ledger)
    {
        _ledger = ledger;
        _ledger.LedgerRead += () =>
        {
            if (AutoClose) CloseLedger();
        };
    }

    /// <summary>One submission as the fake saw it, and what it answered.</summary>
    public sealed record Transmission(
        int Ordinal, string Hash, string TransactionType, string Account, uint Sequence, uint LastLedger, int Inners,
        long OpenLedgerIndex, uint PayerValidatedSequence, string Answer);

    /// <summary>The tec results scripted per inner memo marker. A result that is not a tec would not be a tec inner, so it is refused when set.</summary>
    public sealed class InnerResults
    {
        private readonly Dictionary<string, string> _results = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The tec result for the inner whose memo type is <paramref name="marker"/>.</summary>
        /// <param name="marker">The inner's MemoType, hex.</param>
        public string this[string marker]
        {
            get => _results[marker];
            set
            {
                if (value is null || !value.StartsWith("tec", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"An inner scripted to end in tec cannot end in '{value}'", nameof(value));
                }

                _results[marker] = value;
            }
        }

        internal bool TryGetValue(string marker, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? result) => _results.TryGetValue(marker, out result);
    }

    private sealed record Pending(string Hash, JsonObject Tx, string Type, string Account, uint Sequence, uint LastLedger);

    /// <summary>Closes a ledger at the start of every ledger read. On by default.</summary>
    public bool AutoClose { get; set; } = true;

    /// <summary>How long each submission is held before it is processed, so concurrent senders overlap.</summary>
    public TimeSpan SubmitDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Called for each processed submission, before its answer returns.</summary>
    public Action<Transmission>? Transmitting { get; set; }

    /// <summary>
    /// Batch outers answered with this and not processed, keyed by the outer's own (payer) sequence and
    /// which transmission of that sequence it is (1-based, counted over every blob that carries it from
    /// that account). Keyed by the candidate rather than by arrival order, so a script meant for one
    /// round never hits the next round's first send. Other transaction types are not addressed by it.
    /// </summary>
    public Dictionary<(uint Sequence, int Copy), string> AnswerAt { get; } = [];

    /// <summary>Submissions (by arrival ordinal) processed, then answered with a dropped connection.</summary>
    public HashSet<int> ApplyThenDropAt { get; } = [];

    /// <summary>Outers (by sequence) held this long before they are processed: a later one overtakes them.</summary>
    public Dictionary<uint, TimeSpan> DelayOuter { get; } = [];

    /// <summary>Outers (by sequence) answered tesSUCCESS and never applied; one-shot.</summary>
    public HashSet<uint> LoseOuter { get; } = [];

    /// <summary>Outers (by sequence) that apply with this tec at the close: fee and sequence taken, no inner applied; one-shot.</summary>
    public Dictionary<uint, string> OuterResult { get; } = [];

    /// <summary>Inners (by memo marker) that apply with this tec every time; the sequence is consumed. Only tec results are accepted.</summary>
    public InnerResults InnerResult { get; } = new();

    /// <summary>Inners (by memo marker) that neither apply nor consume a sequence; one-shot.</summary>
    public HashSet<string> InnerVanishes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many ledgers this fake has closed.</summary>
    public int Closes
    {
        get { lock (_gate) return _closes; }
    }

    /// <summary>The most submissions that were in flight at once.</summary>
    public int PeakInFlight
    {
        get { lock (_gate) return _peakInFlight; }
    }

    /// <summary>Every processed submission, in the order it was answered.</summary>
    public IReadOnlyList<Transmission> Transmissions
    {
        get { lock (_gate) return [.. _transmissions]; }
    }

    /// <summary>Sets the account's next sequence, validated and open, as of the ledger the fake is at.</summary>
    /// <param name="account">The account's classic address.</param>
    /// <param name="next">Its next sequence.</param>
    public void SetSequence(string account, uint next)
    {
        lock (_gate)
        {
            _validated[account] = next;
            _open[account] = next;
            _ledger.RecordSequences(_ledger.CurrentLedger, new Dictionary<string, uint> { [account] = next });
        }
    }

    /// <summary>Queues a transaction nobody in the test signed, applied first at the next close.</summary>
    /// <param name="account">The account that signed it.</param>
    /// <param name="kind">AccountSet, TicketCreate or TicketedAccountSet.</param>
    /// <param name="ticketCount">How many tickets a TicketCreate asks for.</param>
    public void Foreign(string account, string kind, uint ticketCount = 0)
    {
        lock (_gate) _foreign.Add((account, kind, ticketCount));
    }

    /// <summary>Gives the account a live ticket created by <paramref name="creatorHash"/>.</summary>
    /// <param name="account">The account's classic address.</param>
    /// <param name="ticket">The ticket's sequence.</param>
    /// <param name="creatorHash">The hash of the TicketCreate that made it.</param>
    public void AddLiveTicket(string account, uint ticket, string creatorHash)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(account, out Dictionary<uint, string>? live))
            {
                live = [];
                _tickets[account] = live;
            }

            live[ticket] = creatorHash;
            PublishTickets(account);
        }
    }

    public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult((validated ? _validated : _open).GetValueOrDefault(account, 1u));
    }

    public async Task<SubmitOutcome> SubmitAsync(
        Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
    {
        SignedTransaction signed = await signer.SignAsync(transaction, cancellationToken).ConfigureAwait(false);
        string answer = await SubmitBlobAsync(signed.TxBlob, cancellationToken).ConfigureAwait(false);
        return new SubmitOutcome(answer, signed.Hash);
    }

    public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
    {
        JsonObject tx = XrplBinaryCodec.Decode(txBlob).AsObject();
        string hash = HashLedger.HashSignedTx(txBlob).ToUpperInvariant();
        string type = (string)tx["TransactionType"]!;
        string account = (string)tx["Account"]!;
        uint sequence = (uint)Number(tx["Sequence"]);
        uint lastLedger = (uint)Number(tx["LastLedgerSequence"]);
        uint flags = (uint)Number(tx["Flags"]);

        if (type == "Batch" && (flags & BatchTransactions.IndependentFlag) == 0)
        {
            throw new InvalidOperationException($"fake: a Batch outer must carry tfIndependent, it carried flags {flags:X8}");
        }

        int inners = tx["RawTransactions"] is JsonArray raw ? raw.Count : 0;
        int ordinal;
        int copy;

        lock (_gate)
        {
            ordinal = _submits++;
            copy = _copies[(account, sequence)] = _copies.GetValueOrDefault((account, sequence)) + 1;
            _inFlight++;
            if (_inFlight > _peakInFlight) _peakInFlight = _inFlight;
        }

        try
        {
            await Task.Yield();

            if (SubmitDelay > TimeSpan.Zero) await Task.Delay(SubmitDelay, cancellationToken).ConfigureAwait(false);

            if (type == "Batch" && DelayOuter.TryGetValue(sequence, out TimeSpan delay))
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            Transmission transmission;

            lock (_gate)
            {
                string answer = type == "Batch" && AnswerAt.TryGetValue((sequence, copy), out string? scripted)
                    ? scripted
                    : Apply(new Pending(hash, tx, type, account, sequence, lastLedger));

                transmission = new Transmission(
                    ordinal, hash, type, account, sequence, lastLedger, inners, _ledger.CurrentLedger + 1,
                    type == "Batch" ? _validated.GetValueOrDefault(account, 1u) : 0, answer);
                _transmissions.Add(transmission);
            }

            Transmitting?.Invoke(transmission);

            if (ApplyThenDropAt.Contains(ordinal))
            {
                throw new XrplFileException(XrplFileError.NodeUnreachable, "fake: the connection dropped before the answer");
            }

            return transmission.Answer;
        }
        finally
        {
            lock (_gate) _inFlight--;
        }
    }

    /// <summary>Closes the open ledger: foreign transactions first, then what was accepted, in order.</summary>
    public void CloseLedger()
    {
        lock (_gate)
        {
            long index = _ledger.CurrentLedger + 1;
            int position = 0;
            List<LedgerTransaction> records = [];

            foreach ((string account, string kind, uint count) in _foreign) WriteForeign(account, kind, count, index, position++, records);
            _foreign.Clear();

            foreach (Pending pending in _openLedger) Write(pending, index, records);
            _openLedger.Clear();

            // Nothing held can apply in a ledger past its deadline.
            _held.RemoveAll(held => held.LastLedger != 0 && held.LastLedger < index + 1);

            foreach (string account in _validated.Keys.ToList())
            {
                _open[account] = Math.Max(_open.GetValueOrDefault(account, 1u), _validated[account]);
            }

            // One step: a reader never sees this ledger's history with the previous ledger's sequences.
            _ledger.Publish(index, records, new Dictionary<string, uint>(_validated, StringComparer.Ordinal));
            _closes++;
        }
    }

    /// <summary>Called under the gate.</summary>
    private string Apply(Pending pending)
    {
        long openLedger = _ledger.CurrentLedger + 1;

        if (pending.Tx["TicketSequence"] is { } ticketNode)
        {
            uint ticket = (uint)Number(ticketNode);
            bool taken = _openLedger.Any(open => open.Account == pending.Account
                && open.Tx["TicketSequence"] is { } other && (uint)Number(other) == ticket);

            if (taken || !_tickets.TryGetValue(pending.Account, out Dictionary<uint, string>? live) || !live.ContainsKey(ticket)) return "tefNO_TICKET";
            if (pending.LastLedger != 0 && pending.LastLedger < openLedger) return "tefMAX_LEDGER";

            // The ticket stays listed until the close validates its spend, as a validated listing does.
            _openLedger.Add(pending);
            return "tesSUCCESS";
        }

        uint next = _open.GetValueOrDefault(pending.Account, 1u);

        if (pending.Sequence < next) return "tefPAST_SEQ";
        if (pending.LastLedger != 0 && pending.LastLedger < openLedger) return "tefMAX_LEDGER";

        if (pending.Sequence > next)
        {
            if (!_held.Any(held => held.Hash == pending.Hash)) _held.Add(pending);
            return "terPRE_SEQ";
        }

        if (pending.Type == "Batch" && LoseOuter.Remove(pending.Sequence)) return "tesSUCCESS";

        Accept(pending);
        return "tesSUCCESS";
    }

    /// <summary>Called under the gate.</summary>
    private void Accept(Pending pending)
    {
        _openLedger.Add(pending);
        _open[pending.Account] = pending.Sequence + 1;

        while (_held.FirstOrDefault(held => held.Account == pending.Account && held.Sequence == _open[pending.Account]) is { } ready)
        {
            _held.Remove(ready);
            _openLedger.Add(ready);
            _open[ready.Account] = ready.Sequence + 1;
        }
    }

    /// <summary>Called under the gate.</summary>
    private void Write(Pending pending, long index, List<LedgerTransaction> records)
    {
        if (pending.Tx["TicketSequence"] is { } ticketNode)
        {
            if (_tickets.TryGetValue(pending.Account, out Dictionary<uint, string>? live) && live.Remove((uint)Number(ticketNode)))
            {
                PublishTickets(pending.Account);
            }

            (string? type, string? data, string? format) = MemoOf(pending.Tx);
            records.Add(new LedgerTransaction(pending.Hash, index, pending.Account, true, true, "tesSUCCESS", type, data, format)
            {
                TransactionType = pending.Type,
                Sequence = 0,
                TicketSequence = (uint)Number(ticketNode),
                Fee = Number(pending.Tx["Fee"]),
            });
            return;
        }

        // Applied in a ledger whose foreign transactions already took this sequence: no record.
        if (pending.Sequence != _validated.GetValueOrDefault(pending.Account, 1u)) return;

        _validated[pending.Account] = pending.Sequence + 1;

        if (pending.Type != "Batch")
        {
            (string? type, string? data, string? format) = MemoOf(pending.Tx);
            records.Add(new LedgerTransaction(pending.Hash, index, pending.Account, true, true, "tesSUCCESS", type, data, format)
            {
                TransactionType = pending.Type,
                Sequence = pending.Sequence,
                Fee = Number(pending.Tx["Fee"]),
            });
            return;
        }

        string outcome = OuterResult.Remove(pending.Sequence, out string? scripted) ? scripted : "tesSUCCESS";
        records.Add(new LedgerTransaction(pending.Hash, index, pending.Account, outcome == "tesSUCCESS", true, outcome, null, null, null)
        {
            TransactionType = "Batch",
            Sequence = pending.Sequence,
            Fee = Number(pending.Tx["Fee"]),
        });

        if (outcome != "tesSUCCESS") return;

        int position = 0;

        foreach (JsonNode? wrapper in (JsonArray)pending.Tx["RawTransactions"]!)
        {
            JsonObject inner = wrapper!["RawTransaction"]!.AsObject();
            string innerAccount = (string)inner["Account"]!;
            uint innerSequence = (uint)Number(inner["Sequence"]);
            (string? type, string? data, string? format) = MemoOf(inner);
            string marker = type ?? string.Empty;

            if (InnerVanishes.Remove(marker)) continue;
            if (innerSequence != _validated.GetValueOrDefault(innerAccount, 1u)) continue;

            string result = InnerResult.TryGetValue(marker, out string? failure) ? failure : "tesSUCCESS";
            _validated[innerAccount] = innerSequence + 1;

            records.Add(new LedgerTransaction(Sha($"inner:{pending.Hash}:{position++}"), index, innerAccount,
                result == "tesSUCCESS", true, result, type, data, format)
            {
                TransactionType = (string?)inner["TransactionType"],
                Sequence = innerSequence,
                Fee = 0,
                ParentBatchID = pending.Hash,
            });
        }
    }

    /// <summary>Called under the gate.</summary>
    private void WriteForeign(string account, string kind, uint count, long index, int position, List<LedgerTransaction> records)
    {
        uint next = _validated.GetValueOrDefault(account, 1u);
        string hash = Sha($"foreign:{account}:{index}:{position}");

        LedgerTransaction record = kind switch
        {
            "AccountSet" => new LedgerTransaction(hash, index, account, true, true, "tesSUCCESS", null, null, null)
            {
                TransactionType = "AccountSet", Sequence = next, Fee = 12,
            },
            "TicketCreate" => new LedgerTransaction(hash, index, account, true, true, "tesSUCCESS", null, null, null)
            {
                TransactionType = "TicketCreate", Sequence = next, TicketCount = count, Fee = 12,
            },
            "TicketedAccountSet" => new LedgerTransaction(hash, index, account, true, true, "tesSUCCESS", null, null, null)
            {
                TransactionType = "AccountSet", Sequence = 0, TicketSequence = 900_000, Fee = 12,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "AccountSet, TicketCreate or TicketedAccountSet"),
        };

        records.Add(record);

        if (kind == "AccountSet") _validated[account] = next + 1;
        if (kind == "TicketCreate") _validated[account] = next + 1 + count;
    }

    /// <summary>Called under the gate.</summary>
    private void PublishTickets(string account)
    {
        Dictionary<uint, string> live = _tickets.GetValueOrDefault(account) ?? [];
        _ledger.SetTicketCount(account, live.Count);
        _ledger.SetLiveTickets(account, [.. live.OrderBy(pair => pair.Key).Select(pair => new LiveTicket(pair.Key, pair.Value))]);
    }

    private static (string? Type, string? Data, string? Format) MemoOf(JsonObject tx) =>
        tx["Memos"] is JsonArray memos && memos.Count > 0 && memos[0]?["Memo"] is JsonObject memo
            ? ((string?)memo["MemoType"], (string?)memo["MemoData"], (string?)memo["MemoFormat"])
            : (null, null, null);

    private static long Number(JsonNode? node) =>
        node is null ? 0 : long.Parse(node.ToJsonString().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
