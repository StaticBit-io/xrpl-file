using XrplFile.App.Core.Localization;

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
    /// Bumped by every estimate, hand-off and discard: an estimate keeps the source it opened, and
    /// publishes its figures, only when nothing of the kind happened while it ran, so a screen left
    /// mid-estimate leaves no file open and an older estimate cannot land next to a newer one's file.
    /// </summary>
    private int _generation;

    public PublicationEstimate? Estimate { get; private set; }

    /// <summary>The node the estimate came from, which is also the node a run would go to.</summary>
    public string? NodeUrl { get; private set; }

    public UiText? Message { get; private set; }

    /// <summary>The two-account estimate, when the request has a payer (spec 6.2); otherwise null.</summary>
    public BatchPublicationEstimate? BatchEstimate { get; private set; }

    /// <summary>"Batch, paid by &lt;label&gt;." for a Batch request, otherwise null (spec 6.2).</summary>
    public UiText? MethodLine { get; private set; }

    /// <summary>
    /// The request a run started from this estimate uses (<see cref="PublishPolicy.Effective"/>):
    /// what was estimated, which is not always what was asked for. Null before the first estimate.
    /// </summary>
    public PublishRequest? Request { get; private set; }

    /// <summary>
    /// <see cref="EstimateText.TooSmallForBatch"/>, said under the estimate when a chosen payer was dropped
    /// because the file is too small for Batch; otherwise null.
    /// </summary>
    public UiText? Note { get; private set; }

    private SpendingApproval? _approval;

    /// <summary>The cost-and-time summary of the current estimate, or null before one.</summary>
    public CostSummary? Summary { get; private set; }

    /// <summary>Whether the summary has to be confirmed before Publish (spec S12).</summary>
    public bool NeedsConfirmation => Summary?.NeedsConfirmation == true;

    /// <summary>Whether the person ticked the checkbox for the current estimate.</summary>
    public bool Confirmed { get; private set; }

    public void Confirm(bool understood) => Confirmed = understood;

    public bool CanPublish => IsAffordable(Estimate, BatchEstimate) && (!NeedsConfirmation || Confirmed);

    /// <summary>Whether the estimate could be published once confirmed: the accounts cover it and the run can be priced and placed.</summary>
    private static bool IsAffordable(PublicationEstimate? estimate, BatchPublicationEstimate? batchEstimate) =>
        batchEstimate is { } batch
            ? batch.WriterCovers && batch.PayerCovers
            : estimate is not null
                && estimate.BalanceCovers
                && estimate.FeeDropsPerTransaction <= int.MaxValue
                && !PublishPolicy.HasNoTicketRoom(estimate);

    /// <summary>
    /// What a run started from this estimate may spend (spec S25); null while the estimate cannot be
    /// published, which includes a large one the person has not confirmed.
    /// </summary>
    public SpendingApproval? Approval => CanPublish ? _approval : null;

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

            // With no file held there is nothing the estimate could be published from.
            Clear();
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
            Clear();
        }

        if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);

        IPublicationSource source;

        try
        {
            source = _open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Publish(generation, new Outcome { Message = PublishText.FileUnreadable }, held: null);
            return;
        }

        // The size checked and priced is the size of what is held, whatever the picker measured earlier.
        if (SizeRefusal(source.Length, request.PayerAccount) is { } refusal)
        {
            Publish(generation, new Outcome { Message = refusal }, held: null);
            await source.DisposeAsync().ConfigureAwait(false);
            return;
        }

        bool kept = false;

        try
        {
            Outcome outcome = await EstimateHeldAsync(request with { Size = source.Length }, cancellationToken).ConfigureAwait(false);

            kept = Publish(generation, outcome, source);
        }
        finally
        {
            if (!kept) await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The size policy of <see cref="PublishModel"/> (spec S1, S16) applied to the file the estimate
    /// holds: the picker measured it earlier, and a file can grow between picking and estimating.
    /// </summary>
    private static UiText? SizeRefusal(long length, string? payerAccount)
    {
        if (length > PublishModel.MaxPublicationBytes) return PublishText.ErrorTooLarge;
        if (length > PublishModel.TicketPathMaxBytes && payerAccount is null) return PublishText.PayerRequired;

        return null;
    }

    /// <summary>
    /// What one estimate worked out, kept apart from the model until <see cref="Publish"/> finds the
    /// estimate still current: an older one finishing late must not land its figures and approval next
    /// to a newer one's held file.
    /// </summary>
    private sealed class Outcome
    {
        public PublishRequest? Request { get; init; }

        public UiText? Note { get; init; }

        public string? NodeUrl { get; set; }

        public PublicationEstimate? Estimate { get; set; }

        public BatchPublicationEstimate? BatchEstimate { get; set; }

        public UiText? Message { get; set; }

        public UiText? MethodLine { get; set; }

        public CostSummary? Summary { get; set; }

        public SpendingApproval? Approval { get; set; }
    }

    /// <summary>Called under <see cref="_gate"/>.</summary>
    private void Clear()
    {
        Estimate = null;
        NodeUrl = null;
        Message = null;
        BatchEstimate = null;
        MethodLine = null;
        Request = null;
        Note = null;
        Summary = null;
        Confirmed = false;
        _approval = null;
    }

    /// <summary>
    /// Takes an outcome and the file it was made from, but only when no other estimate, hand-off or
    /// discard happened while it ran. True when the model now holds the file.
    /// </summary>
    private bool Publish(int generation, Outcome outcome, IPublicationSource? held)
    {
        lock (_gate)
        {
            if (generation != _generation) return false;

            Request = outcome.Request;
            Note = outcome.Note;
            NodeUrl = outcome.NodeUrl;
            Estimate = outcome.Estimate;
            BatchEstimate = outcome.BatchEstimate;
            Message = outcome.Message;
            MethodLine = outcome.MethodLine;
            Summary = outcome.Summary;
            Confirmed = false;
            _approval = outcome.Approval;
            _held = held;

            return held is not null;
        }
    }

    private async Task<Outcome> EstimateHeldAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        PublishRequest effective = PublishPolicy.Effective(request);
        Outcome outcome = new()
        {
            Request = effective,
            Note = ReferenceEquals(effective, request) ? null : EstimateText.TooSmallForBatch,
        };
        request = effective;

        if (request.PayerAccount is { } payer)
        {
            await EstimateBatchAsync(outcome, request, payer, cancellationToken).ConfigureAwait(false);
            return outcome;
        }

        PublicationEstimate estimate;

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(request.Network, cancellationToken).ConfigureAwait(false);

            outcome.NodeUrl = connection.Url;

            SessionOptions options = new()
            {
                StateDirectory = paths.StateDirectory,
                TicketsPerBatch = PublishPolicy.TicketsPerBatch,
            };

            estimate = await PublicationSession.EstimateAsync(
                connection.Source, request.Account, request.Size, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (XrplFileException ex)
        {
            outcome.Message = PublishErrorMessages.For(ex);
            return outcome;
        }

        outcome.Estimate = estimate;
        outcome.Summary = CostSummary.Of(estimate, request);

        if (estimate.FeeDropsPerTransaction > int.MaxValue)
        {
            // Both strategy option types take an int fee, and the run prices its strategy from this
            // number so the session does not refuse it with FeeMismatch. A price this large is not a
            // situation the protocol produces; it is one this application says no to rather than
            // wraps around. PublicationRun.RunAsync re-checks this same thing for itself before a
            // resume (which never reaches this model at all) and uses this exact sentence too.
            outcome.Message = PublishErrorMessages.FeeTooLargeForApplication;
            return outcome;
        }

        if (PublishPolicy.HasNoTicketRoom(estimate))
        {
            outcome.Message = UiText.Of(EstimateText.NoTicketRoom, ("limit", TicketStrategy.MaxLiveTickets));
            return outcome;
        }

        if (!estimate.BalanceCovers)
        {
            outcome.Message = NotAffordable(estimate);
        }

        SetApprovalIfPublishable(outcome);
        return outcome;
    }

    private async Task EstimateBatchAsync(Outcome outcome, PublishRequest request, string payer, CancellationToken cancellationToken)
    {
        BatchPublicationEstimate estimate;

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(request.Network, cancellationToken).ConfigureAwait(false);

            outcome.NodeUrl = connection.Url;

            SessionOptions options = new() { StateDirectory = paths.StateDirectory };

            estimate = await PublicationSession.EstimateAsync(
                connection.Source, request.Account, payer, request.Size, options, cancellationToken).ConfigureAwait(false);
        }
        catch (XrplFileException ex)
        {
            // Nothing has started, so "listed under unfinished runs" would be false (Spec question 6).
            outcome.Message = ex.Error == XrplFileError.FeeTooHigh ? PublishErrorMessages.FeeTooHighNow : PublishErrorMessages.For(ex);
            return;
        }

        outcome.BatchEstimate = estimate;
        outcome.MethodLine = UiText.Of(EstimateText.MethodBatch, ("payer", request.PayerLabel ?? payer));
        outcome.Summary = CostSummary.Of(estimate, request);
        outcome.Message = ShortfallOf(estimate, request);
        SetApprovalIfPublishable(outcome);
    }

    private static void SetApprovalIfPublishable(Outcome outcome)
    {
        // Made before the confirmation: Approval hands it out only once CanPublish agrees.
        if (!IsAffordable(outcome.Estimate, outcome.BatchEstimate)) return;

        PublishRequest request = outcome.Request!;

        outcome.Approval = outcome.BatchEstimate is { } batch
            ? SpendingApproval.For(batch, request.Account, request.PayerAccount!)
            : SpendingApproval.For(outcome.Estimate!, request.Account);
    }

    /// <summary>The account that is short, by label, with what it needs and what it can spend (spec 6.2); null when both cover.</summary>
    public static UiText? ShortfallOf(BatchPublicationEstimate estimate, PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(request);

        string writer = request.WriterLabel ?? request.Account;
        string payer = request.PayerLabel ?? request.PayerAccount ?? string.Empty;

        if (!estimate.WriterCovers && !estimate.PayerCovers)
        {
            return UiText.Of(
                EstimateText.ShortfallBoth,
                ("writer", writer),
                ("writerRequired", new XrpAmount(estimate.WriterRequiredDrops)),
                ("writerSpendable", new XrpAmount(estimate.WriterSpendableDrops)),
                ("payer", payer),
                ("payerRequired", new XrpAmount(estimate.PayerRequiredDrops)),
                ("payerSpendable", new XrpAmount(estimate.PayerSpendableDrops)));
        }

        if (!estimate.WriterCovers)
        {
            return UiText.Of(
                EstimateText.ShortfallWriter,
                ("name", writer),
                ("required", new XrpAmount(estimate.WriterRequiredDrops)),
                ("spendable", new XrpAmount(estimate.WriterSpendableDrops)));
        }

        if (!estimate.PayerCovers)
        {
            return UiText.Of(
                EstimateText.ShortfallPayer,
                ("name", payer),
                ("required", new XrpAmount(estimate.PayerRequiredDrops)),
                ("spendable", new XrpAmount(estimate.PayerSpendableDrops)));
        }

        return null;
    }

    /// <summary>
    /// The sentence for an account that cannot cover the run. When the estimate holds a ticket reserve,
    /// which the ledger gives back as tickets are used up, it says what part of the requirement that is,
    /// so a person reading "needs 51 XRP" learns that most of it is not spent; with none, it describes
    /// no money that is not there.
    /// </summary>
    private static UiText NotAffordable(PublicationEstimate estimate)
    {
        XrpAmount required = new(estimate.RequiredDrops);
        XrpAmount spendable = new(estimate.SpendableDrops);

        if (estimate.PeakReserveDrops <= 0) return UiText.Of(EstimateText.NotAffordable, ("required", required), ("spendable", spendable));

        TextId phrase = estimate.PeakReserveDrops * 2 > estimate.RequiredDrops
            ? EstimateText.NotAffordableMostReserve
            : EstimateText.NotAffordableSomeReserve;

        return UiText.Of(phrase, ("required", required), ("spendable", spendable), ("reserve", new XrpAmount(estimate.PeakReserveDrops)));
    }
}
