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

    public bool CanPublish =>
        Estimate is not null
        && Estimate.BalanceCovers
        && Estimate.FeeDropsPerTransaction <= int.MaxValue
        && !PublishPolicy.HasNoTicketRoom(Estimate);

    public async Task EstimateAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Estimate = null;
        NodeUrl = null;
        Message = null;

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
