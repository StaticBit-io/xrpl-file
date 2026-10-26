namespace XrplFile;

/// <summary>
/// Memos as inners of the writer, eight to a batch, in outers the payer signs and pays for (spec B1,
/// 4.1). The writer's sequence carries the chunks; the payer's sequence orders the batches; nothing
/// is queued, and nothing locks a reserve. Rounds assign sequences from one snapshot, send, settle
/// and sweep; whatever is still missing goes into the next round with fresh sequences.
///
/// One instance serves one session: the budget is counted over its lifetime and the candidates per
/// memo over each call, across its rounds, and SubmitAllAsync is not re-entrant.
/// </summary>
public sealed class BatchStrategy : ISubmissionStrategy
{
    private readonly BatchContext _context;
    private readonly BatchStrategyOptions _options;
    private readonly List<WeakReference> _finishedRegistries = [];
    private readonly List<BatchFence> _roundFences = [];
    private long _payerFeeDrops;
    private bool _payerFeeUnknown;

    public BatchStrategy(
        ILedgerSink sink, ILedgerSource source, ITransactionSigner writer, ITransactionSigner payer,
        BatchBudget budget, IBatchFenceRecorder fence, BatchStrategyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(payer);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(fence);

        if (string.Equals(writer.Account, payer.Account, StringComparison.Ordinal))
        {
            throw new ArgumentException("the payer of a Batch run is another account than its writer (spec B1)", nameof(payer));
        }

        _options = options ?? new BatchStrategyOptions();
        _context = new BatchContext(
            sink, source, writer, payer, budget, _options, new BatchFence(fence), new BatchWindow(_options.InitialWindow, _options.MaxWindow), new BatchTrace(_options.TraceListener), new BatchSpending(budget), new BatchLedgerFloor());
    }

    /// <summary>The estimate's approved unit, which the session checks the estimate against (spec 4.1).</summary>
    public long FeeDropsPerTransaction => _context.Budget.FeeUnitDrops;

    /// <summary>The writer: every memo is the writer's.</summary>
    public string Account => _context.Writer.Account;

    public string Payer => _context.Payer.Account;

    public int TicketMints => 0;

    /// <summary>
    /// What the payer paid this session: the <c>Fee</c> of every outer this instance signed that
    /// settlement found applied, matched by hash (spec B29). Null when a record did not say its fee.
    /// </summary>
    public long? PayerFeeDrops => _payerFeeUnknown ? null : Interlocked.Read(ref _payerFeeDrops);

    /// <summary>Time spent inside the two signers, over this instance's lifetime (spec 4.2's measurement).</summary>
    public TimeSpan SigningTime => _context.Trace.SigningTime;

    internal IReadOnlyList<string> Trace => _context.Trace.Events;

    /// <summary>
    /// The last 10,000 events of what this instance did, in order and with times: rounds, registrations,
    /// submit answers, resends, halts and settlements. Only ordinals, ledger indexes, fee units and engine
    /// results; never key material. The application hears every event as it happens through
    /// <see cref="BatchStrategyOptions.TraceListener"/>.
    /// </summary>
    public IReadOnlyList<BatchTraceEntry> TraceEntries => _context.Trace.Entries;

    internal int WindowSize => _context.Window.Size;

    internal int WindowInFlight => _context.Window.InFlight;

    /// <summary>One weak reference per round run, to its registry: a test seam that proves a round's blobs do not outlive it (spec S5).</summary>
    internal IReadOnlyList<WeakReference> FinishedRegistries
    {
        get { lock (_finishedRegistries) return [.. _finishedRegistries]; }
    }

    /// <summary>The fence each round ran with: a test seam that proves the fence is the strategy's, not the round's (spec S5).</summary>
    internal IReadOnlyList<BatchFence> RoundFences
    {
        get { lock (_finishedRegistries) return [.. _roundFences]; }
    }

    public async Task<SubmittedMemos> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memos);

        // Per pass and compact (spec S19): a landed bit and a candidate count per memo. Each round
        // takes at most MaxRoundMemos of the memos still missing, in index order, so what a round
        // returned unlanded goes first into the next one.
        SubmittedMemosBuilder landed = new(memos.Count, keepChunkFacts: false);
        byte[] candidates = new byte[memos.Count];
        BatchProgress progress = new(submitted, memos.Count);

        while (landed.Landed.Count < memos.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int[] pending = NextRound(landed.Landed, _options.MaxRoundMemos);
            RequireAnotherCandidate(memos, pending, candidates);

            RoundResult round = await RunRoundAsync(memos, pending, candidates, progress, cancellationToken).ConfigureAwait(false);

            foreach ((int index, LandedInner inner) in round.Landed)
            {
                landed.Record(index, RoundMarkers.MarkerHexAt(memos, index), inner.Hash, inner.LedgerIndex);
            }

            Interlocked.Add(ref _payerFeeDrops, round.PayerFeeDrops);
            if (!round.PayerFeeKnown) _payerFeeUnknown = true;
        }

        return landed.Build();
    }

    /// <summary>Runs one round; its registry, history and marker map go out of scope with it.</summary>
    private async Task<RoundResult> RunRoundAsync(
        IReadOnlyList<MemoSubmission> memos, int[] pending, byte[] candidates, BatchProgress progress, CancellationToken cancellationToken)
    {
        BatchRound round = new(_context, memos, pending, candidates, progress);
        lock (_finishedRegistries)
        {
            _finishedRegistries.Add(new WeakReference(round.Registry));
            _roundFences.Add(round.Registry.Fence);
        }

        return await round.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The first <paramref name="cap"/> memos that have not landed, in index order.</summary>
    private static int[] NextRound(LandedSet landed, int cap)
    {
        List<int> pending = new(Math.Min(cap, landed.Capacity - landed.Count));

        for (int index = landed.NextMissing(0); index >= 0 && pending.Count < cap; index = landed.NextMissing(index + 1))
        {
            pending.Add(index);
        }

        return [.. pending];
    }

    /// <summary>
    /// Spends this library's leftover tickets on the writer with plain AccountSets (spec 4.1 "Tidy-up"),
    /// with the behaviour and bounds of spec 2026-10-12 (I6–I8): the session calls it only after the
    /// last round settled and the run recorded "done". It runs at the approved unit.
    /// </summary>
    public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken) =>
        LeftoverTickets.ReleaseAsync(
            _context.Sink, _context.Source, _context.Writer, _context.Budget.FeeUnitDrops, _options.ChainCheck, _options.Time, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>A memo that would need a fourth candidate fails the run (spec B13); the counts carry across rounds (spec S5).</summary>
    private void RequireAnotherCandidate(IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, byte[] candidates)
    {
        foreach (int index in pending)
        {
            if (candidates[index] >= _options.MaxAttemptsPerMemo)
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"the memo marked {RoundMarkers.MarkerHexAt(memos, index)} did not land in the {_options.MaxAttemptsPerMemo} candidates a session allows it");
            }
        }
    }
}
