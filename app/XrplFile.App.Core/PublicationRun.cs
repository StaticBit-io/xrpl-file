using System.Globalization;
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
    string? HistoryNote)
{
    /// <summary>
    /// Whether the run publishes on tickets, as the estimate it was approved against decided.
    /// False until that estimate is taken — and until then the run has minted nothing.
    /// </summary>
    public bool UsesTickets { get; init; }

    /// <summary>What one ticket locks, in drops, from the same estimate. Zero until it is taken.</summary>
    public long OwnerReserveDrops { get; init; }

    /// <summary>Whether the run publishes through Batch, with a payer (spec 6.1).</summary>
    public bool UsesBatch { get; init; }

    /// <summary>The run's state file, once it has one: what Resume on the failed-run screen resumes (spec B19).</summary>
    public string? StatePath { get; init; }

    /// <summary>When this run was started, on the run's clock (spec 6.3: the elapsed time).</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// When this run stopped, however it stopped, on the same clock as <see cref="StartedAt"/>; null
    /// while it runs. What freezes the elapsed time on a finished screen.
    /// </summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>The file's size, which says how many passes are still to come (spec B21).</summary>
    public long FileBytes { get; init; }

    /// <summary>
    /// What a resume will cost, while it waits for the person (spec S25); null when no resume is
    /// waiting. <see cref="PublicationRun.AnswerConfirmation"/> answers it.
    /// </summary>
    public CostSummary? Confirmation { get; init; }

    /// <summary>
    /// The working time of this publication's earlier sessions, from its state file; zero for a run
    /// that was started rather than resumed.
    /// </summary>
    public TimeSpan WorkedBefore { get; init; }

    /// <summary>
    /// How long this session's resume confirmation waited for the person: time that is no one's work,
    /// taken off the session's own span when the working time is added up.
    /// </summary>
    public TimeSpan WaitedForPerson { get; init; }
}

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
    VaultAutoLock? autoLock = null,
    TimeProvider? time = null,
    BatchStrategyOptions? batchOptions = null,
    Func<string, IPublicationSource>? openSource = null,
    IKeepAwake? keepAwake = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, IPublicationSource> _openSource = openSource ?? (path => FilePublicationSource.Open(path));

    /// <summary>State files whose recorded source the last resume found changed (spec B17); guarded by <see cref="_gate"/>.</summary>
    private readonly HashSet<string> _rejectedSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly RunTraceLog _traceLog = new(paths.TraceLogPath);
    private bool _usesBatch;
    private string? _statePath;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private long _fileBytes;
    private TimeSpan _workedBefore;
    private TimeSpan _waited;

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
    private bool _usesTickets;
    private long _ownerReserveDrops;
    private CostSummary? _confirmation;
    private TaskCompletionSource<bool>? _answer;

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
                return new RunSnapshot(_state, _progress, _report, _failure, _name, _network, _historyNote)
                {
                    UsesTickets = _usesTickets,
                    OwnerReserveDrops = _ownerReserveDrops,
                    UsesBatch = _usesBatch,
                    StatePath = _statePath,
                    StartedAt = _startedAt,
                    FinishedAt = _finishedAt,
                    FileBytes = _fileBytes,
                    Confirmation = _confirmation,
                    WorkedBefore = _workedBefore,
                    WaitedForPerson = _waited,
                };
            }
        }
    }

    /// <summary>
    /// Starts a publication with the file the estimate held and the approval it made (spec S24, S25).
    /// <see langword="false"/> when one is already going; the request's source then stays the caller's.
    /// On <see langword="true"/> the run owns the source and disposes it when it ends.
    /// </summary>
    public bool Start(PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Source is null) throw new ArgumentException("a fresh run publishes the file its estimate held", nameof(request));
        if (request.Approval is null) throw new ArgumentException("a fresh run spends what its estimate approved", nameof(request));

        return Begin(request, statePath: null);
    }

    /// <summary>
    /// Resumes an interrupted one, opening the file at <see cref="PublishRequest.SourcePath"/> itself
    /// (spec S7, S24). <see langword="false"/> when one is already going.
    /// </summary>
    public bool Resume(string statePath, PublishRequest request)
    {
        ArgumentException.ThrowIfNullOrEmpty(statePath);
        ArgumentNullException.ThrowIfNull(request);

        return Begin(request, statePath);
    }

    /// <summary>
    /// Whether the last resume of this run found the file at its recorded path changed (spec B17): the
    /// next resume then asks for the file instead of trying the same path again.
    /// </summary>
    public bool HasRejectedSource(string statePath)
    {
        lock (_gate) return _rejectedSources.Contains(Path.GetFullPath(statePath));
    }

    public void Cancel()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    /// <summary>
    /// Answers a resume that waits for the person (spec S25): true goes on, false stops the run
    /// before anything is sent. False when no resume is waiting.
    /// </summary>
    public bool AnswerConfirmation(bool approved)
    {
        TaskCompletionSource<bool>? answer;

        lock (_gate)
        {
            answer = _answer;
        }

        return answer?.TrySetResult(approved) == true;
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

        Task finished = await Task.WhenAny(running, Task.Delay(timeout, _time)).ConfigureAwait(false);
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
            _usesTickets = false;
            _ownerReserveDrops = 0;
            _usesBatch = false;
            _statePath = null;
            _startedAt = null;
            _finishedAt = null;
            _fileBytes = 0;
            _workedBefore = TimeSpan.Zero;
            _waited = TimeSpan.Zero;
            _confirmation = null;
            _answer?.TrySetCanceled();
            _answer = null;

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
            _usesTickets = false;
            _ownerReserveDrops = 0;
            _name = request.Name;
            _network = request.Network;
            _usesBatch = request.PayerAccount is not null;
            _statePath = statePath;
            _startedAt = _time.GetUtcNow();
            _finishedAt = null;
            _fileBytes = request.Size;
            _workedBefore = TimeSpan.Zero;
            _waited = TimeSpan.Zero;

            // Inside the lock that starts the run, so a refusal this very run records cannot be erased
            // by a removal that comes after it: a file the person just picked is a fresh try.
            if (statePath is not null && !request.SourceRecorded) _rejectedSources.Remove(Path.GetFullPath(statePath));
            _completion = Task.Run(() => RunAsync(request, statePath, cancellation.Token, generation));
        }

        return true;
    }

    private async Task RunAsync(PublishRequest request, string? statePath, CancellationToken cancellationToken, int generation)
    {
        PublicationRunState finalState;
        PublicationReport? report = null;
        PublicationState? finished = null;
        string? finishedPath = null;
        string? failure = null;
        Exception? caught = null;
        RunAttempt attempt = new()
        {
            Point = statePath is null ? RunFailurePoint.BeforeSubmitting : RunFailurePoint.BeforeResuming,
            StatePath = statePath,
        };

        // An exception filter that never catches: it keeps whatever ended the run for the trace log,
        // which every catch below would otherwise have to hand over separately.
        bool Keep(Exception error)
        {
            caught = error;
            return false;
        }

        IPublicationSource? source = request.Source;

        try
        {
            // First, so that a resume's hashing and reconciliation are covered too (spec S13).
            keepAwake?.Acquire();

            // Ends with this try block, on every path out of the work; what follows the block never waits on a signer.
            using ActivityLease? lease = autoLock is null ? null : ActivityLease.Start(autoLock.Touch, _time);
            paths.EnsureCreated();

            // Before any node is contacted: a recorded file that cannot be opened needs no connection to say so.
            source ??= OpenRecorded(request);

            // A failed read of the file is told apart from a failed write of the state.
            IPublicationSource reading = new ReadReportingSource(source, request.SourcePath);

            ILedgerConnection connection =
                await connections.ConnectAsync(request.Network, cancellationToken).ConfigureAwait(false);

            try
            {
                // A run resumes on the strategy it recorded (spec B16); a fresh one is Batch when it has a payer (spec B4).
                PublicationState? recorded = statePath is null ? null : PublicationState.Load(statePath);
                bool batch = recorded is null ? request.PayerAccount is not null : recorded.Version == PublicationState.BatchVersion;

                // The strategy actually run decides what the screen assumes about its pace, not the request: a resume runs the recorded one.
                lock (_gate)
                {
                    if (_generation == generation)
                    {
                        _usesBatch = batch;
                        _workedBefore = TimeSpan.FromSeconds(recorded?.WorkedSeconds ?? 0);
                    }
                }

                (report, finished, finishedPath) = batch
                    ? await RunBatchAsync(request, reading, statePath, connection, attempt, generation, cancellationToken).ConfigureAwait(false)
                    : await RunOneAccountAsync(request, reading, statePath, recorded, connection, attempt, generation, cancellationToken).ConfigureAwait(false);

                finalState = PublicationRunState.Succeeded;
            }
            finally
            {
                await DisposeQuietlyAsync(connection).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (Keep(ex))
        {
            // Never reached: Keep returns false.
            throw;
        }
        catch (OperationCanceledException)
        {
            finalState = PublicationRunState.Cancelled;
        }
        catch (FeeTooLargeForApplicationException)
        {
            failure = PublishErrorMessages.FeeTooLargeForApplication;
            finalState = PublicationRunState.Failed;
        }
        catch (SourceUnreadableException ex)
        {
            // Not marked as a rejected source: "has changed since the run started" is reserved for a
            // size or root refusal (Spec question 10), and this message already sends the person to the picker.
            failure = $"The file at {ex.Path} could not be read. Choose it again.";
            finalState = PublicationRunState.Failed;
        }
        catch (SourceReadFailedException ex)
        {
            // Not the state file: the generic IOException clause below says the state could not be
            // written, which would send the person after the wrong thing. With a state file the run
            // stays resumable once the file is back; a fresh run that fails while its file is hashed
            // has none yet (both starts hash before they save), so there is nothing to resume.
            failure = attempt.StatePath is null
                ? (ex.Path is null
                    ? "The file could not be read. Make it available and publish it again."
                    : $"The file at {ex.Path} could not be read. Make it available and publish it again.")
                : (ex.Path is null
                    ? "The file could not be read while it was being published. Make it available again and resume the run."
                    : $"The file at {ex.Path} could not be read while it was being published. Make it available again and resume the run.");
            finalState = PublicationRunState.Failed;
        }
        catch (XrplFileException ex) when (ex is { Error: XrplFileError.ResumeMismatch, SourceChanged: true } && statePath is not null)
        {
            // Only a size or root refusal blames the file; a finished run, another chunk size or
            // another writer is refused with ResumeMismatch too, and would open the picker for nothing.
            lock (_gate) _rejectedSources.Add(Path.GetFullPath(statePath));
            failure = request.SourceRecorded
                ? $"The file at {request.SourcePath} has changed since the run started. Choose the original file."
                : "That file is not the one this run was publishing.";
            finalState = PublicationRunState.Failed;
        }
        catch (XrplFileException ex)
        {
            failure = PublishErrorMessages.ForRun(ex, attempt.Point, attempt.UsesTickets, attempt.OwnerReserveDrops);
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
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            failure = "This run's state file could not be read, so it cannot be resumed.";
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
        finally
        {
            // Released before the terminal state is published, so a screen that sees the run end finds the file free.
            if (source is not null) await DisposeQuietlyAsync(source).ConfigureAwait(false);

            keepAwake.ReleaseQuietly();
        }

        // Every connection and strategy acquired above has already been disposed (quietly; see the
        // finally blocks) by the time control reaches here — during stack unwinding into whichever
        // catch ran, or as the try block's own scope closed on the success path — so publishing the
        // terminal state only from this point on, never from inside the try, means a screen that
        // reacts to Succeeded and immediately starts another run can never observe the previous
        // run's connection or strategy still mid-dispose (design F6's "closing stops it" only means
        // something if the stop is visible after it has actually happened).
        await WriteTraceAsync(attempt, finalState, caught).ConfigureAwait(false);

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
                _finishedAt = _time.GetUtcNow();
                _cancellation?.Dispose();
                _cancellation = null;
            }

            try
            {
                HistoryAppendResult appended = history.Append(new PublicationRecord(
                    report!.Link.ToString(),
                    finished!.Name ?? request.Name,
                    finished.Size,
                    finished.Type,
                    finished.Network,
                    finished.Writer,
                    report.Transactions,
                    report.FeeDropsEstimated,
                    report.FinishedAt,
                    finished.Payer));

                // Taken from Append's own return value, not from a shared property read afterward:
                // IPublicationHistory.Append is documented to return only its own call's outcome, so
                // this cannot read a different, overlapping append's verdict as if it were this
                // run's.
                //
                // Branches on Recorded, not merely on whether Note happens to be set: the one
                // implementation today never returns Recorded: false with a null Note, but that
                // invariant lives in HistoryAppendResult's own documentation, not in anything the
                // compiler enforces, and IPublicationHistory now admits other implementations. A
                // future one that violated it must not have "the publication was not recorded" pass
                // through here silently just because it forgot to explain why.
                if (!appended.Recorded)
                {
                    lock (_gate)
                    {
                        if (_generation == generation)
                        {
                            _historyNote = appended.Note
                                ?? "The publication succeeded, but could not be recorded in your history.";
                        }
                    }
                }
                else if (appended.Note is not null)
                {
                    lock (_gate)
                    {
                        if (_generation == generation) _historyNote = appended.Note;
                    }
                }

                // History holds the link now, so the run's state has nothing left to protect, and a
                // record the person later forgets cannot come back from it (Spec question 22).
                if (appended.Recorded) DeleteState(finishedPath!);
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
            DateTimeOffset finishedAt = _time.GetUtcNow();

            // Before the state is published: a screen that sees the run stop may offer Resume at once, and
            // a resume reads this figure from the file.
            RecordWorked(attempt, generation, finishedAt);

            lock (_gate)
            {
                _failure = failure;
                _state = finalState;
                _finishedAt = finishedAt;
                _cancellation?.Dispose();
                _cancellation = null;
            }
        }
    }

    /// <summary>What <see cref="PublishErrorMessages.ForRun"/> may say about money, as the run learns it.</summary>
    private sealed class RunAttempt
    {
        public RunFailurePoint Point { get; set; }

        public bool UsesTickets { get; set; }

        public long OwnerReserveDrops { get; set; }

        /// <summary>The channel a Batch run's trace entries go through to the trace log as they happen; null for a run that built no Batch strategy.</summary>
        public RunTraceChannel? Trace { get; set; }

        public string? StatePath { get; set; }
    }

    /// <summary>A one-account run: the code this method held before Batch, resuming on the recorded strategy.</summary>
    private async Task<(PublicationReport Report, PublicationState State, string StatePath)> RunOneAccountAsync(
        PublishRequest request, IPublicationSource source, string? statePath, PublicationState? recorded, ILedgerConnection connection, RunAttempt attempt,
        int generation, CancellationToken cancellationToken)
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
            connection.Source, request.Account, source.Length, estimateOptions, cancellationToken).ConfigureAwait(false);

        // PublishPolicy.Create's own remarks say every caller checks this first and that
        // reaching it otherwise is a defect -- but EstimateModel's own check only ever gates
        // a fresh Start: it runs once, against a screen the person is looking at, and this
        // estimate is a second, independent one taken right here because the node's price
        // can have moved since. UnfinishedModel.ResumeAsync reaches PublicationRun.Resume,
        // and so this method, without ever going through EstimateModel at all, so checking
        // only there would leave a resumed run free to reach PublishPolicy.Create's own
        // defect exception. Checked again here instead, which covers both callers with the
        // one sentence this application actually has for it, before either one reveals a key
        // or spends anything on a strategy this price can never be used to build.
        if (estimate.FeeDropsPerTransaction > int.MaxValue) throw new FeeTooLargeForApplicationException();

        attempt.UsesTickets = recorded?.Strategy is { } name ? name == PublicationState.TicketStrategyName : PublishPolicy.UsesTickets(estimate);
        attempt.OwnerReserveDrops = estimate.OwnerReserveDrops;

        // Pinned to this run's generation, like every other write after Begin: a newer run's
        // snapshot must not inherit this one's answer.
        lock (_gate)
        {
            if (_generation == generation)
            {
                _usesTickets = attempt.UsesTickets;
                _ownerReserveDrops = attempt.OwnerReserveDrops;
            }
        }

        SessionOptions options = new()
        {
            StateDirectory = paths.StateDirectory,
            TicketsPerBatch = PublishPolicy.TicketsPerBatch,
            BudgetDrops = estimate.RequiredDrops,
            SourcePath = request.SourcePath,
            Approval = request.Approval,
            ApproveResume = ApproveResume(request, generation),
        };

        VaultSigner signer = await VaultSigner.CreateAsync(vault, request.KeyReference, cancellationToken, autoLock).ConfigureAwait(false);
        ISubmissionStrategy probe = PublishPolicy.CreateFor(recorded?.Strategy, estimate, connection, signer);

        try
        {
            PublicationSession session = statePath is null
                ? await PublicationSession.StartAsync(
                    probe, connection.Source, source, request.Type, request.Name, request.Account,
                    request.KeyReference, request.Network, options, new SynchronousProgress(Observe), cancellationToken).ConfigureAwait(false)
                : await PublicationSession.ResumeAsync(
                    probe, connection.Source, source, statePath, options, new SynchronousProgress(Observe), cancellationToken)
                    .ConfigureAwait(false);

            RecordStatePath(generation, session.StatePath);
            attempt.StatePath = session.StatePath;
            attempt.Point = RunFailurePoint.WhileSubmitting;

            PublicationReport report = await session.RunAsync(new SynchronousProgress(Observe), cancellationToken).ConfigureAwait(false);
            return (report, session.State, session.StatePath);
        }
        finally
        {
            // Disposal is cleanup, not part of what the run is about, and its own outcome
            // must never be able to override the one just decided: a paid publication
            // that already has its report in hand must not be reported Failed with the
            // report thrown away because closing the strategy afterwards happened to throw.
            await DisposeQuietlyAsync(probe).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A Batch run (spec 6.1): both keys from the vault, the strategy built by the session through
    /// <see cref="PublishPolicy.CreateBatch"/>. A resume passes the payer the request names -- the
    /// recorded one, or the replacement the person chose (spec B18) -- and reports the fence wait.
    /// </summary>
    /// <remarks>
    /// Both signers keep their derived wallet for the run, because a Batch run signs far more often than a
    /// one-account run does, and the wallet is dropped on every way out of this method, including a
    /// start or a resume the session refuses: a cached wallet outlives a lock that was undone and an entry
    /// that was deleted, so nothing but <see cref="VaultSigner.ClearCache"/> bounds it.
    /// </remarks>
    private async Task<(PublicationReport Report, PublicationState State, string StatePath)> RunBatchAsync(
        PublishRequest request, IPublicationSource source, string? statePath, ILedgerConnection connection, RunAttempt attempt, int generation,
        CancellationToken cancellationToken)
    {
        string payer = request.PayerAccount ?? throw new InvalidOperationException("a Batch run's request names its payer");
        string payerReference = request.PayerReference ?? throw new InvalidOperationException("a Batch run's request names its payer's key");

        VaultSigner? writerSigner = null;
        VaultSigner? payerSigner = null;
        BatchStrategy? built = null;

        try
        {
            VaultSigner writer = writerSigner = await VaultSigner
                .CreateAsync(vault, request.KeyReference, cancellationToken, autoLock, cacheWallet: true).ConfigureAwait(false);
            BatchSignerCreated?.Invoke(writer);

            VaultSigner payerKey = payerSigner = await VaultSigner
                .CreateAsync(vault, payerReference, cancellationToken, autoLock, cacheWallet: true).ConfigureAwait(false);
            BatchSignerCreated?.Invoke(payerKey);

            BatchStrategy Build(BatchBudget budget, IBatchFenceRecorder fence)
            {
                RunTraceChannel trace = new(lines => _traceLog.AppendAsync(lines, CancellationToken.None));

                // Handed over before the strategy is built, so a build that throws still has its channel completed with the run.
                attempt.Trace = trace;
                trace.Post(string.Create(CultureInfo.InvariantCulture,
                    $"{_time.GetUtcNow():O} batch run {StateFileOf(attempt)} network={request.Network} writer={request.Account} payer={request.PayerAccount} bytes={source.Length}"));

                BatchStrategyOptions options = (batchOptions ?? new BatchStrategyOptions()) with
                {
                    TraceListener = entry => trace.Post(string.Create(CultureInfo.InvariantCulture, $"+{entry.Elapsed.TotalSeconds:F3} {entry.Event}")),
                };

                built = PublishPolicy.CreateBatch(budget, fence, connection, writer, payerKey, options);
                return built;
            }

            TimeSpan fencePoll = batchOptions?.ChainCheck ?? TimeSpan.FromSeconds(1);
            PublicationSession session;

            if (statePath is null)
            {
                SessionOptions options = new()
                {
                    StateDirectory = paths.StateDirectory,
                    SourcePath = request.SourcePath,
                    FencePoll = fencePoll,

                    // The approval is this run's ceiling (spec S25). A budget priced from a fresh estimate
                    // would refuse an approval whose ceiling is above the current fee unit, which is the
                    // case when the unit fell since it was given and the very case it exists to allow.
                    Approval = request.Approval,
                };

                session = await PublicationSession.StartAsync(
                    Build, connection.Source, source, request.Type, request.Name, request.Account, request.KeyReference,
                    payer, payerReference, request.Network, options, new SynchronousProgress(Observe), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                SessionOptions options = new()
                {
                    StateDirectory = paths.StateDirectory,
                    SourcePath = request.SourcePath,
                    FencePoll = fencePoll,
                    ApproveResume = ApproveResume(request, generation),
                };

                session = await PublicationSession.ResumeAsync(
                    Build, connection.Source, source, statePath, payer, payerReference, options,
                    new SynchronousProgress(Observe), cancellationToken).ConfigureAwait(false);
            }

            RecordStatePath(generation, session.StatePath);
            attempt.StatePath = session.StatePath;
            attempt.Point = RunFailurePoint.WhileSubmitting;

            PublicationReport report = await session.RunAsync(new SynchronousProgress(Observe), cancellationToken).ConfigureAwait(false);
            return (report, session.State, session.StatePath);
        }
        finally
        {
            writerSigner?.ClearCache();
            payerSigner?.ClearCache();

            if (built is not null) await DisposeQuietlyAsync(built).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Called with each signer a Batch run creates, before anything is signed with it. A seam for the
    /// tests, which keep the signers to check that the run dropped their cached wallets.
    /// </summary>
    internal Action<VaultSigner>? BatchSignerCreated { get; set; }

    /// <summary>
    /// Ends a Batch run's trace, however the run ended: the end line names the state file, so a report can
    /// be matched to a run even when a fresh run's header could not, and says how the run ended. The
    /// entries went to the log as they happened (<see cref="RunTraceChannel"/>); this waits until they are
    /// written. A run that never built a strategy has no trace and writes nothing.
    /// </summary>
    private async Task WriteTraceAsync(RunAttempt attempt, PublicationRunState finalState, Exception? caught)
    {
        if (attempt.Trace is not { } trace) return;

        string ending = finalState == PublicationRunState.Failed
            ? caught switch
            {
                XrplFileException e => $" {e.Error}{(e.EngineResult is null ? string.Empty : " " + e.EngineResult)}",
                VaultException e => $" {e.Error}",
                null => string.Empty,
                _ => $" {caught.GetType().Name}",
            }
            : string.Empty;

        trace.Post(string.Create(CultureInfo.InvariantCulture, $"{_time.GetUtcNow():O} end {finalState}{ending} {StateFileOf(attempt)}"));
        await trace.CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>A fresh run's file is named by the session after the strategy is built, so its header cannot name it.</summary>
    private static string StateFileOf(RunAttempt attempt) =>
        attempt.StatePath is null ? "(no state file)" : Path.GetFileName(attempt.StatePath);

    /// <summary>Pins the state file onto this run's snapshot, as every other write after Begin is pinned.</summary>
    private void RecordStatePath(int generation, string statePath)
    {
        lock (_gate)
        {
            if (_generation == generation) _statePath = statePath;
        }
    }

    /// <summary>
    /// Writes the working time to the state of a run that stopped without finishing, so a resume can
    /// carry the whole publication's figure. Only a session that got past its approval writes: one that
    /// was declined, left unanswered or refused before submitting sent nothing and leaves the file as it
    /// was. The figure is shown, never decided on, so a state that cannot be read or written is left alone.
    /// </summary>
    private void RecordWorked(RunAttempt attempt, int generation, DateTimeOffset finishedAt)
    {
        if (attempt is not { Point: RunFailurePoint.WhileSubmitting, StatePath: { } statePath }) return;

        TimeSpan worked;

        lock (_gate)
        {
            if (_generation != generation || _startedAt is not { } startedAt) return;

            worked = _workedBefore + (finishedAt - startedAt - _waited);
        }

        try
        {
            // A state the person forgot between this check and the save would be written again; the
            // window is milliseconds, and the next start-up lists it as an unfinished run they can forget.
            if (!File.Exists(statePath)) return;

            WorkedWriting?.Invoke(statePath);

            PublicationState state = PublicationState.Load(statePath);
            long seconds = (long)Math.Floor(Math.Max(0, worked.TotalSeconds));

            (state with { WorkedSeconds = seconds }).Save(statePath);
        }
        catch (Exception)
        {
            // The figure is shown, never decided on: whatever goes wrong here is no reason to leave the
            // run Running or to replace the outcome it is about to publish.
        }
    }

    /// <summary>
    /// Called with the state file just before the working time is written into it. A seam for the tests,
    /// which make the write fail in ways the file system rarely does.
    /// </summary>
    internal Action<string>? WorkedWriting { get; set; }

    /// <summary>
    /// Removes a finished run's state file. One that cannot be removed now is removed at the next
    /// start-up, which finds its link already listed (<c>PublicationHistory.RecordFinishedRuns</c>, Task 22).
    /// </summary>
    private static void DeleteState(string statePath)
    {
        try
        {
            File.Delete(statePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next start-up.
        }
    }

    /// <summary>
    /// A resume's approval (spec S25), made from the quote of what remains: given at once below the
    /// threshold, asked of the person on the run screen above it. Declining is a cancellation, and so is
    /// leaving the question unanswered for the vault's whole idle window.
    /// </summary>
    private ResumeApproval ApproveResume(PublishRequest request, int generation) => async (quote, cancellationToken) =>
    {
        CostSummary summary = quote.Batch is { } batch ? CostSummary.OfResume(batch, request) : CostSummary.Of(quote.OneAccount!, request);
        SpendingApproval approval = quote.Batch is { } priced
            ? SpendingApproval.For(priced, quote.Writer, quote.Payer!)
            : SpendingApproval.For(quote.OneAccount!, quote.Writer);

        if (!summary.NeedsConfirmation) return approval;

        TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_generation != generation) throw new OperationCanceledException(cancellationToken);
            _confirmation = summary;
            _answer = answer;
        }

        DateTimeOffset shownAt = _time.GetUtcNow();

        try
        {
            using CancellationTokenRegistration cancelled = cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken));

            // The lease keeps the vault open while the run works, and a person who walked away is not work:
            // past the vault's own idle window an unanswered question is a decline, so the run ends
            // resumable and the lease stops touching (spec S14).
            bool approved;

            try
            {
                approved = autoLock is null
                    ? await answer.Task.ConfigureAwait(false)
                    : await answer.Task.WaitAsync(autoLock.Idle, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new OperationCanceledException("the resume's cost was not answered within the vault's idle window", cancellationToken);
            }

            if (!approved) throw new OperationCanceledException("the person declined the resume's cost", cancellationToken);

            return approval;
        }
        finally
        {
            lock (_gate)
            {
                _confirmation = null;
                _answer = null;

                if (_generation == generation) _waited += _time.GetUtcNow() - shownAt;
            }
        }
    };

    /// <summary>A resume reads the file the run recorded, held the same way an estimate holds it (spec S7, S24).</summary>
    private IPublicationSource OpenRecorded(PublishRequest request)
    {
        string path = request.SourcePath ?? throw new InvalidOperationException("a resume's request names the file to read");

        try
        {
            return _openSource(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SourceUnreadableException(path, ex);
        }
    }

    /// <summary>Local control flow, like <see cref="FeeTooLargeForApplicationException"/>: the recorded file could not be opened.</summary>
    private sealed class SourceUnreadableException(string path, Exception inner) : Exception(path, inner)
    {
        public string Path { get; } = path;
    }

    /// <summary>Turns a failed read of the held file into <see cref="SourceReadFailedException"/>, so the run names the file and not the state.</summary>
    private sealed class ReadReportingSource(IPublicationSource inner, string? path) : IPublicationSource
    {
        public long Length => inner.Length;

        public int ReadChunk(long index, Span<byte> destination)
        {
            try
            {
                return inner.ReadChunk(index, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SourceReadFailedException(path, ex);
            }
        }

        public async ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken)
        {
            try
            {
                return await inner.ReadChunkAsync(index, destination, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SourceReadFailedException(path, ex);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Local control flow: the held file failed a read during the run (a drive removed, a share gone, the file cut short).</summary>
    private sealed class SourceReadFailedException(string? path, Exception inner) : Exception(path, inner)
    {
        public string? Path { get; } = path;
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
    /// Reports straight through on the calling thread, not through <see cref="Progress{T}"/>.
    /// <see cref="Progress{T}"/> captures whatever <see cref="SynchronizationContext"/> is current
    /// when it is constructed and posts every report there, falling back to the thread pool when
    /// there is none -- and the sink built here is constructed inside <see cref="RunAsync"/>, which
    /// itself runs under <see cref="Task.Run(Func{Task})"/>, off any synchronization context. A
    /// <see cref="Progress{T}"/> built there would therefore post every report to the thread pool,
    /// landing on some other thread at some later, unpredictable time rather than being observable
    /// the instant it is reported -- never reaching a MAUI Blazor window synchronously with the
    /// call that raised it.
    /// </summary>
    private sealed class SynchronousProgress(Action<PublishProgress> report) : IProgress<PublishProgress>
    {
        public void Report(PublishProgress value) => report(value);
    }

    /// <summary>
    /// Thrown, and caught a few lines below, purely as local control flow: a way to unwind out of
    /// <see cref="RunAsync"/>'s nested try/finally blocks -- so the connection already opened for
    /// the estimate is still disposed by the finally around it -- to the one place this run's
    /// terminal state is decided, without duplicating that decision at the point the price is
    /// found to be unusable. Never crosses this class's own boundary.
    /// </summary>
    private sealed class FeeTooLargeForApplicationException : Exception;
}
