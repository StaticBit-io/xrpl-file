namespace XrplFile;

/// <summary>
/// Memos as inners of the writer, eight to a batch, in outers the payer signs and pays for (spec B1,
/// 4.1). The writer's sequence carries the chunks; the payer's sequence orders the batches; nothing
/// is queued, and nothing locks a reserve. Rounds assign sequences from one snapshot, send, settle
/// and sweep; whatever is still missing goes into the next round with fresh sequences.
///
/// One instance serves one session: the candidates per memo and the budget are counted over its
/// lifetime, and SubmitAllAsync is not re-entrant.
/// </summary>
public sealed class BatchStrategy : ISubmissionStrategy
{
    private readonly BatchContext _context;
    private readonly BatchStrategyOptions _options;
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
            sink, source, writer, payer, budget, _options, new BatchFence(fence), new BatchWindow(_options.InitialWindow), new BatchTrace(), new BatchSpending(budget));
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

    internal int WindowSize => _context.Window.Size;

    internal int WindowInFlight => _context.Window.InFlight;

    public async Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memos);
        RequireDistinctMarkers(memos);

        Dictionary<int, LandedInner> landed = [];
        Dictionary<int, int> candidates = [];
        List<int> pending = [.. Enumerable.Range(0, memos.Count)];
        BatchProgress progress = new(submitted);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireAnotherCandidate(memos, pending, candidates);

            RoundResult round = await new BatchRound(_context, memos, pending, candidates, progress).RunAsync(cancellationToken).ConfigureAwait(false);

            foreach ((int index, LandedInner inner) in round.Landed) landed.TryAdd(index, inner);

            Interlocked.Add(ref _payerFeeDrops, round.PayerFeeDrops);
            if (!round.PayerFeeKnown) _payerFeeUnknown = true;

            progress.Found(landed.Keys);
            pending = [.. pending.Where(index => !landed.ContainsKey(index))];
        }

        return [.. landed.OrderBy(pair => pair.Key)
            .Select(pair => new SubmittedMemo(pair.Key, pair.Value.Hash) { LedgerIndex = pair.Value.LedgerIndex })];
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

    /// <summary>A memo that would need a fourth candidate fails the run (spec B13).</summary>
    private void RequireAnotherCandidate(IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, IReadOnlyDictionary<int, int> candidates)
    {
        foreach (int index in pending)
        {
            if (candidates.GetValueOrDefault(index) >= _options.MaxAttemptsPerMemo)
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"the memo marked {memos[index].MemoTypeHex} did not land in the {_options.MaxAttemptsPerMemo} candidates a session allows it");
            }
        }
    }

    private static void RequireDistinctMarkers(IReadOnlyList<MemoSubmission> memos)
    {
        HashSet<string> markers = new(memos.Count, StringComparer.OrdinalIgnoreCase);

        foreach (MemoSubmission memo in memos)
        {
            if (!markers.Add(memo.MemoTypeHex))
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"two memos carry the marker {memo.MemoTypeHex}, which the sweep could not tell apart");
            }
        }
    }
}
