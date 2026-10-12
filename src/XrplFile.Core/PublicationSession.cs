using System.Security.Cryptography;
using System.Text.Json;

namespace XrplFile;

/// <remarks>
/// The fee an estimate is priced at and the fee a strategy charges are two different numbers, and
/// nothing this class says about money is true unless they are the same one.
/// <see cref="SequenceStrategyOptions.FeeDrops"/> and <see cref="TicketStrategyOptions.FeeDrops"/>
/// both default to a hardcoded 12 drops, while <see cref="PublicationSession.EstimateAsync"/> and
/// <see cref="PublicationSession.StartAsync"/> price a run from the node's current open-ledger fee
/// — <see cref="PublicationEstimate.FeeDropsPerTransaction"/>.
///
/// A session is handed its <see cref="ISubmissionStrategy"/> already built, so it cannot reach in
/// and change what that strategy charges. What it can do is refuse to start, and it does:
/// <see cref="PublicationSession.StartAsync"/> compares
/// <see cref="ISubmissionStrategy.FeeDropsPerTransaction"/> against the estimate it just built and
/// throws <see cref="XrplFileError.FeeMismatch"/> before a drop is spent, rather than approving a
/// run priced differently from the one that would actually be submitted.
///
/// So the order on the caller's side is: ask for the estimate — either
/// <see cref="PublicationSession.EstimateAsync"/> or <see cref="PublicationEstimator.EstimateAsync"/>
/// — read its <see cref="PublicationEstimate.FeeDropsPerTransaction"/>, build the strategy with
/// that same value in its own <c>FeeDrops</c> option, then call
/// <see cref="PublicationSession.StartAsync"/>.
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
/// safe to pay for happen in <see cref="StartAsync"/> and are not repeated.
/// </summary>
public sealed class PublicationSession
{
    /// <summary>
    /// The stages a run passes through, in order. The rank is what keeps the recorded phase moving
    /// forward only: the writer reports a batch's progress through <see cref="Progress{T}"/>, which
    /// lands on the thread pool, so a chunk's report can arrive after the run has moved on.
    /// </summary>
    private static readonly string[] Stages = ["chunks", "lists", "read-back", "manifest", "done"];

    private readonly ISubmissionStrategy _strategy;
    private readonly ILedgerSource _source;
    private readonly ReadOnlyMemory<byte> _file;
    /// <summary>
    /// How long a tidy-up past its deadline is given to unwind and report what it settled before
    /// the session stops waiting for it.
    /// </summary>
    private static readonly TimeSpan TicketReleaseGrace = TimeSpan.FromSeconds(5);

    private readonly SessionOptions _options;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    // Captured once at StartAsync, from the same estimate the budget and balance checks used, and
    // reused for the report so its numbers cannot drift against the ones the run was approved
    // against. The fee is the one the strategy was checked to agree with; the mints are what a
    // ticketed strategy pays for on top of the memos.
    private readonly long _feeDropsPerTransaction;
    private readonly int _mints;

    /// <summary>Guards the state and its file: progress arrives from the thread pool.</summary>
    private readonly Lock _stateLock = new();

    private PublicationState _state;
    private Exception? _stateSaveFailure;

    /// <summary>0 before the run, 1 while it is running, 2 once it is over either way.</summary>
    private int _run;

    /// <summary>
    /// What <see cref="ResumeAsync"/> found already in the ledger: chunks and block lists, each by
    /// its own index. Null for a run that started fresh through <see cref="StartAsync"/> -- there
    /// is nothing to reconcile against on a first run.
    /// </summary>
    private LandedMemos? _landed;

    private PublicationSession(
        ISubmissionStrategy strategy, ILedgerSource source, ReadOnlyMemory<byte> file,
        PublicationState state, string statePath, SessionOptions options, long feeDropsPerTransaction, int mints)
    {
        _strategy = strategy;
        _source = source;
        _file = file;
        _state = state;
        _options = options;
        _feeDropsPerTransaction = feeDropsPerTransaction;
        _mints = mints;
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
    /// Estimates, refuses what cannot be afforded or what the strategy would price differently,
    /// and writes the state file — all before a single transaction is submitted. A run that is
    /// going to fail on money fails here, where it has cost nothing.
    /// </summary>
    public static async Task<PublicationSession> StartAsync(
        ISubmissionStrategy strategy, ILedgerSource source, ReadOnlyMemory<byte> file, string type, string? name,
        string writer, string keyReference, string network, SessionOptions options, CancellationToken cancellationToken)
    {
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
        // only (see RootOf), so a placeholder ledger here costs the check nothing -- it is the
        // same root the manifest will carry once the real per-block ledgers are known.
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
        string root = RootOf(chunks, startedIn);

        PublicationState state = new(
            PublicationState.CurrentVersion, network, writer, keyReference, type, name,
            file.Length, MemoLayout.ChunkBytes, root, startedIn, Phase: Stages[0],
            ChunkHashes: new Dictionary<int, string>());

        // One run, one file, and no two runs share one. Two publications from one account inside
        // one ledger — four seconds, ordinary in a batch loop — would otherwise agree on a name,
        // and the second would overwrite the only record of money the first had already spent.
        // The root is now known up front, but a hex root is an unfriendly file name and offers no
        // ordering, so the run is still told apart by a random suffix rather than by its identity.
        string suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        string path = Path.Combine(options.StateDirectory, $"{startedIn}-{writer}-{suffix}.json");
        state.Save(path);

        return new PublicationSession(
            strategy, source, file, state, path, options, estimate.FeeDropsPerTransaction, estimate.Mints);
    }

    /// <summary>
    /// Picks a run back up from its state file.
    ///
    /// The file is where to look, not what to believe. A crash can fall between a submission and
    /// its record, so the ledger is read from the run's starting point and whatever is actually
    /// there is what counts. The file's own map of hashes carries the report through the
    /// interruption; it does not decide what still needs sending.
    ///
    /// A resumed run still spends money, so it is checked exactly as a fresh one is: a fresh
    /// estimate is priced against the strategy's fee, and the caller's budget and the account's
    /// balance are checked against it, before the sweep below even runs. That estimate is for the
    /// <em>whole file</em>, not for the part still missing — the chunks already in the ledger are
    /// priced again, and their tickets with them. A budget sized for the original run therefore
    /// carries the resume as well; one sized for the remainder refuses it.
    /// </summary>
    public static async Task<PublicationSession> ResumeAsync(
        ISubmissionStrategy strategy, ILedgerSource source, ReadOnlyMemory<byte> file,
        string statePath, SessionOptions options, CancellationToken cancellationToken)
    {
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

        if (string.Equals(state.Phase, "done", StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} describes a run that finished; publishing it again would put a second copy in the ledger");
        }

        if (state.Size != file.Length)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} was started for {state.Size} bytes and this file is {file.Length}");
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
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, state.Chunk);
        string root = RootOf(chunks, state.StartedInLedger);

        if (state.Root is not { } recorded)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} records no root, so there is nothing to check this file against");
        }

        if (!string.Equals(recorded, root, StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} was started for a file rooted at {recorded} and this one roots at {root}");
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

        PublicationSession session = new(
            strategy, source, file, state, statePath, options, estimate.FeeDropsPerTransaction, estimate.Mints);
        session._landed = await session.ReconcileAsync(chunks, cancellationToken).ConfigureAwait(false);

        return session;
    }

    /// <summary>
    /// Which of this publication's memos are already in the ledger, read from the ledger itself.
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
    /// </summary>
    private async Task<LandedMemos> ReconcileAsync(IReadOnlyList<byte[]> chunks, CancellationToken cancellationToken)
    {
        Dictionary<int, LandedMemo> landedChunks = [];
        Dictionary<int, LandedMemo> landedLists = [];
        long to = await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        await foreach (LedgerTransaction tx in _source
            .AccountTransactionsAsync(_state.Writer, _state.StartedInLedger, to, forward: true, cancellationToken)
            .ConfigureAwait(false))
        {
            if (!tx.Succeeded || !tx.Validated) continue;
            if (tx.MemoDataHex is not { } data) continue;

            if (MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is { } chunkIndex)
            {
                if (chunkIndex < 0 || chunkIndex >= chunks.Count) continue;
                if (!string.Equals(data, MemoLayout.Hex(chunks[(int)chunkIndex]), StringComparison.OrdinalIgnoreCase)) continue;

                // First in canonical order, exactly as a reader will resolve it.
                landedChunks.TryAdd((int)chunkIndex, new LandedMemo(tx.Hash, tx.LedgerIndex, data));
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

        return new LandedMemos(landedChunks, landedLists);
    }

    /// <summary>The file's root, computed the way the writer will compute it -- over block hashes alone.</summary>
    private static string RootOf(IReadOnlyList<byte[]> chunks, long placeholderLedger)
    {
        int blocks = FileTree.BlockCount(chunks.Count);
        List<BlockEntry> entries = new(blocks);

        for (int block = 0; block < blocks; block++)
        {
            List<byte[]> members = [.. chunks.Skip(block * FileTree.BlockChunks).Take(FileTree.BlockChunks)];
            entries.Add(new BlockEntry(block, Convert.ToHexString(FileTree.BlockHash(members)), placeholderLedger));
        }

        // The root is over the block hashes alone; the ledger in an entry does not enter it.
        return FileTree.RootHex(entries);
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
            PublicationState started = State;

            XrplFileWriter writer = new(_strategy, _source, new WriterOptions { ReadBack = _options.ReadBack });

            PublishResult result = await writer.PublishAsync(
                _file, started.Type, started.Name, started.Writer, started.Network, _landed,
                new PhaseRecordingProgress(this, progress), cancellationToken).ConfigureAwait(false);

            Advance("done", result.Manifest.Root, result.ChunkHashes);

            // After "done" and never before: the manifest is in the ledger, so nothing the tidy-up
            // does — or fails to do — can cost the person their link (spec I8).
            (TicketRelease release, Exception? releaseFailure) = await ReleaseLeftoverTicketsAsync(cancellationToken).ConfigureAwait(false);

            long transactions = result.ChunkHashes.Count + result.ListHashes.Count + 1 + _mints;

            // Priced from the fee the estimate was approved against (see the remarks on
            // SessionOptions), not a fresh node query: the node's open-ledger fee moves with load,
            // and a second query here would report a cost the run was never checked against.
            return new PublicationReport(
                result.Link, result.Manifest, result.Writer,
                transactions, transactions * _feeDropsPerTransaction,
                _startedAt, DateTimeOffset.UtcNow,
                result.ChunkHashes, result.ListHashes, result.ManifestHash)
            {
                StateSaveFailure = StateSaveFailure,
                TicketsReleased = release.Released,
                TicketsAlreadyGone = release.AlreadyGone,
                TicketsNotReleased = release.NotReleased,
                TicketReleaseFailure = releaseFailure,
            };
        }
        finally
        {
            // A run that threw is over too. Its money is spent, and starting again from the top is
            // a decision for a caller who has re-checked the account, not for a retry loop.
            Interlocked.Exchange(ref _run, 2);
        }
    }

    /// <summary>
    /// Spends this library's leftover tickets under its own deadline, linked to the publication's
    /// token. A cancellation or the deadline ends it with the counts it reached; an exception is
    /// caught and returned. Neither fails the publication (spec I8).
    /// </summary>
    private async Task<(TicketRelease Release, Exception? Failure)> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken)
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

            release = _strategy.ReleaseLeftoverTicketsAsync(deadline.Token);

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

            return (TicketRelease.None, new TimeoutException(
                $"the tidy-up did not stop within {TicketReleaseGrace} of its {_options.TicketReleaseTimeout} deadline; tickets it had not released stay on the account"));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return (TicketRelease.None, null);
        }
        catch (Exception failure)
        {
            return (TicketRelease.None, failure);
        }
    }

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
    /// Per-chunk hashes are not written here, because they are not reachable —
    /// <see cref="PublishProgress"/> carries a count and a stage, not a transaction hash. The
    /// stage is what a person interrupted half-way actually needs, and it is what this records.
    /// </summary>
    private void Advance(string phase, string? root, IReadOnlyList<string>? chunkHashes)
    {
        lock (_stateLock)
        {
            if (RankOf(phase) <= RankOf(_state.Phase)) return;

            _state = _state with
            {
                Phase = phase,
                Root = root ?? _state.Root,
                ChunkHashes = chunkHashes is null
                    ? _state.ChunkHashes
                    : chunkHashes.Select((hash, index) => (hash, index)).ToDictionary(pair => pair.index, pair => pair.hash),
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
            session.Advance(value.Stage, root: null, chunkHashes: null);
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
