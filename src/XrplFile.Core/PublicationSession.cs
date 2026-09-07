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

        PublicationEstimator.Require(estimate, options.BudgetDrops);

        long startedIn = await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        PublicationState state = new(
            PublicationState.CurrentVersion, network, writer, keyReference, type, name,
            file.Length, MemoLayout.ChunkBytes, Root: null, startedIn, Phase: Stages[0],
            ChunkHashes: new Dictionary<int, string>());

        // One run, one file, and no two runs share one. Two publications from one account inside
        // one ledger — four seconds, ordinary in a batch loop — would otherwise agree on a name,
        // and the second would overwrite the only record of money the first had already spent.
        // The file's own root would be the natural name and is not known until the end, so the run
        // is told apart by chance instead; what is inside says which run it is.
        string suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        string path = Path.Combine(options.StateDirectory, $"{startedIn}-{writer}-{suffix}.json");
        state.Save(path);

        return new PublicationSession(
            strategy, source, file, state, path, options, estimate.FeeDropsPerTransaction, estimate.Mints);
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
                _file, started.Type, started.Name, started.Writer, started.Network,
                new PhaseRecordingProgress(this, progress), cancellationToken).ConfigureAwait(false);

            Advance("done", result.Manifest.Root, result.ChunkHashes);

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
