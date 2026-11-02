using System.Text.Json;
using XrplFile.App.Core;

namespace XrplFile.SitePrice;

/// <summary>What the network charged and reserved when the table was taken.</summary>
public sealed record PriceFees(long OpenLedgerFeeDrops, long BaseReserveDrops, long OwnerReserveDrops);

/// <summary>
/// One file size priced by the publisher's own estimate.
/// </summary>
/// <param name="Bytes">The file's size.</param>
/// <param name="Transactions">
/// Every transaction the run puts in a ledger. Tickets: the estimate's <see cref="PublicationEstimate.Transactions"/>,
/// memos plus the TicketCreate mints. Batch: <see cref="BatchPublicationEstimate.Memos"/> plus the
/// <see cref="BatchPublicationEstimate.Batches"/> that wrap them.
/// </param>
/// <param name="FeeDrops">
/// What the run costs as it goes, nothing retried: <see cref="PublicationEstimate.FeeDropsTotal"/>, or for Batch the
/// payer's and the writer's fees together.
/// </param>
/// <param name="HeldDrops">
/// The reserve the throwaway writer locks while the run lasts: its base reserve, plus for tickets the
/// <see cref="PublicationEstimate.PeakReserveDrops"/> of the live tickets. Batch mints nothing, so only the base reserve is held.
/// The payer is an account the person already has and locks nothing for the run.
/// </param>
/// <param name="ReturnedDrops">
/// What comes back when the writer is deleted: <paramref name="HeldDrops"/> less the AccountDelete fee, which the network
/// sets to one owner reserve and keeps.
/// </param>
/// <param name="Method"><c>batch</c> or <c>tickets</c>.</param>
public sealed record PriceRow(long Bytes, long Transactions, long FeeDrops, long HeldDrops, long ReturnedDrops, string Method);

public sealed record PriceTable(string Network, DateTimeOffset TakenAt, string Node, PriceFees Fees, IReadOnlyList<PriceRow> Rows);

public static class SitePrice
{
    public const string Mainnet = "mainnet";

    // Placeholders only: FreshAccountSource never looks an account up, so no real address is needed.
    private const string Writer = "rrrrrrrrrrrrrrrrrrrrrhoLvTp";
    private const string Payer = "rrrrrrrrrrrrrrrrrrrrBZbvji";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // The file is committed; the same bytes on every platform.
        NewLine = "\n",
    };

    /// <summary>
    /// Prices each size as the publisher would for a person who has a payer to offer: Batch unless the file is
    /// too small for it, in which case the application drops the payer and the writer uses tickets
    /// (<see cref="PublishPolicy.Effective"/>, as <c>EstimateModel</c> does).
    /// </summary>
    /// <param name="source">A node's fees and amendments; accounts are never read from it.</param>
    public static async Task<PriceTable> ComputeAsync(
        ILedgerSource source, IReadOnlyList<long> sizes, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sizes);

        FreshAccountSource fresh = new(source);
        NetworkFees fees = await fresh.FeesAsync(cancellationToken).ConfigureAwait(false);
        List<PriceRow> rows = [];

        foreach (long size in sizes)
        {
            rows.Add(PublishPolicy.TooSmallForBatch(size)
                ? await TicketRowAsync(fresh, size, cancellationToken).ConfigureAwait(false)
                : await BatchRowAsync(fresh, size, cancellationToken).ConfigureAwait(false));
        }

        return new PriceTable(
            Mainnet, at, string.Empty, new PriceFees(fees.OpenLedgerFeeDrops, fees.BaseReserveDrops, fees.OwnerReserveDrops), rows);
    }

    public static string ToJson(PriceTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return JsonSerializer.Serialize(table, JsonOptions);
    }

    private static async Task<PriceRow> TicketRowAsync(ILedgerSource source, long size, CancellationToken cancellationToken)
    {
        PublicationEstimate estimate = await PublicationSession.EstimateAsync(
            source, Writer, size, Options(), cancellationToken).ConfigureAwait(false);

        long held = estimate.BaseReserveDrops + estimate.PeakReserveDrops;

        return new PriceRow(size, estimate.Transactions, estimate.FeeDropsTotal, held, held - estimate.OwnerReserveDrops, "tickets");
    }

    private static async Task<PriceRow> BatchRowAsync(ILedgerSource source, long size, CancellationToken cancellationToken)
    {
        BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(
            source, Writer, Payer, size, Options(), cancellationToken).ConfigureAwait(false);

        long held = estimate.BaseReserveDrops;

        return new PriceRow(
            size, estimate.Memos + estimate.Batches, estimate.PayerFeeDrops + estimate.WriterFeeDrops,
            held, held - estimate.OwnerReserveDrops, "batch");
    }

    // An estimate reads no state directory; the option is required by the type, not used here.
    private static SessionOptions Options() => new()
    {
        StateDirectory = string.Empty,
        TicketsPerBatch = PublishPolicy.TicketsPerBatch,
    };
}
