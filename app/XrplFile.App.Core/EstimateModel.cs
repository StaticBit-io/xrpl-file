namespace XrplFile.App.Core;

/// <summary>
/// The estimate screen (design 4.2). The figures are the library's, unaltered, even when the run
/// will use the sequence strategy (design F9): the estimate is ticket-shaped — it counts mints and a
/// peak ticket reserve a sequence run never pays — so showing it unchanged overstates the cost and
/// never understates it, and a second estimator would be one nothing validates.
/// </summary>
// `nodes` is accepted so this screen's constructor stays wired the same way as its siblings
// (PublishModel takes the same store) and so a future need here — showing the configured URL
// before a connection is attempted, say — does not change the signature callers already build
// against. Nothing in this class reads it today: IConnectionFactory.ConnectAsync is the one seam
// that resolves a network to a URL (design F10), and NodeUrl below comes back from the connection
// it opens rather than from a second resolution done here.
#pragma warning disable CS9113 // parameter is unread
public sealed class EstimateModel(IConnectionFactory connections, INodeSettingsStore nodes, RunPaths paths)
#pragma warning restore CS9113
{
    public PublicationEstimate? Estimate { get; private set; }

    /// <summary>The node the estimate came from, which is also the node a run would go to.</summary>
    public string? NodeUrl { get; private set; }

    public string? Message { get; private set; }

    public bool CanPublish =>
        Estimate is not null
        && Estimate.BalanceCovers
        && Estimate.FeeDropsPerTransaction <= int.MaxValue;

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
            // wraps around.
            Message = "The node is quoting a price this application cannot use. Try a different node.";
            return;
        }

        if (!Estimate.BalanceCovers)
        {
            Message =
                $"This publication needs {Estimate.RequiredDrops:N0} drops and the account can spend "
                + $"{Estimate.SpendableDrops:N0}. Fund it, or publish a smaller file.";
        }
    }
}
