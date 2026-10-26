namespace XrplFile.App.Core;

/// <summary>
/// The estimate screen (design 4.2). The figures are the library's, unaltered, even when the run
/// will use the sequence strategy (design F9): the estimate is ticket-shaped — it counts mints and a
/// peak ticket reserve a sequence run never pays — so showing it unchanged overstates the cost and
/// never understates it, and a second estimator would be one nothing validates.
/// </summary>
public sealed class EstimateModel(IConnectionFactory connections, RunPaths paths, Func<string, IPublicationSource>? open = null)
{
    private readonly Func<string, IPublicationSource> _open = open ?? (path => FilePublicationSource.Open(path));
    private readonly Lock _gate = new();
    private IPublicationSource? _held;

    /// <summary>
    /// Bumped by every estimate, hand-off and discard: an estimate keeps the source it opened only
    /// when nothing of the kind happened while it ran, so a screen left mid-estimate leaves no file open.
    /// </summary>
    private int _generation;

    public PublicationEstimate? Estimate { get; private set; }

    /// <summary>The node the estimate came from, which is also the node a run would go to.</summary>
    public string? NodeUrl { get; private set; }

    public string? Message { get; private set; }

    /// <summary>The two-account estimate, when the request has a payer (spec 6.2); otherwise null.</summary>
    public BatchPublicationEstimate? BatchEstimate { get; private set; }

    /// <summary>"Batch, paid by &lt;label&gt;." for a Batch request, otherwise null (spec 6.2).</summary>
    public string? MethodLine { get; private set; }

    /// <summary>Said under the estimate when a chosen payer was dropped because the file is too small for Batch.</summary>
    public const string TooSmallForBatchNote = "Too small for Batch — the writer pays its few transactions itself.";

    /// <summary>
    /// The request a run started from this estimate uses (<see cref="PublishPolicy.Effective"/>):
    /// what was estimated, which is not always what was asked for. Null before the first estimate.
    /// </summary>
    public PublishRequest? Request { get; private set; }

    /// <summary><see cref="TooSmallForBatchNote"/> when the payer was dropped, otherwise null.</summary>
    public string? Note { get; private set; }

    public bool CanPublish =>
        BatchEstimate is { } batch
            ? batch.WriterCovers && batch.PayerCovers
            : Estimate is not null
                && Estimate.BalanceCovers
                && Estimate.FeeDropsPerTransaction <= int.MaxValue
                && !PublishPolicy.HasNoTicketRoom(Estimate);

    /// <summary>What a run started from this estimate may spend (spec S25); null while the estimate cannot be published.</summary>
    public SpendingApproval? Approval { get; private set; }

    /// <summary>
    /// The estimated request with the held file and the approval, for a run to take (spec S24, S25).
    /// The model lets go of the file: from here on it is the caller's, and a discard does not touch it.
    /// Null when there is nothing to publish.
    /// </summary>
    public PublishRequest? HandOff()
    {
        lock (_gate)
        {
            if (_held is not { } source || Request is not { } request || !CanPublish || Approval is not { } approval) return null;

            _held = null;
            _generation++;
            return request with { Source = source, Approval = approval };
        }
    }

    /// <summary>Lets the held file go (spec S24): Back, another file, another key, or leaving the screen.</summary>
    public async Task DiscardAsync()
    {
        IPublicationSource? held;

        lock (_gate)
        {
            held = _held;
            _held = null;
            _generation++;
        }

        if (held is not null) await held.DisposeAsync().ConfigureAwait(false);
    }

    public async Task EstimateAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SourcePath is not { } path)
        {
            throw new ArgumentException("an estimate is of a file on disk, which it holds until the run takes it", nameof(request));
        }

        int generation;
        IPublicationSource? previous;

        lock (_gate)
        {
            generation = ++_generation;
            previous = _held;
            _held = null;
        }

        if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);

        Estimate = null;
        NodeUrl = null;
        Message = null;
        BatchEstimate = null;
        MethodLine = null;
        Approval = null;

        IPublicationSource source;

        try
        {
            source = _open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "That file could not be read.";
            return;
        }

        bool kept = false;

        try
        {
            // The size priced is the size of what is held, whatever the picker measured earlier.
            await EstimateHeldAsync(request with { Size = source.Length }, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (generation == _generation)
                {
                    _held = source;
                    kept = true;
                }
            }
        }
        finally
        {
            if (!kept) await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task EstimateHeldAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        PublishRequest effective = PublishPolicy.Effective(request);
        Request = effective;
        Note = ReferenceEquals(effective, request) ? null : TooSmallForBatchNote;
        request = effective;

        if (request.PayerAccount is { } payer)
        {
            await EstimateBatchAsync(request, payer, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(request.Network, cancellationToken).ConfigureAwait(false);

            NodeUrl = connection.Url;

            SessionOptions options = new()
            {
                StateDirectory = paths.StateDirectory,
                TicketsPerBatch = PublishPolicy.TicketsPerBatch,
            };

            Estimate = await PublicationSession.EstimateAsync(
                connection.Source, request.Account, request.Size, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (XrplFileException ex)
        {
            Message = PublishErrorMessages.For(ex);
            return;
        }

        if (Estimate.FeeDropsPerTransaction > int.MaxValue)
        {
            // Both strategy option types take an int fee, and the run prices its strategy from this
            // number so the session does not refuse it with FeeMismatch. A price this large is not a
            // situation the protocol produces; it is one this application says no to rather than
            // wraps around. PublicationRun.RunAsync re-checks this same thing for itself before a
            // resume (which never reaches this model at all) and uses this exact sentence too.
            Message = PublishErrorMessages.FeeTooLargeForApplication;
            return;
        }

        if (PublishPolicy.HasNoTicketRoom(Estimate))
        {
            Message =
                $"This account already holds the {TicketStrategy.MaxLiveTickets} tickets the XRP Ledger allows, none of them "
                + "recognisably left by this application, so a file this size has nowhere to put its own. Publish a smaller file, "
                + "which needs no tickets, or use another account.";
            return;
        }

        if (!Estimate.BalanceCovers)
        {
            Message =
                $"This publication needs {XrpAmount.Format(Estimate.RequiredDrops)} and the account can spend "
                + $"{XrpAmount.Format(Estimate.SpendableDrops)}. {ReserveSentence(Estimate)}"
                + "Fund it, or publish a smaller file.";
        }

        SetApprovalIfPublishable();
    }

    private async Task EstimateBatchAsync(PublishRequest request, string payer, CancellationToken cancellationToken)
    {
        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(request.Network, cancellationToken).ConfigureAwait(false);

            NodeUrl = connection.Url;

            SessionOptions options = new() { StateDirectory = paths.StateDirectory };

            BatchEstimate = await PublicationSession.EstimateAsync(
                connection.Source, request.Account, payer, request.Size, options, cancellationToken).ConfigureAwait(false);
        }
        catch (XrplFileException ex)
        {
            // Nothing has started, so "listed under unfinished runs" would be false (Spec question 6).
            Message = ex.Error == XrplFileError.FeeTooHigh ? PublishErrorMessages.FeeTooHighNow : PublishErrorMessages.For(ex);
            return;
        }

        MethodLine = $"Batch, paid by {request.PayerLabel ?? payer}.";
        Message = ShortfallOf(BatchEstimate, request);
        SetApprovalIfPublishable();
    }

    private void SetApprovalIfPublishable()
    {
        if (!CanPublish) return;

        Approval = BatchEstimate is { } batch
            ? SpendingApproval.For(batch, Request!.Account, Request.PayerAccount!)
            : SpendingApproval.For(Estimate!, Request!.Account);
    }

    /// <summary>The account that is short, by label, with what it needs and what it can spend (spec 6.2); null when both cover.</summary>
    public static string? ShortfallOf(BatchPublicationEstimate estimate, PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(request);

        List<string> lines = [];

        if (!estimate.WriterCovers)
        {
            lines.Add($"The writer {request.WriterLabel ?? request.Account} needs {XrpAmount.Format(estimate.WriterRequiredDrops)} "
                + $"and can spend {XrpAmount.Format(estimate.WriterSpendableDrops)}.");
        }

        if (!estimate.PayerCovers)
        {
            lines.Add($"The payer {request.PayerLabel ?? request.PayerAccount} needs {XrpAmount.Format(estimate.PayerRequiredDrops)} "
                + $"and can spend {XrpAmount.Format(estimate.PayerSpendableDrops)}.");
        }

        return lines.Count == 0 ? null : string.Join(" ", lines);
    }

    /// <summary>
    /// What part of the requirement is the ticket reserve, which the ledger gives back as tickets are
    /// used up — so a person reading "needs 51 XRP" learns that most of it is not spent. Empty when
    /// the estimate holds no ticket reserve, so the sentence never describes money that is not there.
    /// </summary>
    private static string ReserveSentence(PublicationEstimate estimate)
    {
        if (estimate.PeakReserveDrops <= 0) return string.Empty;

        string share = estimate.PeakReserveDrops * 2 > estimate.RequiredDrops ? "Most" : "Some";

        return $"{share} of it is reserve that comes back as the publication finishes: "
            + $"{XrpAmount.Format(estimate.PeakReserveDrops)} for tickets. ";
    }
}
