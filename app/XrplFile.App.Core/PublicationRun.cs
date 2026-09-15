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
public sealed record RunSnapshot(
    PublicationRunState State,
    PublishProgress? Progress,
    PublicationReport? Report,
    string? Failure,
    string? Name,
    string? Network);

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
    private PublicationRunState _state = PublicationRunState.Idle;
    private PublishProgress? _progress;
    private PublicationReport? _report;
    private string? _failure;
    private string? _name;
    private string? _network;

    /// <summary>Completes when the current run has finished, however it finished.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public RunSnapshot Snapshot
    {
        get { lock (_gate) return new RunSnapshot(_state, _progress, _report, _failure, _name, _network); }
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
            running = Completion;
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
        }
    }

    private bool Begin(PublishRequest request, string? statePath)
    {
        CancellationTokenSource cancellation = new();

        lock (_gate)
        {
            if (_state == PublicationRunState.Running) return false;

            _cancellation?.Dispose();
            _cancellation = cancellation;
            _state = PublicationRunState.Running;
            _progress = null;
            _report = null;
            _failure = null;
            _name = request.Name;
            _network = request.Network;
            Completion = Task.Run(() => RunAsync(request, statePath, cancellation.Token));
        }

        return true;
    }

    private async Task RunAsync(PublishRequest request, string? statePath, CancellationToken cancellationToken)
    {
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

            PublicationReport report = await session
                .RunAsync(new SynchronousProgress(Observe), cancellationToken).ConfigureAwait(false);

            history.Append(new PublicationRecord(
                report.Link.ToString(),
                request.Name,
                request.File.Length,
                request.Type,
                request.Network,
                request.Account,
                report.Transactions,
                report.FeeDropsEstimated,
                report.FinishedAt));

            lock (_gate)
            {
                _report = report;
                _state = PublicationRunState.Succeeded;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) _state = PublicationRunState.Cancelled;
        }
        catch (XrplFileException ex)
        {
            Fail(PublishErrorMessages.For(ex));
        }
        catch (VaultException ex)
        {
            Fail(VaultErrorMessages.For(ex.Error));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail("The publication's state could not be written to disk, so the run was stopped.");
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

    private void Fail(string message)
    {
        lock (_gate)
        {
            _failure = message;
            _state = PublicationRunState.Failed;
        }
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
