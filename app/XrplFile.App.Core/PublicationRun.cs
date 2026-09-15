using XrplFile.Keys;

namespace XrplFile.App.Core;

public enum PublicationRunState
{
    Idle,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// One consistent view of the run. A screen takes this rather than reading fields one at a time:
/// separate reads could see <see cref="PublicationRunState.Succeeded"/> with no report yet, or
/// <see cref="PublicationRunState.Running"/> carrying the previous run's one.
/// </summary>
/// <param name="HistoryFailure">
/// Why a succeeded run's <see cref="PublicationHistory.Append"/> could not record it, or
/// <see langword="null"/> when it did (or when the run has not succeeded). The publication itself
/// is never at risk from this — <see cref="Report"/> and <see cref="State"/> already carry it — but
/// a person whose history silently stopped growing has no way to know their last publication is
/// missing from the list unless something tells them, which is what this is for.
/// </param>
public sealed record RunSnapshot(
    PublicationRunState State,
    PublishProgress? Progress,
    PublicationReport? Report,
    string? Failure,
    string? Name,
    string? Network,
    string? HistoryFailure);

/// <summary>
/// The one publication this application can have going (design 8, F5). It holds the cancellation
/// token, the connection, the strategy and the session, and it outlives the screen that started it,
/// so navigating to the key list and back finds the same run at its current progress.
///
/// It does not outlive the window: closing stops it (design F6), and the state file part A writes is
/// what makes it resumable afterwards.
/// </summary>
public sealed class PublicationRun(
    IConnectionFactory connections,
    IKeyVault vault,
    RunPaths paths,
    PublicationHistory history,
    VaultAutoLock? autoLock = null)
{
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cancellation;
    private Task _completion = Task.CompletedTask;
    private PublicationRunState _state = PublicationRunState.Idle;
    private PublishProgress? _progress;
    private PublicationReport? _report;
    private string? _failure;
    private string? _name;
    private string? _network;
    private string? _historyFailure;

    /// <summary>Completes when the current run has finished, however it finished.</summary>
    /// <remarks>
    /// Read under <see cref="_gate"/>, like every other piece of this run's state: an unsynchronised
    /// field here would let a caller pair one run's <see cref="Snapshot"/> with a different run's
    /// completion task — <see cref="Begin"/> replaces this the moment a new run starts, which a
    /// racing reader could otherwise catch mid-assignment.
    /// </remarks>
    public Task Completion
    {
        get { lock (_gate) return _completion; }
    }

    public RunSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new RunSnapshot(_state, _progress, _report, _failure, _name, _network, _historyFailure);
            }
        }
    }

    /// <summary>Starts a publication. <see langword="false"/> when one is already going.</summary>
    public bool Start(PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Begin(request, statePath: null);
    }

    /// <summary>Resumes an interrupted one. <see langword="false"/> when one is already going.</summary>
    public bool Resume(string statePath, PublishRequest request)
    {
        ArgumentException.ThrowIfNullOrEmpty(statePath);
        ArgumentNullException.ThrowIfNull(request);

        return Begin(request, statePath);
    }

    public void Cancel()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    /// <summary>
    /// Cancels and waits for the run to actually stop. What the window-close handler calls before it
    /// locks the vault (design 9): locking first would kill the run at its next signature.
    /// <see langword="false"/> means the timeout expired with the run still going — the caller
    /// proceeds anyway rather than holding a closing window open forever.
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        Task running;
        lock (_gate)
        {
            _cancellation?.Cancel();
            running = _completion;
        }

        if (running.IsCompleted) return true;

        Task finished = await Task.WhenAny(running, Task.Delay(timeout)).ConfigureAwait(false);
        return ReferenceEquals(finished, running);
    }

    /// <summary>Puts a finished run's outcome away so the screen can start another.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_state == PublicationRunState.Running) return;

            _state = PublicationRunState.Idle;
            _progress = null;
            _report = null;
            _failure = null;
            _name = null;
            _network = null;
            _historyFailure = null;

            // Already disposed and nulled by RunAsync's own terminal-state publication once the run
            // in question is not Running any more — this is a second, defensive clearing so a stale
            // (and, in the ordinary case, already-null) source can never be reached through Cancel
            // once the run it belonged to is done.
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private bool Begin(PublishRequest request, string? statePath)
    {
        lock (_gate)
        {
            if (_state == PublicationRunState.Running) return false;

            // Built only once a start is actually going to happen: a refused start (the branch
            // above) must not allocate a token source nobody will ever dispose.
            CancellationTokenSource cancellation = new();

            _cancellation?.Dispose();
            _cancellation = cancellation;
            _state = PublicationRunState.Running;
            _progress = null;
            _report = null;
            _failure = null;
            _historyFailure = null;
            _name = request.Name;
            _network = request.Network;
            _completion = Task.Run(() => RunAsync(request, statePath, cancellation.Token));
        }

        return true;
    }

    private async Task RunAsync(PublishRequest request, string? statePath, CancellationToken cancellationToken)
    {
        PublicationRunState finalState;
        PublicationReport? report = null;
        string? failure = null;

        try
        {
            paths.EnsureCreated();

            await using ILedgerConnection connection =
                await connections.ConnectAsync(request.Network, cancellationToken).ConfigureAwait(false);

            SessionOptions estimateOptions = new()
            {
                StateDirectory = paths.StateDirectory,
                TicketsPerBatch = PublishPolicy.TicketsPerBatch,
            };

            // Estimated again here rather than carried from the screen: the node's price moves, and
            // StartAsync refuses a strategy priced differently (FeeMismatch). This is also what the
            // budget is set from, so a run cannot quietly cost more than what was just priced.
            PublicationEstimate estimate = await PublicationSession.EstimateAsync(
                connection.Source, request.Account, request.File.Length, estimateOptions, cancellationToken)
                .ConfigureAwait(false);

            SessionOptions options = new()
            {
                StateDirectory = paths.StateDirectory,
                TicketsPerBatch = PublishPolicy.TicketsPerBatch,
                BudgetDrops = estimate.RequiredDrops,
            };

            VaultSigner signer = await VaultSigner
                .CreateAsync(vault, request.KeyReference, cancellationToken, autoLock).ConfigureAwait(false);

            await using ISubmissionStrategy strategy = PublishPolicy.Create(estimate, connection, signer);

            PublicationSession session = statePath is null
                ? await PublicationSession.StartAsync(
                    strategy,
                    connection.Source,
                    request.File,
                    request.Type,
                    request.Name,
                    request.Account,
                    request.KeyReference,
                    request.Network,
                    options,
                    cancellationToken).ConfigureAwait(false)
                : await PublicationSession.ResumeAsync(
                    strategy, connection.Source, request.File, statePath, options, cancellationToken)
                    .ConfigureAwait(false);

            report = await session
                .RunAsync(new SynchronousProgress(Observe), cancellationToken).ConfigureAwait(false);

            finalState = PublicationRunState.Succeeded;
        }
        catch (OperationCanceledException)
        {
            finalState = PublicationRunState.Cancelled;
        }
        catch (XrplFileException ex)
        {
            failure = PublishErrorMessages.For(ex);
            finalState = PublicationRunState.Failed;
        }
        catch (VaultException ex)
        {
            failure = VaultErrorMessages.For(ex.Error);
            finalState = PublicationRunState.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = "The publication's state could not be written to disk, so the run was stopped.";
            finalState = PublicationRunState.Failed;
        }
        catch (Exception)
        {
            // Anything this application has no specific sentence for — an XrplFileWriter argument
            // check on an empty file or a blank media type, for one real example. Reported rather
            // than left to escape: an exception this method does not catch faults Completion and
            // leaves _state stuck at Running forever, which refuses Reset, refuses every future
            // Start, and turns StopAsync into cancelling a task that already died and reporting
            // success about it. A service that can only be recovered by restarting the whole
            // application is worse than one that reports a failure it does not have a specific
            // sentence for.
            failure = "Something went wrong that this application does not have a specific message for. The run was stopped.";
            finalState = PublicationRunState.Failed;
        }

        // Every `await using` declared above has already been disposed by the time control reaches
        // here — during stack unwinding into whichever catch ran, or as the try block's own scope
        // closed on the success path — so publishing the terminal state only from this point on,
        // never from inside the try, means a screen that reacts to Succeeded and immediately starts
        // another run can never observe the previous run's connection or strategy still mid-dispose
        // (design F6's "closing stops it" only means something if the stop is visible after it has
        // actually happened).
        if (finalState == PublicationRunState.Succeeded)
        {
            // Assigned before the history is touched, and specifically before Append is called: a
            // run that reached this point has already spent its money and the ledger already holds
            // it, and nothing from here on may turn that into a reported Failed with the report
            // discarded. That used to be reachable: PublicationHistory.Save used to let
            // Directory.CreateDirectory throw outside its own try, which came back here as an
            // IOException, was caught by the handler above, and reported a paid, completed
            // publication as Failed with _report never assigned at all — the one outcome
            // PublicationHistory's own remarks say must never happen. Save no longer lets that
            // happen either, but the ordering here is the primary defence, not a backstop for it.
            lock (_gate)
            {
                _report = report;
                _state = PublicationRunState.Succeeded;
                _cancellation?.Dispose();
                _cancellation = null;
            }

            try
            {
                history.Append(new PublicationRecord(
                    report!.Link.ToString(),
                    request.Name,
                    request.File.Length,
                    request.Type,
                    request.Network,
                    request.Account,
                    report.Transactions,
                    report.FeeDropsEstimated,
                    report.FinishedAt));

                if (history.Failure is not null)
                {
                    lock (_gate) _historyFailure = history.Failure;
                }
            }
            catch (Exception)
            {
                // PublicationHistory.Append is documented not to throw IOException or
                // UnauthorizedAccessException any more — it degrades into Failure instead, which is
                // surfaced above. This is the last-resort guard for anything else: the publication
                // already succeeded and is already recorded in _report and _state, so nothing here
                // may escape RunAsync uncaught (the exact wedge the catch-all above exists to
                // prevent) or turn this run into a reported failure (the exact loss the ordering
                // above exists to prevent).
                lock (_gate)
                {
                    _historyFailure =
                        "The publication succeeded, but recording it in your history failed unexpectedly.";
                }
            }
        }
        else
        {
            lock (_gate)
            {
                _failure = failure;
                _state = finalState;
                _cancellation?.Dispose();
                _cancellation = null;
            }
        }
    }

    /// <summary>
    /// Every progress report is also activity on the vault (design 10). <see cref="VaultSigner"/>
    /// already touches the idle lock on each signature, but the read-back stage signs nothing and
    /// can outlast the idle window on a large file — and the manifest signature that follows it
    /// would then find a locked vault at the very last transaction of an otherwise complete run.
    /// </summary>
    private void Observe(PublishProgress progress)
    {
        autoLock?.Touch();
        lock (_gate) _progress = progress;
    }

    /// <summary>
    /// Reports straight through on the calling thread. <see cref="Progress{T}"/> posts to a
    /// synchronization context, which on a MAUI Blazor window means the report arrives after the
    /// screen has already read the state it was supposed to update.
    /// </summary>
    private sealed class SynchronousProgress(Action<PublishProgress> report) : IProgress<PublishProgress>
    {
        public void Report(PublishProgress value) => report(value);
    }
}
