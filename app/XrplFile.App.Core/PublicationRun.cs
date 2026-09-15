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
/// <param name="HistoryNote">
/// Something about the history that the person should know after a succeeded run, or
/// <see langword="null"/> when there is nothing to say (including when the run has not succeeded).
/// Not only a failure: <see cref="IPublicationHistory.Append"/> can succeed at recording the
/// publication and still have something worth mentioning, such as an earlier, corrupt list it had
/// to replace. The publication itself is never at risk from anything this carries — <see cref="Report"/>
/// and <see cref="State"/> already say so on their own — this is about the list, not the ledger.
/// </param>
public sealed record RunSnapshot(
    PublicationRunState State,
    PublishProgress? Progress,
    PublicationReport? Report,
    string? Failure,
    string? Name,
    string? Network,
    string? HistoryNote);

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
    IPublicationHistory history,
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
    private string? _historyNote;

    /// <summary>
    /// Which run this is, counting from 1 at the first <see cref="Begin"/>. Exists only to pin a
    /// write onto the run it belongs to: the history append that follows a success runs after
    /// <see cref="PublicationRunState.Succeeded"/> is already published, which is the one window
    /// where <see cref="Begin"/> and <see cref="Reset"/> both accept — a screen reacting to that
    /// success and starting another run immediately must not have the first run's history outcome
    /// land on the second run's snapshot. Bumped in <see cref="Begin"/> (a new run starting is the
    /// ordinary case this exists for) and in <see cref="Reset"/> too: a plain reset, with no new run
    /// at all, opens exactly the same window, since it accepts on the same "not Running" state and
    /// can therefore also run concurrently with a still-in-flight history append from the run it is
    /// clearing. See <see cref="RunAsync"/>'s own remarks at the two writes this guards.
    /// </summary>
    private int _generation;

    /// <summary>Completes when the current run has finished, however it finished.</summary>
    /// <remarks>
    /// Read under <see cref="_gate"/>, like every other piece of this run's state: an unsynchronised
    /// field here would let a caller pair one run's <see cref="Snapshot"/> with a different run's
    /// completion task — <see cref="Begin"/> replaces this the moment a new run starts, which a
    /// racing reader could otherwise catch mid-assignment.
    /// </remarks>
    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _completion;
            }
        }
    }

    public RunSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new RunSnapshot(_state, _progress, _report, _failure, _name, _network, _historyNote);
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
            _historyNote = null;

            // Bumped here, not only in Begin: Reset accepts on the same "not Running" state Begin
            // does, and a history append from the run just cleared can still be in flight when it
            // runs (the append follows Succeeded, which is already published by the time either of
            // these can be called). Without this, that append's own write — pinned to the
            // generation Reset is about to leave behind — would still match and land its note on
            // this now-Idle snapshot, which is exactly what pinning it was supposed to prevent.
            ++_generation;

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
            int generation = ++_generation;

            _cancellation?.Dispose();
            _cancellation = cancellation;
            _state = PublicationRunState.Running;
            _progress = null;
            _report = null;
            _failure = null;
            _historyNote = null;
            _name = request.Name;
            _network = request.Network;
            _completion = Task.Run(() => RunAsync(request, statePath, cancellation.Token, generation));
        }

        return true;
    }

    private async Task RunAsync(PublishRequest request, string? statePath, CancellationToken cancellationToken, int generation)
    {
        PublicationRunState finalState;
        PublicationReport? report = null;
        string? failure = null;

        try
        {
            paths.EnsureCreated();

            ILedgerConnection connection =
                await connections.ConnectAsync(request.Network, cancellationToken).ConfigureAwait(false);

            try
            {
                SessionOptions estimateOptions = new()
                {
                    StateDirectory = paths.StateDirectory,
                    TicketsPerBatch = PublishPolicy.TicketsPerBatch,
                };

                // Estimated again here rather than carried from the screen: the node's price moves,
                // and StartAsync refuses a strategy priced differently (FeeMismatch). This is also
                // what the budget is set from, so a run cannot quietly cost more than what was just
                // priced.
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

                ISubmissionStrategy strategy = PublishPolicy.Create(estimate, connection, signer);

                try
                {
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
                finally
                {
                    // Disposal is cleanup, not part of what the run is about, and its own outcome
                    // must never be able to override the one just decided above: a paid publication
                    // that already has its report in hand must not be reported Failed with the
                    // report thrown away because closing the strategy afterwards happened to throw.
                    // Every strategy this application builds today either swallows its own disposal
                    // errors or does nothing (SequenceStrategy.DisposeAsync is a no-op; TicketStrategy
                    // does not throw from disposing its queue), so this is not reachable yet — but
                    // the previous version of this method did not depend on that being true, and
                    // this one does, so the dependency is closed here rather than left implicit.
                    await DisposeQuietlyAsync(strategy).ConfigureAwait(false);
                }
            }
            finally
            {
                // Same reasoning as the strategy's own disposal above, one level out.
                await DisposeQuietlyAsync(connection).ConfigureAwait(false);
            }
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

        // Every connection and strategy acquired above has already been disposed (quietly; see the
        // finally blocks) by the time control reaches here — during stack unwinding into whichever
        // catch ran, or as the try block's own scope closed on the success path — so publishing the
        // terminal state only from this point on, never from inside the try, means a screen that
        // reacts to Succeeded and immediately starts another run can never observe the previous
        // run's connection or strategy still mid-dispose (design F6's "closing stops it" only means
        // something if the stop is visible after it has actually happened).
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
            //
            // This is also the point _state stops being Running, which is the only thing that keeps
            // Begin and Reset out — from here on a screen reacting to Succeeded may legitimately
            // start the next run before the history append below has even begun. The two writes
            // that follow are therefore pinned to this run's own generation, not written
            // unconditionally: a newer run's Begin bumps _generation, and a write this run makes
            // after that must be dropped rather than land on a snapshot that is no longer about it.
            lock (_gate)
            {
                _report = report;
                _state = PublicationRunState.Succeeded;
                _cancellation?.Dispose();
                _cancellation = null;
            }

            try
            {
                HistoryAppendResult appended = history.Append(new PublicationRecord(
                    report!.Link.ToString(),
                    request.Name,
                    request.File.Length,
                    request.Type,
                    request.Network,
                    request.Account,
                    report.Transactions,
                    report.FeeDropsEstimated,
                    report.FinishedAt));

                // Taken from Append's own return value, not from a shared property read afterward:
                // IPublicationHistory.Append is documented to return only its own call's outcome, so
                // this cannot read a different, overlapping append's verdict as if it were this
                // run's.
                if (appended.Note is not null)
                {
                    lock (_gate)
                    {
                        if (_generation == generation) _historyNote = appended.Note;
                    }
                }
            }
            catch (Exception ex)
            {
                // PublicationHistory.Append is documented not to throw IOException or
                // UnauthorizedAccessException any more — it returns a Recorded: false result instead,
                // handled above. This is the last-resort guard for anything else. It exists not
                // because _state could still be corrupted by it (Succeeded is already published, so
                // Reset and the next Start already work regardless) but because Completion must not
                // fault: a screen or a test awaiting a publication that actually succeeded would
                // otherwise receive an exception instead of a completed run, and a RunAsync that lets
                // this escape leaves an unobserved task exception behind it besides. The exception's
                // own message is kept, not discarded: this runs after money has been spent, and
                // losing every trace of what went wrong afterwards is not something to also do here.
                lock (_gate)
                {
                    if (_generation == generation)
                    {
                        _historyNote =
                            $"The publication succeeded, but recording it in your history failed unexpectedly: {ex.Message}";
                    }
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
    /// Disposes something this run acquired without letting whatever it throws be mistaken for the
    /// run's own outcome. See the two call sites in <see cref="RunAsync"/> for why that distinction
    /// matters here specifically.
    /// </summary>
    private static async ValueTask DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Swallowed on purpose — see the call sites' own remarks.
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
