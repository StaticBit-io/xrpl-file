using System.Security.Cryptography;
using System.Text.Json;

namespace XrplFile;

/// <remarks>
/// The fee an estimate is priced at and the fee a strategy charges are two different numbers, and
/// nothing this class says about money is true unless they are the same one.
/// <see cref="SequenceStrategyOptions.FeeDrops"/> and <see cref="TicketStrategyOptions.FeeDrops"/>
/// both default to a hardcoded 12 drops, while <see cref="PublicationSession.EstimateAsync(ILedgerSource, string, long, SessionOptions, CancellationToken)"/> and
/// <see cref="PublicationSession.StartAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, string, string, string, string, SessionOptions, CancellationToken)"/> price a run from the node's current open-ledger fee
/// — <see cref="PublicationEstimate.FeeDropsPerTransaction"/>.
///
/// A session is handed its <see cref="ISubmissionStrategy"/> already built, so it cannot reach in
/// and change what that strategy charges. What it can do is refuse to start, and it does:
/// <see cref="PublicationSession.StartAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, string, string, string, string, SessionOptions, CancellationToken)"/> compares
/// <see cref="ISubmissionStrategy.FeeDropsPerTransaction"/> against the estimate it just built and
/// throws <see cref="XrplFileError.FeeMismatch"/> before a drop is spent, rather than approving a
/// run priced differently from the one that would actually be submitted.
///
/// So the order on the caller's side is: ask for the estimate — either
/// <see cref="PublicationSession.EstimateAsync(ILedgerSource, string, long, SessionOptions, CancellationToken)"/> or <see cref="PublicationEstimator.EstimateAsync"/>
/// — read its <see cref="PublicationEstimate.FeeDropsPerTransaction"/>, build the strategy with
/// that same value in its own <c>FeeDrops</c> option, then call
/// <see cref="PublicationSession.StartAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, string, string, string, string, SessionOptions, CancellationToken)"/>.
/// </remarks>
public sealed class SessionOptions
{
    /// <summary>Where state files live. One file per run, named after the run.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>The most the run may cost, or null for no ceiling of the caller's own.</summary>
    public long? BudgetDrops { get; init; }

    public int TicketsPerBatch { get; init; } = 100;

    public bool ReadBack { get; init; } = true;

    /// <summary>
    /// How long the tidy-up after a successful publication may take before what it has not settled
    /// is left as it is (spec I8). Linked to the publication's own cancellation.
    /// </summary>
    public TimeSpan TicketReleaseTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The absolute path the bytes were read from, recorded so a resume need not ask for them (spec B17).</summary>
    public string? SourcePath { get; init; }

    /// <summary>How often a resume reads the ledger while it waits for the fence to pass (spec B23).</summary>
    public TimeSpan FencePoll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long the fence wait tolerates a node that fails or a ledger that does not advance (Spec question 4).</summary>
    public TimeSpan NodeOutageLimit { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a resume's reconciliation waits for <c>account_tx</c> to cover the writer's sequence
    /// up to the snapshot taken after the fence (spec 5.2 step 3, B30). Read again every <see cref="FencePoll"/>.
    /// </summary>
    public TimeSpan HistoryLagLimit { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// One run found in a state directory by <see cref="PublicationSession.Unfinished"/>.
/// </summary>
/// <param name="Path">The state file. It is the row's identity even when nothing in it could be read.</param>
/// <param name="State">What the file said, or null when this library could not read it.</param>
/// <param name="Unreadable">
/// Why it could not be read, or null when it could. A file written by a newer version of this
/// library reads as unreadable here and is still worth a line: it may name live tickets and an
/// unspent reserve, and a person who is told the file exists can install the version that reads
/// it, where a person who is told nothing cannot.
/// </param>
public sealed record UnfinishedRun(string Path, PublicationState? State, string? Unreadable);

/// <summary>
/// A publication as something that can be estimated, started, interrupted and found again.
///
/// The writer knows the format's order and the strategy knows how a transaction reaches the
/// ledger. What is left is everything about money and time: what this will cost before it costs
/// it, whether the account can carry it, what survives the process dying, and what to show
/// afterwards. That is this class, and it is the one an application holds.
///
/// One session is one run. <see cref="RunAsync"/> may be called once; the checks that make a run
/// safe to pay for happen in <see cref="StartAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, string, string, string, string, SessionOptions, CancellationToken)"/> and are not repeated.
/// </summary>
public sealed class PublicationSession : IBatchFenceRecorder
{
    /// <summary>
    /// The stages a run passes through, in order. The rank is what keeps the recorded phase moving
    /// forward only: the writer reports a batch's progress through <see cref="Progress{T}"/>, which
    /// lands on the thread pool, so a chunk's report can arrive after the run has moved on.
    /// </summary>
    private static readonly string[] Stages = ["chunks", "lists", "read-back", "manifest", "done"];

    /// <summary>
    /// How long a tidy-up past its deadline is given to unwind and report what it settled before
    /// the session stops waiting for it.
    /// </summary>
    private static readonly TimeSpan TicketReleaseGrace = TimeSpan.FromSeconds(5);

    private ISubmissionStrategy? _strategy;
    private readonly ILedgerSource _source;
    private readonly IPublicationSource _file;

    /// <summary>The file's block hashes, streamed once at start or resume (spec S3, S7) and handed to the writer.</summary>
    private readonly IReadOnlyList<byte[]> _blockHashes;

    /// <summary>Set when a <see cref="ReadOnlyMemory{T}"/> overload wrapped the bytes: the session then disposes the source (spec 4.1).</summary>
    private bool _ownsFile;

    private readonly SessionOptions _options;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    // Captured once at StartAsync, from the same estimate the budget and balance checks used, and
    // reused for the report so its numbers cannot drift against the ones the run was approved
    // against. The fee is the one the strategy was checked to agree with; the mints are what the
    // estimate priced a ticketed strategy to pay on top of the memos.
    private long _feeDropsPerTransaction;

    /// <summary>The estimate's mints, used on a resume.</summary>
    private int _mints;

    /// <summary>The finished publication a resume found in the ledger (spec B27), or null.</summary>
    private PublishResult? _alreadyPublished;

    /// <summary>The Batch estimate the run was approved against, or null for a one-account run.</summary>
    private BatchPublicationEstimate? _batchEstimate;

    /// <summary>The strategy; a session resumed onto a publication that already finished has none (spec B27).</summary>
    private ISubmissionStrategy Strategy =>
        _strategy ?? throw new InvalidOperationException("this session finished a publication without submitting and has no strategy");

    /// <summary>
    /// What the strategy had minted when a fresh run was started, or null on a resume (spec I18).
    /// </summary>
    private readonly int? _mintsAtStart;

    /// <summary>Guards the state and its file: progress arrives from the thread pool.</summary>
    private readonly Lock _stateLock = new();

    private PublicationState _state;
    private Exception? _stateSaveFailure;

    /// <summary>0 before the run, 1 while it is running, 2 once it is over either way.</summary>
    private int _run;

    /// <summary>
    /// What <see cref="ResumeAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, SessionOptions, CancellationToken)"/> found already in the ledger: chunks and block lists, each by
    /// its own index. Null for a run that started fresh through <see cref="StartAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, string, string, string, string, SessionOptions, CancellationToken)"/> -- there
    /// is nothing to reconcile against on a first run.
    /// </summary>
    private LandedMemos? _landed;

    private PublicationSession(
        ISubmissionStrategy? strategy, ILedgerSource source, IPublicationSource file, IReadOnlyList<byte[]> blockHashes,
        PublicationState state, string statePath, SessionOptions options, long feeDropsPerTransaction, int mints,
        int? mintsAtStart)
    {
        _strategy = strategy;
        _source = source;
        _file = file;
        _blockHashes = blockHashes;
        _state = state;
        _options = options;
        _feeDropsPerTransaction = feeDropsPerTransaction;
        _mints = mints;
        _mintsAtStart = mintsAtStart;
        StatePath = statePath;
    }

    public string StatePath { get; }

    public PublicationState State
    {
        get { lock (_stateLock) return _state; }
    }

    /// <summary>Why the state file could not last be written, or null. Also on the report.</summary>
    public Exception? StateSaveFailure
    {
        get { lock (_stateLock) return _stateSaveFailure; }
    }

    /// <summary>What the run would cost, without creating anything.</summary>
    public static Task<PublicationEstimate> EstimateAsync(
        ILedgerSource source, string account, long fileBytes, SessionOptions options, CancellationToken cancellationToken) =>
        PublicationEstimator.EstimateAsync(source, account, fileBytes, options.TicketsPerBatch, cancellationToken);

    /// <summary>
    /// What a Batch run would cost each account (spec 4.5), refusing first a network that does not
    /// enable <c>BatchV1_1</c>, then an open-ledger fee above 200 drops. The amendment is read again
    /// here, before every run: this read decides, not a cached one (spec B14).
    /// </summary>
    public static async Task<BatchPublicationEstimate> EstimateAsync(
        ILedgerSource source, string writer, string payer, long fileBytes, SessionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        await RequireBatchEnabledAsync(source, cancellationToken).ConfigureAwait(false);

        BatchPublicationEstimate estimate = await PublicationEstimator
            .EstimateBatchAsync(source, writer, payer, fileBytes, cancellationToken).ConfigureAwait(false);

        RequireFeeWithinLimit(estimate);

        return estimate;
    }

    /// <summary>
    /// Starts a Batch run (spec 4.5): estimates, refuses in the spec's order, builds the strategy
    /// through <paramref name="strategyFactory"/> with this session as its fence recorder, and writes
    /// a version-3 state file -- all before a single transaction is submitted.
    /// </summary>
    public static Task<PublicationSession> StartAsync(
        BatchStrategyFactory strategyFactory, ILedgerSource source, ReadOnlyMemory<byte> file, string type, string? name,
        string writer, string keyReference, string payer, string payerKeyReference, string network, SessionOptions options,
        CancellationToken cancellationToken) =>
        OwningAsync(file, owned => StartAsync(
            strategyFactory, source, owned, type, name, writer, keyReference, payer, payerKeyReference, network, options, null, cancellationToken));

    /// <summary>
    /// Starts a Batch run with the file read from a source (spec 4.3), refusing in the order
    /// <see cref="StartAsync(BatchStrategyFactory, ILedgerSource, ReadOnlyMemory{byte}, string, string, string, string, string, string, string, SessionOptions, CancellationToken)"/>
    /// does. The source is borrowed: the session reads it and the caller disposes it. Its root is
    /// streamed once the estimate has been accepted, reported as the <c>"hashing"</c> stage of
    /// <paramref name="preparation"/>.
    /// </summary>
    public static async Task<PublicationSession> StartAsync(
        BatchStrategyFactory strategyFactory, ILedgerSource source, IPublicationSource file, string type, string? name,
        string writer, string keyReference, string payer, string payerKeyReference, string network, SessionOptions options,
        IProgress<PublishProgress>? preparation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(strategyFactory);
        ArgumentNullException.ThrowIfNull(options);
        RequirePublishable(file);
        RequireNameableAccount(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(payerKeyReference);

        if (string.Equals(payer, writer, StringComparison.Ordinal))
        {
            throw new ArgumentException("a Batch run's payer is another account than its writer (spec B1)", nameof(payer));
        }

        BatchPublicationEstimate estimate = await EstimateAsync(source, writer, payer, file.Length, options, cancellationToken).ConfigureAwait(false);
        PublicationEstimator.RequireBatch(estimate, options.BudgetDrops);

        // The ledger and the writer's sequence in it come from one snapshot (spec B6): together they
        // are where a resume's history has to start being complete (spec 5.2 step 3).
        LedgerSnapshot started = await source.SnapshotAsync([writer], cancellationToken).ConfigureAwait(false);
        long startedIn = started.LedgerIndex;
        IReadOnlyList<byte[]> blocks = await HashAsync(file, preparation, cancellationToken).ConfigureAwait(false);

        PublicationState state = new(
            PublicationState.BatchVersion, network, writer, keyReference, type, name,
            file.Length, MemoLayout.ChunkBytes, FileTree.RootHexOf(blocks), startedIn, Phase: Stages[0],
            Strategy: PublicationState.BatchStrategyName, SourcePath: options.SourcePath,
            Payer: payer, PayerKeyReference: payerKeyReference, Fence: 0, StartSequence: started.SequenceOf(writer));

        string path = NewStatePath(options, startedIn, writer);
        PublicationSession session = new(null, source, file, blocks, state, path, options, 0, 0, mintsAtStart: 0);

        await session.AttachBatchAsync(strategyFactory, estimate, resuming: false).ConfigureAwait(false);
        state.Save(path);

        return session;
    }

    /// <summary>The root a file will publish under: over block hashes alone, as the writer computes it.</summary>
    public static string RootOf(ReadOnlyMemory<byte> file) =>
        FileTree.RootHexOf(FileTree.BlockHashes(FileTree.Split(file, MemoLayout.ChunkBytes)));

    /// <summary>
    /// The largest file a publication takes (spec S1): 16 GiB. Every chunk index stays an
    /// <see cref="int"/>, and per-chunk tracking is about 19 MB at this size.
    /// </summary>
    public const long MaxPublicationBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>The root a source will publish under, in one streaming pass (spec S3, S7).</summary>
    public static async Task<string> RootOfAsync(IPublicationSource file, IProgress<long>? progress, CancellationToken cancellationToken) =>
        FileTree.RootHexOf(await FileTree.BlockHashesAsync(file, progress, cancellationToken).ConfigureAwait(false));

    /// <summary>An empty file has no root, and a file above <see cref="MaxPublicationBytes"/> is refused before anything is read (spec 4.1).</summary>
    private static void RequirePublishable(IPublicationSource file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Length <= 0) throw new ArgumentException("an empty file has no chunks and no root", nameof(file));

        if (file.Length > MaxPublicationBytes)
        {
            throw new XrplFileException(XrplFileError.TooLarge,
                $"the file is {file.Length} bytes and a publication takes at most {MaxPublicationBytes}");
        }
    }

    /// <summary>The file's block hashes in one streaming pass, reported as the "hashing" stage in chunks (spec 5.3).</summary>
    private static Task<IReadOnlyList<byte[]>> HashAsync(IPublicationSource file, IProgress<PublishProgress>? preparation, CancellationToken cancellationToken)
    {
        long chunks = ChunkReads.ChunkCount(file.Length);
        IProgress<long>? hashed = preparation is null ? null : new Hashed(done => preparation.Report(new PublishProgress("hashing", done, chunks)));

        return FileTree.BlockHashesAsync(file, hashed, cancellationToken);
    }

    /// <summary>Reports on the hashing thread, like the writer's own adapter, rather than posting through <see cref="Progress{T}"/>.</summary>
    private sealed class Hashed(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    /// <summary>Wraps bytes for the <see cref="ReadOnlyMemory{T}"/> overloads; the session that results owns it.</summary>
    private static async Task<PublicationSession> OwningAsync(ReadOnlyMemory<byte> bytes, Func<IPublicationSource, Task<PublicationSession>> start)
    {
        MemoryPublicationSource owned = new(bytes);

        try
        {
            PublicationSession session = await start(owned).ConfigureAwait(false);
            session._ownsFile = true;
            return session;
        }
        catch
        {
            await owned.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A BatchStrategy needs the session's own fence recorder and a version-3 state; the one-account
    /// overloads give it neither, so it can only come through the factory overloads (spec B16, Spec question 12).
    /// </summary>
    private static void RequireNotBatch(ISubmissionStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        if (strategy is BatchStrategy)
        {
            throw new ArgumentException(
                "a BatchStrategy is built by the session, with the session as its fence; start it with a BatchStrategyFactory",
                nameof(strategy));
        }
    }

    private static async Task RequireBatchEnabledAsync(ILedgerSource source, CancellationToken cancellationToken)
    {
        if (!await source.IsAmendmentEnabledAsync(Amendments.Batch, cancellationToken).ConfigureAwait(false))
        {
            throw new XrplFileException(XrplFileError.BatchNotEnabled,
                $"this network's validated ledger does not enable {Amendments.Batch}");
        }
    }

    private static void RequireFeeWithinLimit(BatchPublicationEstimate estimate)
    {
        if (estimate.OpenLedgerFeeDrops > BatchFees.MaxOpenLedgerFeeDrops)
        {
            throw new XrplFileException(XrplFileError.FeeTooHigh,
                $"the open-ledger fee is {estimate.OpenLedgerFeeDrops} drops, above the {BatchFees.MaxOpenLedgerFeeDrops} a run starts at");
        }
    }

    /// <summary>
    /// Builds the run's strategy with this session as its fence recorder, and holds it to the state:
    /// the same writer, the same payer, and the estimate's unit (spec 4.1).
    /// </summary>
    private async Task AttachBatchAsync(BatchStrategyFactory strategyFactory, BatchPublicationEstimate estimate, bool resuming)
    {
        BatchBudget budget = new(
            estimate.FeeUnitDrops, estimate.FeeCeilingDrops, estimate.PayerFeeDropsWorstCase, estimate.WriterFeeDropsWorstCase);

        BatchStrategy strategy = strategyFactory(budget, this);

        Exception? refusal = null;

        if (!string.Equals(strategy.Account, _state.Writer, StringComparison.Ordinal)
            || !string.Equals(strategy.Payer, _state.Payer, StringComparison.Ordinal))
        {
            string message = $"the run is recorded for writer {_state.Writer} and payer {_state.Payer}, and the strategy signs as "
                + $"{strategy.Account} and {strategy.Payer}";

            refusal = resuming
                ? new XrplFileException(XrplFileError.ResumeMismatch, message)
                : new ArgumentException(message, nameof(strategyFactory));
        }
        else if (strategy.FeeDropsPerTransaction != estimate.FeeUnitDrops)
        {
            refusal = new XrplFileException(XrplFileError.FeeMismatch,
                $"the estimate approved a unit of {estimate.FeeUnitDrops} drops and the strategy charges {strategy.FeeDropsPerTransaction}");
        }

        if (refusal is not null)
        {
            // The factory built it, so nobody else will release what its signers hold.
            await strategy.DisposeAsync().ConfigureAwait(false);
            throw refusal;
        }

        _strategy = strategy;
        _batchEstimate = estimate;
        _feeDropsPerTransaction = estimate.FeeUnitDrops;
    }

    /// <summary>
    /// Persists the fence (spec B23) before the transmission it covers is first sent. A save that
    /// fails throws, which stops that transmission and fails the run.
    /// </summary>
    Task IBatchFenceRecorder.RecordFenceAsync(long deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            if (_state.Fence is { } recorded && recorded >= deadline) return Task.CompletedTask;

            PublicationState next = _state with { Fence = deadline };
            next.Save(StatePath);
            _state = next;
        }

        return Task.CompletedTask;
    }

    /// <summary>One run, one file: a ledger-and-writer name with a random suffix (see StartAsync's remarks).</summary>
    private static string NewStatePath(SessionOptions options, long startedIn, string writer)
    {
        string suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return Path.Combine(options.StateDirectory, $"{startedIn}-{writer}-{suffix}.json");
    }

    /// <summary>The name a version-2 state records for the strategies this library ships; null for anything else.</summary>
    private static string? StrategyName(ISubmissionStrategy strategy) => strategy switch
    {
        TicketStrategy => PublicationState.TicketStrategyName,
        SequenceStrategy => PublicationState.SequenceStrategyName,
        _ => null,
    };

    /// <summary>
    /// Estimates, refuses what cannot be afforded or what the strategy would price differently,
    /// and writes the state file — all before a single transaction is submitted. A run that is
    /// going to fail on money fails here, where it has cost nothing.
    /// </summary>
    public static Task<PublicationSession> StartAsync(
        ISubmissionStrategy strategy, ILedgerSource source, ReadOnlyMemory<byte> file, string type, string? name,
        string writer, string keyReference, string network, SessionOptions options, CancellationToken cancellationToken) =>
        OwningAsync(file, owned => StartAsync(strategy, source, owned, type, name, writer, keyReference, network, options, null, cancellationToken));

    /// <summary>
    /// Starts a run with the file read from a source (spec 4.3), refusing in the order
    /// <see cref="StartAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, string, string, string, string, SessionOptions, CancellationToken)"/>
    /// does. The source is borrowed: the session reads it and the caller disposes it. Its root is
    /// streamed once, after every refusal that needs no read, and reported as the <c>"hashing"</c>
    /// stage of <paramref name="preparation"/>.
    /// </summary>
    public static async Task<PublicationSession> StartAsync(
        ISubmissionStrategy strategy, ILedgerSource source, IPublicationSource file, string type, string? name,
        string writer, string keyReference, string network, SessionOptions options, IProgress<PublishProgress>? preparation,
        CancellationToken cancellationToken)
    {
        RequireNotBatch(strategy);
        RequirePublishable(file);

        // The writer becomes part of a file name below, so it is checked before anything at all
        // happens. An XRPL account is base58 and cannot hold a separator or a dot-dot; something
        // that can is not an account, and must not get to choose where the state file lands.
        RequireNameableAccount(writer);

        // The writer the run is recorded under and the account that will sign every transaction
        // must be the same one, or the state file, the report and the manifest all name an account
        // that published nothing. Here it is a caller's mistake, visible without asking the node
        // anything, so it is refused as one.
        if (!string.Equals(strategy.Account, writer, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"the run is to be recorded for {writer} and the strategy signs as {strategy.Account}; "
                + "one publication has one writer",
                nameof(writer));
        }

        PublicationEstimate estimate = await PublicationEstimator
            .EstimateAsync(source, writer, file.Length, options.TicketsPerBatch, cancellationToken).ConfigureAwait(false);

        // Before the budget and balance checks, because it decides whether those checks are about
        // this run at all. See the remarks on SessionOptions.
        if (strategy.FeeDropsPerTransaction != estimate.FeeDropsPerTransaction)
        {
            throw new XrplFileException(XrplFileError.FeeMismatch,
                $"the estimate prices every transaction at {estimate.FeeDropsPerTransaction} drops and the strategy "
                + $"charges {strategy.FeeDropsPerTransaction}; build the strategy from the estimate's fee, or the "
                + "budget and balance checks describe a run nobody is going to submit");
        }

        // The slot before the balance: a run with nowhere to mint cannot go however much the
        // account holds, and a balance refusal would send the person to top it up for nothing.
        if (strategy is TicketStrategy) PublicationEstimator.RequireTicketSlot(estimate);
        PublicationEstimator.Require(estimate, options.BudgetDrops);

        long startedIn = await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        // Computed now, from the bytes alone, rather than left for the manifest to fill in later:
        // it is what a resume checks a second file against, and a run that dies before its first
        // transaction still needs an identity to be resumed by. The root is over block hashes
        // only (see RootOf), so it is the same root the manifest will carry once the real
        // per-block ledgers are known.
        IReadOnlyList<byte[]> blocks = await HashAsync(file, preparation, cancellationToken).ConfigureAwait(false);
        string root = FileTree.RootHexOf(blocks);

        PublicationState state = new(
            PublicationState.CurrentVersion, network, writer, keyReference, type, name,
            file.Length, MemoLayout.ChunkBytes, root, startedIn, Phase: Stages[0],
            Strategy: StrategyName(strategy), SourcePath: options.SourcePath);

        // One run, one file, and no two runs share one. Two publications from one account inside
        // one ledger — four seconds, ordinary in a batch loop — would otherwise agree on a name,
        // and the second would overwrite the only record of money the first had already spent.
        // The root is now known up front, but a hex root is an unfriendly file name and offers no
        // ordering, so the run is still told apart by a random suffix rather than by its identity.
        string path = NewStatePath(options, startedIn, writer);
        state.Save(path);

        return new PublicationSession(
            strategy, source, file, blocks, state, path, options, estimate.FeeDropsPerTransaction, estimate.Mints,
            mintsAtStart: strategy.TicketMints);
    }

    /// <summary>
    /// Picks a run back up from its state file.
    ///
    /// The file is where to look, not what to believe. A crash can fall between a submission and
    /// its record, so the ledger is read from the run's starting point and whatever is actually
    /// there is what counts. The file's own map of hashes carries the report through the
    /// interruption; it does not decide what still needs sending.
    ///
    /// The ledger is reconciled first, and a manifest the dead run already sent is looked for
    /// before anything is priced (spec B27, 5.2): when it is found nothing is sent, so nothing has
    /// to be affordable and a drained account still finishes the run.
    ///
    /// A resumed run that does have something left to send still spends money, so it is checked
    /// exactly as a fresh one is: a fresh estimate is priced against the strategy's fee, and the
    /// caller's budget and the account's balance are checked against it, before anything is sent.
    /// That estimate is for the <em>whole file</em>, not for the part still missing — the chunks
    /// already in the ledger are priced again, and their tickets with them. A budget sized for the
    /// original run therefore carries the resume as well; one sized for the remainder refuses it.
    /// </summary>
    public static Task<PublicationSession> ResumeAsync(
        ISubmissionStrategy strategy, ILedgerSource source, ReadOnlyMemory<byte> file,
        string statePath, SessionOptions options, CancellationToken cancellationToken) =>
        OwningAsync(file, owned => ResumeAsync(strategy, source, owned, statePath, options, null, cancellationToken));

    /// <summary>
    /// Picks a run back up with the file read from a source (spec 4.3), as
    /// <see cref="ResumeAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, SessionOptions, CancellationToken)"/>
    /// does. The source is borrowed: the session reads it and the caller disposes it. Its root is
    /// streamed before the ledger is read, reported as the <c>"hashing"</c> stage of
    /// <paramref name="preparation"/>.
    /// </summary>
    public static async Task<PublicationSession> ResumeAsync(
        ISubmissionStrategy strategy, ILedgerSource source, IPublicationSource file,
        string statePath, SessionOptions options, IProgress<PublishProgress>? preparation, CancellationToken cancellationToken)
    {
        RequireNotBatch(strategy);
        ArgumentNullException.ThrowIfNull(file);

        PublicationState state = PublicationState.Load(statePath);

        // Before anything else, because everything else is about this account: the sweep reads the
        // recorded writer's history, and a strategy signing as somebody else would submit the rest
        // of the file from an account the manifest and the report never name.
        if (!string.Equals(strategy.Account, state.Writer, StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} records {state.Writer} as the writer and the strategy signs as {strategy.Account}; "
                + "a publication cannot change accounts half-way");
        }

        // A Batch run resumes with its payer, on its own overload (spec B16).
        if (state.Version == PublicationState.BatchVersion)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} records a Batch run, which resumes with its payer rather than on one account");
        }

        IReadOnlyList<byte[]> blocks = await RequireResumableAsync(state, statePath, file, preparation, cancellationToken).ConfigureAwait(false);

        PublicationSession session = new(
            strategy, source, file, blocks, state, statePath, options, 0, 0, mintsAtStart: null);
        session.RecordSourcePath(options.SourcePath);
        long to = await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LedgerTransaction> history = await session.HistoryAsync(to, cancellationToken).ConfigureAwait(false);
        session._landed = await session.ReconcileAsync(history, conflictsFail: false, cancellationToken).ConfigureAwait(false);

        // Nothing is sent, so nothing has to be affordable (spec B27).
        if (session.FindManifest(session._landed, history) is { } done)
        {
            session._alreadyPublished = done;
            return session;
        }

        PublicationEstimate estimate = await PublicationEstimator
            .EstimateAsync(source, state.Writer, file.Length, options.TicketsPerBatch, cancellationToken).ConfigureAwait(false);

        // Same rule StartAsync holds: the estimate this run is approved against and the strategy
        // that would submit it must price a transaction the same way, or the checks below describe
        // a run nobody is going to submit.
        if (strategy.FeeDropsPerTransaction != estimate.FeeDropsPerTransaction)
        {
            throw new XrplFileException(XrplFileError.FeeMismatch,
                $"the estimate prices every transaction at {estimate.FeeDropsPerTransaction} drops and the strategy "
                + $"charges {strategy.FeeDropsPerTransaction}; build the strategy from the estimate's fee, or the "
                + "budget and balance checks describe a run nobody is going to submit");
        }

        if (strategy is TicketStrategy) PublicationEstimator.RequireTicketSlot(estimate);
        PublicationEstimator.Require(estimate, options.BudgetDrops);

        session._feeDropsPerTransaction = estimate.FeeDropsPerTransaction;
        session._mints = estimate.Mints;

        return session;
    }

    /// <summary>
    /// Picks a Batch run back up (spec 5.2): checks the file, waits until a snapshot is past the
    /// recorded fence plus the settle ledgers, reconciles, looks for a manifest already sent, writes
    /// a new payer to the state if one is given, prices what is still missing as a new session, and
    /// only then builds the strategy. Nothing the interrupted run sent can apply once the fence has
    /// passed, so the ledger's answer read after it is final.
    /// </summary>
    public static Task<PublicationSession> ResumeAsync(
        BatchStrategyFactory strategyFactory, ILedgerSource source, ReadOnlyMemory<byte> file, string statePath,
        string payer, string payerKeyReference, SessionOptions options, IProgress<PublishProgress>? fenceProgress,
        CancellationToken cancellationToken) =>
        OwningAsync(file, owned => ResumeAsync(
            strategyFactory, source, owned, statePath, payer, payerKeyReference, options, fenceProgress, cancellationToken));

    /// <summary>
    /// Picks a Batch run back up with the file read from a source (spec 4.3). The source is
    /// borrowed: the session reads it and the caller disposes it. Its root is streamed first,
    /// reported as the <c>"hashing"</c> stage of <paramref name="preparation"/>, which also hears
    /// the <c>"fence"</c> wait.
    /// </summary>
    public static async Task<PublicationSession> ResumeAsync(
        BatchStrategyFactory strategyFactory, ILedgerSource source, IPublicationSource file, string statePath,
        string payer, string payerKeyReference, SessionOptions options, IProgress<PublishProgress>? preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(strategyFactory);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(payerKeyReference);

        PublicationState state = PublicationState.Load(statePath);

        if (state.Version != PublicationState.BatchVersion)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} records a one-account run, which resumes on the strategy it recorded rather than as a Batch run");
        }

        if (string.Equals(payer, state.Writer, StringComparison.Ordinal))
        {
            throw new ArgumentException("a Batch run's payer is another account than its writer (spec B1)", nameof(payer));
        }

        IReadOnlyList<byte[]> blocks = await RequireResumableAsync(state, statePath, file, preparation, cancellationToken).ConfigureAwait(false);

        await WaitForFenceAsync(source, state, options, preparation, cancellationToken).ConfigureAwait(false);

        PublicationSession session = new(null, source, file, blocks, state, statePath, options, 0, 0, mintsAtStart: 0);
        session.RecordSourcePath(options.SourcePath);

        // One complete read serves both: a manifest search over a second read could miss what the
        // reconciliation saw, and send a second manifest (spec B27).
        IReadOnlyList<LedgerTransaction> history = await session.CompleteHistoryAsync(cancellationToken).ConfigureAwait(false);
        session._landed = await session.ReconcileAsync(history, conflictsFail: true, cancellationToken).ConfigureAwait(false);

        if (session.FindManifest(session._landed, history) is { } done)
        {
            session._alreadyPublished = done;
            return session;
        }

        // Behind the fence no outer of the old payer can still apply, which is what makes changing it safe (spec B18).
        session.ChangePayer(payer, payerKeyReference);

        await RequireBatchEnabledAsync(source, cancellationToken).ConfigureAwait(false);

        int blockCount = blocks.Count;
        int lists = blockCount > 1 ? PublicationEstimator.BlockListMemoCount(blockCount) : 0;
        long missingChunks = ChunkReads.ChunkCount(file.Length) - session._landed.Chunks.Landed;

        // Lists are priced in full: their bytes depend on ledgers only the writer knows by now, and
        // there are few of them.
        BatchPublicationEstimate estimate = await PublicationEstimator
            .EstimateBatchAsync(source, state.Writer, payer, missingChunks, blockCount, lists, cancellationToken).ConfigureAwait(false);

        RequireFeeWithinLimit(estimate);
        PublicationEstimator.RequireBatch(estimate, options.BudgetDrops);

        await session.AttachBatchAsync(strategyFactory, estimate, resuming: true).ConfigureAwait(false);

        return session;
    }

    /// <summary>
    /// The fence keeps the two settle ledgers spec B23 names: a round no longer waits for them (B9 as
    /// amended 2026-10-21), and a resume, which is rare, keeps its margin.
    /// </summary>
    private const int FenceSettleLedgers = 2;

    /// <summary>
    /// Waits until a snapshot is past the fence plus <see cref="FenceSettleLedgers"/> (spec B23), reporting the wait
    /// as the "fence" stage. A run that never transmitted has a fence of zero and waits for nothing.
    /// Reads are retried, and a node that stays unreachable, or whose validated ledger stays where it
    /// is, for <see cref="SessionOptions.NodeOutageLimit"/> fails the resume (Spec question 4).
    /// </summary>
    private static async Task WaitForFenceAsync(
        ILedgerSource source, PublicationState state, SessionOptions options, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        if (state.Fence is not { } fence || fence <= 0) return;

        BatchStrategyOptions reads = new() { ChainCheck = options.FencePoll, NodeOutageLimit = options.NodeOutageLimit };
        long target = fence + FenceSettleLedgers;
        long? first = null;
        long highest = 0;
        DateTimeOffset advancedAt = DateTimeOffset.UtcNow;

        while (true)
        {
            LedgerSnapshot snapshot = await BatchReads.RetryAsync(
                token => source.SnapshotAsync([state.Writer], token), reads, cancellationToken).ConfigureAwait(false);

            if (snapshot.LedgerIndex > target) return;

            DateTimeOffset now = DateTimeOffset.UtcNow;

            if (snapshot.LedgerIndex > highest)
            {
                highest = snapshot.LedgerIndex;
                advancedAt = now;
            }
            else if (now - advancedAt >= options.NodeOutageLimit)
            {
                throw new XrplFileException(XrplFileError.NodeUnreachable,
                    $"the node's validated ledger has stayed at {snapshot.LedgerIndex} for {options.NodeOutageLimit}, short of the fence at {fence}");
            }

            first ??= snapshot.LedgerIndex;
            progress?.Report(new PublishProgress("fence", snapshot.LedgerIndex - first.Value, target + 1 - first.Value));

            await Task.Delay(options.FencePoll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the payer a resume runs with to the state before anything is sent (spec 5.2 step 5).</summary>
    private void ChangePayer(string payer, string payerKeyReference)
    {
        lock (_stateLock)
        {
            if (string.Equals(_state.Payer, payer, StringComparison.Ordinal)
                && string.Equals(_state.PayerKeyReference, payerKeyReference, StringComparison.Ordinal))
            {
                return;
            }

            _state = _state with { Payer = payer, PayerKeyReference = payerKeyReference };
            _state.Save(StatePath);
        }
    }

    /// <summary>
    /// The manifest this run would send, if the ledger already holds it (spec B27): when every chunk
    /// and list has landed, the manifest is built from the reconciled hashes and ledgers, and a
    /// validated tesSUCCESS transaction of the writer in <paramref name="history"/> carrying exactly
    /// those bytes is the one. A manifest with the same root and other bytes is another publication's
    /// and is not adopted.
    /// </summary>
    private PublishResult? FindManifest(LandedMemos landed, IReadOnlyList<LedgerTransaction> history)
    {
        if (!landed.Chunks.IsComplete) return null;

        IReadOnlyList<BlockEntry> entries = ManifestPlan.Entries(_blockHashes, landed.Chunks.BlockFirstLedgers());
        IReadOnlyList<MemoSubmission> listMemos = ManifestPlan.ListMemos(entries);
        LedgerRange window = landed.Chunks.Window();
        long lastLedger = window.Last;

        for (int j = 0; j < listMemos.Count; j++)
        {
            if (!landed.Lists.TryGetValue(j, out LandedMemo? list)
                || !string.Equals(list.MemoDataHex, listMemos[j].MemoDataHex, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            lastLedger = Math.Max(lastLedger, list.LedgerIndex);
        }

        PublicationState state = State;
        Manifest manifest = ManifestPlan.Build(state.Type, state.Name, state.Size, entries, window.First, lastLedger, listMemos.Count);
        string data = ManifestPlan.Memo(manifest).MemoDataHex;

        foreach (LedgerTransaction tx in history)
        {
            if (!tx.Validated || !tx.Succeeded || !string.Equals(tx.Account, state.Writer, StringComparison.Ordinal)) continue;
            if (!MemoLayout.IsManifest(tx.MemoTypeHex) || !string.Equals(tx.MemoDataHex, data, StringComparison.OrdinalIgnoreCase)) continue;

            return new PublishResult(new XrplFileLink(state.Network, tx.Hash), manifest, state.Writer, landed.Chunks.ChunkCount, listMemos.Count, tx.Hash);
        }

        return null;
    }

    /// <summary>The checks every resume makes before it reads the ledger; returns the file's block hashes (spec S7).</summary>
    private static async Task<IReadOnlyList<byte[]>> RequireResumableAsync(
        PublicationState state, string statePath, IPublicationSource file, IProgress<PublishProgress>? preparation, CancellationToken cancellationToken)
    {
        if (string.Equals(state.Failure, PublicationState.WriterWindowConflictFailure, StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.WriterWindowConflict,
                $"{statePath} was stopped because the writer carries other data for this file's chunks; it is not resumed");
        }

        if (string.Equals(state.Phase, "done", StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} describes a run that finished; publishing it again would put a second copy in the ledger");
        }

        // A recorded size is always above zero and within the publication limit, so this one check
        // also refuses an emptied file and one grown past the limit, as a file that changed (spec S7).
        if (state.Size != file.Length)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} was started for {state.Size} bytes and this file is {file.Length}")
            {
                SourceChanged = true,
            };
        }

        // The run's indices mean nothing under a different chunk size: PublishCoreAsync always
        // re-splits at the current constant, so an old run's chunk 7 would be matched against
        // bytes that are no longer chunk 7's.
        if (state.Chunk != MemoLayout.ChunkBytes)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} was started at {state.Chunk} bytes per chunk and this library writes {MemoLayout.ChunkBytes}");
        }

        // The size matching is not enough: two different files of one size are ordinary. The root
        // is the identity, so it is what the check is made of -- and a file that does not carry one
        // is refused rather than resumed without it, because skipping the check is exactly how a
        // resume publishes the wrong bytes under a started run's name.
        if (state.Root is not { } recorded)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} records no root, so there is nothing to check this file against");
        }

        IReadOnlyList<byte[]> blocks = await HashAsync(file, preparation, cancellationToken).ConfigureAwait(false);
        string root = FileTree.RootHexOf(blocks);

        if (!string.Equals(recorded, root, StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} was started for a file rooted at {recorded} and this one roots at {root}")
            {
                SourceChanged = true,
            };
        }

        return blocks;
    }

    /// <summary>Writes a failure that must outlive this session (spec S17) before it is thrown.</summary>
    private void RecordFailure(string failure)
    {
        lock (_stateLock)
        {
            if (string.Equals(_state.Failure, failure, StringComparison.Ordinal)) return;

            _state = _state with { Failure = failure };
            _state.Save(StatePath);
        }
    }

    /// <summary>A picked file at a new place is where the next resume reads from (spec B17).</summary>
    private void RecordSourcePath(string? sourcePath)
    {
        if (sourcePath is null) return;

        lock (_stateLock)
        {
            if (string.Equals(_state.SourcePath, sourcePath, StringComparison.Ordinal)) return;

            _state = _state with { SourcePath = sourcePath };
            _state.Save(StatePath);
        }
    }

    /// <summary>
    /// Which of this publication's memos are already in the ledger, read from the writer's
    /// <paramref name="history"/> since the run started.
    ///
    /// The sweep starts at the ledger the run started in, which is why that number is in the
    /// state file: without it this would read the account's whole history.
    ///
    /// Starting there excludes the account's earlier publications. It does not exclude a later
    /// one — a marker names no publication, so <c>xf/c/0</c> written by the next file up is
    /// <c>xf/c/0</c> here too — so a chunk is adopted only when its bytes are the bytes this run
    /// would have sent. The block lists cannot be checked that way here, because their payloads
    /// carry per-block ledgers that are not known until the chunks are reconciled; they are
    /// collected raw and checked byte for byte by the writer, which is where they are built.
    ///
    /// With <paramref name="conflictsFail"/> (a Batch resume, spec S17) a validated, successful
    /// writer record carrying one of this file's chunk indices with other bytes ends the
    /// reconciliation with <see cref="XrplFileError.WriterWindowConflict"/>, recorded on the state.
    /// A one-account run ignores such a record as a straggler (spec I12).
    /// </summary>
    private async Task<LandedMemos> ReconcileAsync(
        IReadOnlyList<LedgerTransaction> history, bool conflictsFail, CancellationToken cancellationToken)
    {
        long chunkCount = ChunkReads.ChunkCount(_file.Length);
        ChunkLanding landing = new(chunkCount);
        Dictionary<int, LandedMemo> landedLists = [];
        byte[] buffer = new byte[MemoLayout.ChunkBytes];

        foreach (LedgerTransaction tx in history)
        {
            if (!tx.Succeeded || !tx.Validated) continue;
            if (!string.Equals(tx.Account, _state.Writer, StringComparison.Ordinal)) continue;
            if (tx.MemoDataHex is not { } data) continue;

            if (MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is { } chunkIndex)
            {
                if (chunkIndex < 0 || chunkIndex >= chunkCount) continue;

                // A one-account run stops at the first copy it adopted; a Batch run keeps reading
                // the later copies of an adopted chunk, because a different one is a conflict.
                if (landing.IsLanded(chunkIndex) && !conflictsFail) continue;

                int length = await _file.ReadChunkAsync(chunkIndex, buffer, cancellationToken).ConfigureAwait(false);

                if (ChunkReads.MatchesHex(data, buffer.AsSpan(0, length)))
                {
                    // First in canonical order, exactly as a reader will resolve it.
                    if (!landing.IsLanded(chunkIndex)) landing.Land(chunkIndex, tx.LedgerIndex);
                }
                else if (conflictsFail)
                {
                    RecordFailure(PublicationState.WriterWindowConflictFailure);
                    throw new XrplFileException(XrplFileError.WriterWindowConflict,
                        $"the writer's {tx.Hash} in ledger {tx.LedgerIndex} carries chunk {chunkIndex} with other data than this file's");
                }

                // A one-account run ignores a copy with other bytes as a straggler (spec I12).
                continue;
            }

            if (MemoLayout.ListIndexOf(tx.MemoTypeHex) is { } listIndex)
            {
                if (listIndex < 0 || listIndex > int.MaxValue) continue;

                // Last in canonical order wins for a list, again exactly as the reader resolves
                // one -- it walks the range backwards and keeps the first copy it meets.
                landedLists[(int)listIndex] = new LandedMemo(tx.Hash, tx.LedgerIndex, data);
            }
        }

        return new LandedMemos(landing, landedLists);
    }

    /// <summary>The writer's history from <see cref="PublicationState.StartedInLedger"/> to <paramref name="to"/>, in canonical order.</summary>
    private Task<IReadOnlyList<LedgerTransaction>> HistoryAsync(long to, CancellationToken cancellationToken) =>
        BatchSweep.HistoryAsync(_source, _state.Writer, _state.StartedInLedger, to, cancellationToken);

    /// <summary>
    /// The writer's history up to a snapshot taken after the fence, once it is complete (spec 5.2
    /// step 3): its records cover the writer's sequence from <see cref="PublicationState.StartSequence"/>
    /// up to the snapshot's, counted as a round's final sweep counts them (spec B30). account_tx lags
    /// the validated ledger, so a memo that landed near its deadline can still be missing once the
    /// fence has passed; trusting that answer would pay for the memo again, or send a second manifest.
    /// The read is taken again every <see cref="SessionOptions.FencePoll"/> for up to
    /// <see cref="SessionOptions.HistoryLagLimit"/>, then the resume fails with
    /// <see cref="XrplFileError.NodeLacksHistory"/> having written nothing a later resume depends on.
    /// </summary>
    private async Task<IReadOnlyList<LedgerTransaction>> CompleteHistoryAsync(CancellationToken cancellationToken)
    {
        string writer = _state.Writer;
        BatchStrategyOptions reads = new() { ChainCheck = _options.FencePoll, NodeOutageLimit = _options.NodeOutageLimit };
        LedgerSnapshot snapshot = await BatchReads.RetryAsync(
            token => _source.SnapshotAsync([writer], token), reads, cancellationToken).ConfigureAwait(false);
        uint through = snapshot.SequenceOf(writer);
        DateTimeOffset giveUp = DateTimeOffset.UtcNow + _options.HistoryLagLimit;

        while (true)
        {
            IReadOnlyList<LedgerTransaction> history = await BatchReads.RetryAsync(
                token => HistoryAsync(snapshot.LedgerIndex, token), reads, cancellationToken).ConfigureAwait(false);
            List<LedgerTransaction> own = [.. history.Where(tx => string.Equals(tx.Account, writer, StringComparison.Ordinal))];

            // A state written before StartSequence was recorded has only its own records to start
            // from. That still catches a history that lags at its end, which is where it lags.
            uint from = _state.StartSequence
                ?? (own.Where(tx => tx.Sequence != 0).Select(tx => (uint?)tx.Sequence).Min() ?? through);

            if (BatchSweep.Covers(own, from, through)) return history;

            if (DateTimeOffset.UtcNow >= giveUp)
            {
                throw new XrplFileException(XrplFileError.NodeLacksHistory,
                    $"account_tx did not cover the writer's sequences from {from} up to {through} by ledger {snapshot.LedgerIndex} "
                    + $"within {_options.HistoryLagLimit}");
            }

            await Task.Delay(_options.FencePoll, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<PublicationReport> RunAsync(IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        // A publication is paid for once. A second call would republish every chunk and pay for it
        // again against a budget and a balance nobody re-checked — those checks live in StartAsync
        // — and two concurrent calls would race on the state and its file besides. So a session
        // that has run, or is running, refuses rather than spends.
        int already = Interlocked.CompareExchange(ref _run, 1, 0);

        if (already != 0)
        {
            throw new XrplFileException(XrplFileError.SessionAlreadyRun,
                already == 1
                    ? "this session is already running; a second run would submit the whole file again"
                    : "this session has already run; start another one rather than paying for the file twice");
        }

        try
        {
            if (_alreadyPublished is { } done)
            {
                // The dead run's manifest is in the ledger: record "done" and send nothing, the
                // tidy-up included (spec B27). The fees were spent by the sessions before this one.
                Advance("done", done.Manifest.Root, done.ManifestHash);

                return new PublicationReport(
                    done.Link, done.Manifest, done.Writer,
                    done.ChunkCount + done.ListCount + 1, 0,
                    _startedAt, DateTimeOffset.UtcNow, done.ChunkCount, done.ListCount, done.ManifestHash)
                {
                    StateSaveFailure = StateSaveFailure,
                    Payer = State.Payer,
                };
            }

            PublicationState started = State;

            XrplFileWriter writer = new(Strategy, _source, new WriterOptions { ReadBack = _options.ReadBack });

            PublishResult result = await writer.PublishAsync(
                _file, _blockHashes, started.Type, started.Name, started.Writer, started.Network, _landed,
                new PhaseRecordingProgress(this, progress), cancellationToken).ConfigureAwait(false);

            Advance("done", result.Manifest.Root, result.ManifestHash);

            // After "done" and never before: the manifest is in the ledger, so nothing the tidy-up
            // does — or fails to do — can cost the person their link (spec I8).
            (TicketRelease? release, Exception? releaseFailure) = await ReleaseLeftoverTicketsAsync(cancellationToken).ConfigureAwait(false);
            TicketRelease counts = release ?? TicketRelease.None;

            // A fresh run's session is its whole publication, so its strategy's own count is the
            // publication's; a resume's earlier sessions minted too, unrecorded (spec I18).
            int mints = _mintsAtStart is { } before ? Strategy.TicketMints - before : _mints;
            long transactions = result.ChunkCount + result.ListCount + 1 + mints;

            // The writer's fees, counted as today (spec 4.6): on the Batch path its solo transactions at
            // the approved unit, nothing retried; the payer's measured spend is PayerFeeDrops (Spec question 9).
            long feeEstimated = _batchEstimate is { } batch
                ? batch.WriterFeeDrops
                : transactions * _feeDropsPerTransaction;

            // Priced from the fee the estimate was approved against (see the remarks on
            // SessionOptions), not a fresh node query: the node's open-ledger fee moves with load,
            // and a second query here would report a cost the run was never checked against.
            return new PublicationReport(
                result.Link, result.Manifest, result.Writer,
                transactions, feeEstimated,
                _startedAt, DateTimeOffset.UtcNow,
                result.ChunkCount, result.ListCount, result.ManifestHash)
            {
                StateSaveFailure = StateSaveFailure,
                TicketsReleased = counts.Released,
                TicketsAlreadyGone = counts.AlreadyGone,
                TicketsNotReleased = counts.NotReleased,
                TicketReleaseFailure = releaseFailure,
                TicketReleaseFeeDrops = release is null ? null : release.Charged * _feeDropsPerTransaction,
                TicketReleaseFeeDropsInDoubt = release is null ? null : release.InDoubt * _feeDropsPerTransaction,
                Payer = started.Payer,
                PayerFeeDrops = (Strategy as BatchStrategy)?.PayerFeeDrops,
            };
        }
        finally
        {
            // A run that threw is over too. Its money is spent, and starting again from the top is
            // a decision for a caller who has re-checked the account, not for a retry loop.
            Interlocked.Exchange(ref _run, 2);

            if (_ownsFile) await _file.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Spends this library's leftover tickets under its own deadline, linked to the publication's
    /// token. A cancellation or the deadline ends it with the counts it reached; an exception is
    /// caught and returned. Neither fails the publication (spec I8). Null counts mean they were lost.
    /// </summary>
    private async Task<(TicketRelease? Release, Exception? Failure)> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource giveUp = new();
        using CancellationTokenRegistration graceStart = deadline.Token.Register(
            static state => ((CancellationTokenSource)state!).CancelAfter(TicketReleaseGrace), giveUp);
        Task<TicketRelease>? release = null;

        try
        {
            // Inside the try so a timeout CancelAfter refuses is recorded as the tidy-up's failure
            // rather than thrown after the manifest has landed (spec I8).
            deadline.CancelAfter(_options.TicketReleaseTimeout);

            release = Strategy.ReleaseLeftoverTicketsAsync(deadline.Token);

            // The deadline is the strategy's cue to stop and report what it settled; the wait is
            // only abandoned once the grace after it has run out too, so a strategy that honours the
            // token keeps its counts and one that ignores it still cannot hold the report back
            // (spec I8).
            return (await release.WaitAsync(giveUp.Token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (giveUp.IsCancellationRequested)
        {
            if (release is not null)
            {
                _ = release.ContinueWith(
                    static task => _ = task.Exception,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }

            return (null, new TimeoutException(
                $"the tidy-up did not stop within {Seconds(TicketReleaseGrace)} of its {Seconds(_options.TicketReleaseTimeout)} deadline; tickets it had not released stay on the account"));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return (null, null);
        }
        catch (Exception failure)
        {
            return (null, failure);
        }
    }

    /// <summary>A span as a figure a person reads in a message: "5 s", "0.1 s", "120 s" — not "00:00:05".</summary>
    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " s";

    /// <summary>
    /// Runs that were started and never finished. Money may be sitting on the accounts they name,
    /// and their state files are the only record of it — so a file this library cannot read is
    /// reported as an unreadable row rather than dropped. Dropping it is how a run written by a
    /// newer version, holding live tickets, disappears from the list whose whole purpose is
    /// finding money.
    /// </summary>
    public static IReadOnlyList<UnfinishedRun> Unfinished(string stateDirectory)
    {
        if (!Directory.Exists(stateDirectory)) return [];

        List<UnfinishedRun> unfinished = [];

        foreach (string path in Directory.EnumerateFiles(stateDirectory, "*.json"))
        {
            PublicationState state;

            try
            {
                state = PublicationState.Load(path);
            }
            catch (Exception e) when (e is FormatException or JsonException or IOException or UnauthorizedAccessException)
            {
                // Not readable is not the same as not there. A newer version's file, a corrupt
                // one, and one another process holds open all get the same line for the same
                // reason — and UnauthorizedAccessException is not an IOException, so a single
                // locked file used to abort the whole enumeration instead of skipping one row.
                unfinished.Add(new UnfinishedRun(path, null, e.Message));
                continue;
            }

            if (!string.Equals(state.Phase, "done", StringComparison.Ordinal))
            {
                unfinished.Add(new UnfinishedRun(path, state, null));
            }
        }

        return unfinished;
    }

    /// <summary>
    /// Moves the recorded phase forward and writes it down. Backwards is ignored rather than
    /// recorded: a late progress report from the thread pool must not tell a resumed run that a
    /// stage it has finished is where it stopped.
    ///
    /// <see cref="PublishProgress"/> carries a count and a stage, not a transaction hash. The
    /// stage is what a person interrupted half-way actually needs, and it is what this records.
    /// </summary>
    private void Advance(string phase, string? root, string? manifestHash = null)
    {
        lock (_stateLock)
        {
            if (RankOf(phase) <= RankOf(_state.Phase)) return;

            _state = _state with
            {
                Phase = phase,
                Root = root ?? _state.Root,
                ManifestHash = manifestHash ?? _state.ManifestHash,
            };

            try
            {
                _state.Save(StatePath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The state file is a record of money, not the money itself. By the time the last
                // save runs the transactions are paid for, and the report is the only thing that
                // names what they bought — so the failure is kept for the caller to see and the
                // run carries on rather than throwing the report away. The first failure is the
                // one kept: it is the one that explains what the file on disk now says.
                _stateSaveFailure ??= e;
            }
        }
    }

    /// <summary>Where a stage sits in the run's order, or -1 for one this class does not know.</summary>
    private static int RankOf(string stage) => Array.IndexOf(Stages, stage);

    /// <summary>
    /// The caller's progress with the state file wired in behind it: every stage the writer
    /// reports is on disk before the caller hears about it.
    /// </summary>
    private sealed class PhaseRecordingProgress(PublicationSession session, IProgress<PublishProgress>? inner)
        : IProgress<PublishProgress>
    {
        public void Report(PublishProgress value)
        {
            session.Advance(value.Stage, root: null);
            inner?.Report(value);
        }
    }

    /// <summary>
    /// An account that can be half of a file name and nothing else. XRPL classic addresses are
    /// base58, so this refuses nothing a real account would have offered.
    /// </summary>
    private static void RequireNameableAccount(string writer)
    {
        if (string.IsNullOrEmpty(writer) || writer.Length > 64 || !writer.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException(
                "an account is base58 and becomes part of a state file's name; this one could name a path instead",
                nameof(writer));
        }
    }
}
