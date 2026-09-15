using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XrplFile.Tests;

/// <summary>
/// A sink that validates everything on the spot into a FakeLedgerSource, one hundred
/// transactions per ledger, so the writer's read-back has something to read.
///
/// It answers asynchronously on purpose. A real node answers over a socket, so a parallel sender's
/// submissions genuinely overlap; a fake that completed synchronously would let each task run to
/// the end before the next one started, and every test of concurrency written against it would be
/// vacuous. Everything it mutates is guarded, so an overlapping sender corrupts nothing.
///
/// It signs through the signer it is handed, at the point a real sink signs: before a position is
/// taken and before anything reaches the ledger. It does not use the blob — the hashes here are
/// derived from a submission's position so a test can read them — but a signer that refuses now
/// fails the submission and leaves no trace, which is the whole behaviour a vault-backed signer
/// exists for. Before this the parameter was ignored outright, and a strategy that stopped passing
/// its signer through would have been caught by nothing short of a run against a real node.
/// </summary>
public sealed class FakeLedgerSink(FakeLedgerSource ledger, long firstLedger = 5000) : ILedgerSink
{
    private readonly Lock _gate = new();
    private readonly List<Dictionary<string, object>> _submitted = [];
    private uint _nextSequence = 10;

    /// <summary>
    /// What a validated-sequence read answers instead of <see cref="_nextSequence"/>, when set by
    /// <see cref="SetValidatedSequence"/>. Null — the default — keeps this fake's original
    /// single-counter behaviour, where a validated read and an unvalidated one answer the same
    /// number, which is what every test that does not call <see cref="SetValidatedSequence"/>
    /// relies on.
    /// </summary>
    private uint? _validatedSequenceOverride;

    private int _ledgersClosed;
    private int _inFlight;
    private int _peakInFlight;
    private int _signatures;

    /// <summary>
    /// How many signatures this sink has asked its signer for and actually received, refusals —
    /// a locked vault, an unencodable transaction, anything the signer throws — not counted: the
    /// increment sits after the await, so an exception there skips it.
    /// </summary>
    public int Signatures => Volatile.Read(ref _signatures);

    public IReadOnlyList<Dictionary<string, object>> Submitted
    {
        get { lock (_gate) return [.. _submitted]; }
    }

    /// <summary>Which submissions (by position) the node refuses, and with what.</summary>
    public Dictionary<int, string> Refusals { get; } = [];

    /// <summary>Applied to the stored MemoData of the given submission — corrupts the ledger copy without touching what was signed.</summary>
    public Dictionary<int, Func<string, string>> StoredAs { get; } = [];

    /// <summary>
    /// Which submissions (by position) the node accepts and then validates as failed: the tec*
    /// a transaction only reaches once the ledger closes, which no engine result announced.
    /// </summary>
    public HashSet<int> FailsAs { get; } = [];

    /// <summary>Thrown instead of submitting, at this position — a node failing the way the SDK reports it.</summary>
    public Dictionary<int, Exception> Throws { get; } = [];

    /// <summary>Tickets this fake account holds, minted by TicketCreate and spent by use.</summary>
    public HashSet<uint> LiveTickets { get; } = [];

    /// <summary>
    /// Puts tickets on the account that no strategy instance knows about — what an interrupted run
    /// leaves behind, since nothing on the XRPL cancels a ticket and DisposeAsync only drops the
    /// queue. Numbered well above anything a mint here will issue, so they cannot be mistaken for
    /// one of this run's own.
    /// </summary>
    public void LeakTickets(string account, int count)
    {
        lock (_gate)
        {
            for (uint ticket = 1_000_000; ticket < 1_000_000 + (uint)count; ticket++) LiveTickets.Add(ticket);

            ledger.TicketCounts[account] = LiveTickets.Count;
        }
    }

    /// <summary>Which submissions (by position) answer terQUEUED and land only on a later sweep.</summary>
    public Dictionary<int, int> QueuedUntilSweep { get; } = [];

    /// <summary>
    /// When set, every submission answers <c>terQUEUED</c> rather than reaching the ledger, however
    /// many of them there are — a node under enough load to queue everything a caller sends it,
    /// rather than one scripted submission picked out by position. Nothing else here expresses that
    /// without knowing the position a caller's submission will land at in advance, which a model
    /// under test often cannot. <see cref="Refusals"/> is checked first, so a position scripted
    /// there answers what it was scripted to rather than being silently overridden by this one.
    /// </summary>
    public bool QueueEverything { get; set; }

    /// <summary>Markers whose every submission validates as failed, however often it is retried.</summary>
    public HashSet<string> FailMarkers { get; } = [];

    /// <summary>
    /// Which submissions (by position) the node holds but has not yet validated when its record is
    /// looked up by hash: recorded with Validated: false and no meta at all — the shape a tx
    /// lookup gets for something still in flight, where Succeeded reads as false only because
    /// meta is absent, not because anything failed.
    /// </summary>
    public HashSet<int> UnvalidatedAt { get; } = [];

    /// <summary>Names a marker that always fails; the type exists so a test reads as a sentence.</summary>
    public sealed record FailEvery(string Marker);

    /// <summary>
    /// Every call this sink saw, in order: "query" for a validated-sequence call (SequenceAsync
    /// with validated: true), and "chunk" / "list" / "manifest" / "submit" for a submission,
    /// tagged by what its memo marker says it is. Lets a test see whether the writer interleaves
    /// batch validation waits with submissions, rather than only waiting once at the end.
    /// </summary>
    public List<string> Events { get; } = [];

    /// <summary>
    /// How many of the next validated-sequence queries throw a timeout before answering
    /// normally, so a test can prove the writer's validation wait tolerates one.
    /// </summary>
    public int ValidatedQueryTimeouts { get; set; }

    /// <summary>
    /// How long each submission takes to answer. Zero still yields, which is enough to interleave
    /// parallel senders; a non-zero delay holds them overlapped long enough to measure.
    /// </summary>
    public TimeSpan SubmitDelay { get; set; } = TimeSpan.Zero;

    /// <summary>The most submissions this sink ever had in flight at once.</summary>
    public int PeakInFlight
    {
        get { lock (_gate) return _peakInFlight; }
    }

    /// <summary>
    /// Closes a ledger without anyone submitting anything — the time that passes between two
    /// publications. Without it a second publication lands in the same ledger as the first, and no
    /// sweep anchor can tell one publication's markers from the other's.
    /// </summary>
    public void CloseLedger()
    {
        lock (_gate) _ledgersClosed++;
    }

    /// <summary>
    /// Sets the sequence this sink will next hand out, so a test can put an account within
    /// AccountDelete's deletion threshold without minting ninety throwaway transactions to get
    /// there. Takes no account: this fake tracks one sequence counter regardless of which account
    /// asks for it, the same way <see cref="SequenceAsync"/> itself does — it only ever models the
    /// one throwaway account a real caller signs for, and a parameter this fake could not honour
    /// would invite a test priming two accounts to get one counter and debug the wrong thing.
    /// </summary>
    public void SetSequence(uint sequence)
    {
        lock (_gate) _nextSequence = sequence;
    }

    /// <summary>
    /// Makes a validated-sequence read answer a different number than <see cref="SetSequence"/>'s
    /// unvalidated one, so a test can put the two in disagreement — exactly the situation an
    /// in-flight or already-applied transaction produces on a real node, and the situation
    /// <see cref="ThrowawayAccount.PreviewDeletionAsync"/>'s deletion gate has to survive: it must
    /// measure the threshold against the sequence the transaction will actually be signed with,
    /// not against a validated read that can lag behind it. Pass <see langword="null"/> to go back
    /// to answering <see cref="SetSequence"/>'s number for both reads.
    /// </summary>
    public void SetValidatedSequence(uint? sequence)
    {
        lock (_gate) _validatedSequenceOverride = sequence;
    }

    public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (validated)
            {
                if (ValidatedQueryTimeouts > 0)
                {
                    ValidatedQueryTimeouts--;
                    throw new Xrpl.Client.Exceptions.TimeoutException("fake: the validated-sequence query timed out");
                }

                // A wait for validation is where a real ledger closes; the fake closes one per
                // query, so the manifest lands in a later ledger than the chunks and lists it
                // describes.
                _ledgersClosed++;
                Events.Add("query");

                return Task.FromResult(_validatedSequenceOverride ?? _nextSequence);
            }

            return Task.FromResult(_nextSequence);
        }
    }

    public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
    {
        // First, and before a position is taken: a refused signature must leave nothing behind,
        // and a submission that was never signed must not occupy an index the scripted refusals
        // below are keyed by. A signer that answers in memory — which every signer driven against
        // this fake does — completes this await synchronously, so parallel senders still take
        // their positions in arrival order, exactly as they did when this call was not here.
        //
        // Nothing here catches what the signer throws — a locked vault above all, but just as much
        // whatever the real binary codec refuses to encode. A caller that hands this fake a
        // transaction the codec cannot turn into bytes gets exactly what a real node would never
        // have seen: no submission, no signature counted. Swallowing that would let a regression —
        // an integer Amount, a dropped SigningPubKey — sail through a green suite.
        _ = await signer.SignAsync(transaction, cancellationToken).ConfigureAwait(false);

        Interlocked.Increment(ref _signatures);

        int position;

        lock (_gate)
        {
            position = _submitted.Count;

            if (Throws.TryGetValue(position, out Exception? failure)) throw failure;

            _submitted.Add(transaction);

            _inFlight++;
            if (_inFlight > _peakInFlight) _peakInFlight = _inFlight;
        }

        try
        {
            // Where the socket would be. Positions are handed out above, synchronously, so they
            // still follow submission order however the answers interleave after this point.
            await Task.Yield();

            if (SubmitDelay > TimeSpan.Zero) await Task.Delay(SubmitDelay, cancellationToken).ConfigureAwait(false);

            return Answer(transaction, position);
        }
        finally
        {
            lock (_gate) _inFlight--;
        }
    }

    private SubmitOutcome Answer(Dictionary<string, object> transaction, int position)
    {
        lock (_gate)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"fake:{position}")));

            // Checked before QueueEverything so a test that scripts a specific position and also
            // sets QueueEverything gets the position it asked for, not silently overridden by the
            // catch-all.
            if (Refusals.TryGetValue(position, out string? refusal))
            {
                Events.Add("submit");
                return new SubmitOutcome(refusal, hash);
            }

            if (QueueEverything)
            {
                Events.Add("submit");
                return new SubmitOutcome("terQUEUED", hash);
            }

            if (transaction.TryGetValue("TransactionType", out object? kind) && (string)kind == "TicketCreate")
            {
                uint at = Convert.ToUInt32(transaction["Sequence"]);
                uint count = Convert.ToUInt32(transaction["TicketCount"]);

                // rippled's kMaxTicketThreshold: a TicketCreate that would take the account past
                // the ceiling is charged against a ledger and fails there, minting nothing. A fake
                // that always mints is a fake in which the ceiling does not exist.
                bool tooMany = LiveTickets.Count + count > TicketStrategy.MaxLiveTickets;
                bool mintFailed = tooMany || FailsAs.Contains(position);

                // Exactly what rippled does: tickets at+1..at+count, and the sequence lands at
                // at+1+count. A mint that validates and fails — tecINSUFFICIENT_RESERVE — mints
                // nothing and moves the sequence by one, which is the whole difference a caller
                // has to notice.
                if (!mintFailed)
                {
                    for (uint ticket = at + 1; ticket <= at + count; ticket++) LiveTickets.Add(ticket);
                }

                _nextSequence = mintFailed ? at + 1 : at + count + 1;
                ledger.TicketCounts[(string)transaction["Account"]] = LiveTickets.Count;

                Events.Add("mint");

                bool unvalidated = UnvalidatedAt.Contains(position);
                ledger.Add(new LedgerTransaction(
                    hash, firstLedger + position / 100 + _ledgersClosed, (string)transaction["Account"],
                    Succeeded: !unvalidated && !mintFailed, Validated: !unvalidated,
                    Result: unvalidated ? null
                        : tooMany ? "tecDIR_FULL"
                        : mintFailed ? "tecINSUFFICIENT_RESERVE"
                        : "tesSUCCESS",
                    MemoTypeHex: null, MemoDataHex: null, MemoFormatHex: null));

                return new SubmitOutcome("tesSUCCESS", hash);
            }

            if (transaction.TryGetValue("TicketSequence", out object? ticketField))
            {
                uint ticket = Convert.ToUInt32(ticketField);

                // A node refuses a ticket it does not hold, which is what terPRE_TICKET says.
                if (!LiveTickets.Remove(ticket)) return new SubmitOutcome("terPRE_TICKET", hash);

                ledger.TicketCounts[(string)transaction["Account"]] = LiveTickets.Count;
            }
            else
            {
                // Only a transaction that rides the account's sequence moves it. A ticketed one
                // carries Sequence 0, and taking that literally used to collapse this account's
                // next sequence to 1 and re-issue ticket numbers a later mint had already spent.
                _nextSequence = Convert.ToUInt32(transaction["Sequence"]) + 1;
            }

            string? memoType = null, memoData = null, memoFormat = null;
            if (transaction.TryGetValue("Memos", out object? memos))
            {
                JsonElement first = JsonSerializer.SerializeToElement(memos)[0].GetProperty("Memo");
                memoType = first.TryGetProperty("MemoType", out JsonElement t) ? t.GetString() : null;
                memoData = first.TryGetProperty("MemoData", out JsonElement d) ? d.GetString() : null;
                memoFormat = first.TryGetProperty("MemoFormat", out JsonElement f) ? f.GetString() : null;
            }

            if (memoData is not null && StoredAs.TryGetValue(position, out Func<string, string>? corrupt)) memoData = corrupt(memoData);

            bool failed = FailsAs.Contains(position) || (memoType is not null && FailMarkers.Contains(memoType));

            Events.Add(
                MemoLayout.ChunkIndexOf(memoType) is not null ? "chunk"
                : MemoLayout.ListIndexOf(memoType) is not null ? "list"
                : MemoLayout.IsManifest(memoType) ? "manifest"
                : "submit");

            LedgerTransaction stored = new(
                hash, firstLedger + position / 100 + _ledgersClosed, (string)transaction["Account"],
                Succeeded: !failed, Validated: true,
                Result: failed ? "tecFAKE_FAILURE" : "tesSUCCESS",
                memoType, memoData, memoFormat);

            if (QueuedUntilSweep.TryGetValue(position, out int sweepsUntilVisible))
            {
                ledger.AddDelayed(stored, sweepsUntilVisible);
                return new SubmitOutcome("terQUEUED", hash);
            }

            ledger.Add(stored);

            return new SubmitOutcome("tesSUCCESS", hash);
        }
    }
}
