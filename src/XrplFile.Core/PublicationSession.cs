namespace XrplFile;

/// <remarks>
/// <see cref="SequenceStrategyOptions.FeeDrops"/> and <see cref="TicketStrategyOptions.FeeDrops"/>
/// both default to a hardcoded 12 drops, while <see cref="PublicationSession.EstimateAsync"/> and
/// <see cref="PublicationSession.StartAsync"/> price the run from the node's current open-ledger
/// fee — <see cref="PublicationEstimate.FeeDropsPerTransaction"/>. A session is handed its
/// <see cref="ISubmissionStrategy"/> already built, so it has no way to reach into that strategy
/// and change what it charges; the two numbers can only be kept in step by the caller who builds
/// the strategy. If they are not, the budget and balance checks in
/// <see cref="PublicationSession.StartAsync"/> — and the cost this class later reports — describe
/// a run priced differently from the one that actually gets submitted.
///
/// The fix lives on the caller's side: ask for the estimate first — either
/// <see cref="PublicationSession.EstimateAsync"/> or <see cref="PublicationEstimator.EstimateAsync"/>
/// directly — read its <see cref="PublicationEstimate.FeeDropsPerTransaction"/>, and construct
/// the strategy with that same value in its own <c>FeeDrops</c> option before passing it to
/// <see cref="PublicationSession.StartAsync"/>. Once that is done, the session's own numbers stay
/// in step with it by construction: <see cref="PublicationSession.StartAsync"/> captures the fee
/// the estimate it approved the run against actually used, and <see cref="PublicationSession.RunAsync"/>
/// prices <see cref="PublicationReport.FeeDropsSpent"/> from that same captured figure rather than
/// asking the node again — a second query could return a different, later fee and make the report
/// describe a run that never happened either.
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
/// A publication as something that can be estimated, started, interrupted and found again.
///
/// The writer knows the format's order and the strategy knows how a transaction reaches the
/// ledger. What is left is everything about money and time: what this will cost before it costs
/// it, whether the account can carry it, what survives the process dying, and what to show
/// afterwards. That is this class, and it is the one an application holds.
/// </summary>
public sealed class PublicationSession
{
    private readonly ISubmissionStrategy _strategy;
    private readonly ILedgerSource _source;
    private readonly ReadOnlyMemory<byte> _file;
    private readonly SessionOptions _options;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    // Captured once at StartAsync, from the same estimate the budget and balance checks used, and
    // reused for the report's FeeDropsSpent so that number does not silently drift against the
    // one the run was priced and approved against. See the remarks on SessionOptions for why the
    // strategy's own charge can still differ from this.
    private readonly long _feeDropsPerTransaction;

    private PublicationState _state;

    private PublicationSession(
        ISubmissionStrategy strategy, ILedgerSource source, ReadOnlyMemory<byte> file,
        PublicationState state, string statePath, SessionOptions options, long feeDropsPerTransaction)
    {
        _strategy = strategy;
        _source = source;
        _file = file;
        _state = state;
        _options = options;
        _feeDropsPerTransaction = feeDropsPerTransaction;
        StatePath = statePath;
    }

    public string StatePath { get; }

    public PublicationState State => _state;

    /// <summary>What the run would cost, without creating anything.</summary>
    public static Task<PublicationEstimate> EstimateAsync(
        ILedgerSource source, string account, long fileBytes, SessionOptions options, CancellationToken cancellationToken) =>
        PublicationEstimator.EstimateAsync(source, account, fileBytes, options.TicketsPerBatch, cancellationToken);

    /// <summary>
    /// Estimates, refuses what cannot be afforded, and writes the state file — all before a single
    /// transaction is submitted. A run that is going to fail on money fails here, where it has
    /// cost nothing.
    /// </summary>
    public static async Task<PublicationSession> StartAsync(
        ISubmissionStrategy strategy, ILedgerSource source, ReadOnlyMemory<byte> file, string type, string? name,
        string writer, string keyReference, string network, SessionOptions options, CancellationToken cancellationToken)
    {
        PublicationEstimate estimate = await PublicationEstimator
            .EstimateAsync(source, writer, file.Length, options.TicketsPerBatch, cancellationToken).ConfigureAwait(false);

        PublicationEstimator.Require(estimate, options.BudgetDrops);

        long startedIn = await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        PublicationState state = new(
            PublicationState.CurrentVersion, network, writer, keyReference, type, name,
            file.Length, MemoLayout.ChunkBytes, Root: null, startedIn, Phase: "chunks",
            ChunkHashes: new Dictionary<int, string>());

        string path = Path.Combine(options.StateDirectory, $"{startedIn}-{writer}.json");
        state.Save(path);

        return new PublicationSession(strategy, source, file, state, path, options, estimate.FeeDropsPerTransaction);
    }

    public async Task<PublicationReport> RunAsync(IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        XrplFileWriter writer = new(_strategy, _source, new WriterOptions { ReadBack = _options.ReadBack });

        PublishResult result = await writer.PublishAsync(
            _file, _state.Type, _state.Name, _state.Writer, _state.Network, progress, cancellationToken).ConfigureAwait(false);

        _state = _state with
        {
            Root = result.Manifest.Root,
            Phase = "done",
            ChunkHashes = result.ChunkHashes
                .Select((hash, index) => (hash, index))
                .ToDictionary(pair => pair.index, pair => pair.hash),
        };
        _state.Save(StatePath);

        long transactions = result.ChunkHashes.Count + result.ListHashes.Count + 1;

        // Priced from the fee the estimate was approved against (see the remarks on
        // SessionOptions), not a fresh node query: the node's open-ledger fee moves with load,
        // and a second query here would report a cost the run was never checked against.
        return new PublicationReport(
            result.Link, result.Manifest, result.Writer,
            transactions, transactions * _feeDropsPerTransaction,
            _startedAt, DateTimeOffset.UtcNow,
            result.ChunkHashes, result.ListHashes, result.ManifestHash);
    }

    /// <summary>
    /// Runs that were started and never finished. Money may be sitting on the accounts they name,
    /// and their state files are the only record of it.
    /// </summary>
    public static IReadOnlyList<PublicationState> Unfinished(string stateDirectory)
    {
        if (!Directory.Exists(stateDirectory)) return [];

        List<PublicationState> unfinished = [];

        foreach (string path in Directory.EnumerateFiles(stateDirectory, "*.json"))
        {
            PublicationState state;

            try
            {
                state = PublicationState.Load(path);
            }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException)
            {
                // A file this library cannot read is not a run it can resume. Listing it as one
                // would put a broken row in front of a person with nothing they could do about it.
                continue;
            }

            if (!string.Equals(state.Phase, "done", StringComparison.Ordinal)) unfinished.Add(state);
        }

        return unfinished;
    }
}
