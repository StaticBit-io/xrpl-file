namespace XrplFile.App.Core;

/// <summary>
/// The estimate screen (design 4.2). The figures are the library's, unaltered, even when the run
/// will use the sequence strategy (design F9): the estimate is ticket-shaped — it counts mints and a
/// peak ticket reserve a sequence run never pays — so showing it unchanged overstates the cost and
/// never understates it, and a second estimator would be one nothing validates.
/// </summary>
public sealed class EstimateModel(IConnectionFactory connections, RunPaths paths)
{
    public PublicationEstimate? Estimate { get; private set; }

    /// <summary>The node the estimate came from, which is also the node a run would go to.</summary>
    public string? NodeUrl { get; private set; }

    public string? Message { get; private set; }

    /// <summary>The two-account estimate, when the request has a payer (spec 6.2); otherwise null.</summary>
    public BatchPublicationEstimate? BatchEstimate { get; private set; }

    /// <summary>"Batch, paid by &lt;label&gt;." for a Batch request, otherwise null (spec 6.2).</summary>
    public string? MethodLine { get; private set; }

    public bool CanPublish =>
        BatchEstimate is { } batch
            ? batch.WriterCovers && batch.PayerCovers
            : Estimate is not null
                && Estimate.BalanceCovers
                && Estimate.FeeDropsPerTransaction <= int.MaxValue
                && !PublishPolicy.HasNoTicketRoom(Estimate);

    public async Task EstimateAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Estimate = null;
        NodeUrl = null;
        Message = null;
        BatchEstimate = null;
        MethodLine = null;

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
                connection.Source, request.Account, request.File.Length, options, cancellationToken)
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
                connection.Source, request.Account, payer, request.File.Length, options, cancellationToken).ConfigureAwait(false);
        }
        catch (XrplFileException ex)
        {
            // Nothing has started, so "listed under unfinished runs" would be false (Spec question 6).
            Message = ex.Error == XrplFileError.FeeTooHigh ? PublishErrorMessages.FeeTooHighNow : PublishErrorMessages.For(ex);
            return;
        }

        MethodLine = $"Batch, paid by {request.PayerLabel ?? payer}.";
        Message = ShortfallOf(BatchEstimate, request);
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
