using System.Globalization;
using System.Text;

namespace XrplFile;

/// <summary>One plan entry as priced before the run: what it holds and what the Batch writer expects to spend on it.</summary>
internal sealed record EntryQuote(string Key, long Bytes, BatchPublicationEstimate Estimate);

/// <summary>
/// The whole plan priced at once, which is what the owner confirms. A per-entry estimate counts the base
/// reserve again for each entry, so the totals here count it once per account; and the balances are held
/// against the sum of every entry's worst case, not against each entry alone.
/// </summary>
internal sealed record PlanQuote(IReadOnlyList<EntryQuote> Entries)
{
    public const string ConfirmationPhrase = "publish to mainnet";

    private BatchPublicationEstimate First => Entries[0].Estimate;

    /// <summary>The expected fees of the whole plan, both accounts, nothing retried.</summary>
    public long FeeDrops => Entries.Sum(entry => entry.Estimate.PayerFeeDrops + entry.Estimate.WriterFeeDrops);

    /// <summary>Batch transactions and solo transactions over every entry.</summary>
    public long Transactions => Entries.Sum(entry => (long)entry.Estimate.Batches + entry.Estimate.SoloTransactions);

    /// <summary>The writer's base reserve once, plus every entry's worst-case fees.</summary>
    public long WriterRequiredDrops => First.BaseReserveDrops + Entries.Sum(entry => entry.Estimate.WriterFeeDropsWorstCase);

    /// <summary>The payer's base reserve once, plus every entry's worst-case fees.</summary>
    public long PayerRequiredDrops => First.BaseReserveDrops + Entries.Sum(entry => entry.Estimate.PayerFeeDropsWorstCase);

    // The balances are read at quote time for the same two accounts, so the first entry's figures stand for all.
    public bool WriterCovers => First.WriterSpendableDrops >= WriterRequiredDrops;

    public bool PayerCovers => First.PayerSpendableDrops >= PayerRequiredDrops;

    /// <summary>The text shown to the owner before anything is submitted.</summary>
    public static string Format(PlanQuote quote, string writer, string payer, string network)
    {
        StringBuilder text = new();
        text.AppendLine($"Plan quote on {network}: {quote.Entries.Count} files");

        foreach (EntryQuote entry in quote.Entries)
        {
            long transactions = (long)entry.Estimate.Batches + entry.Estimate.SoloTransactions;
            long fee = entry.Estimate.PayerFeeDrops + entry.Estimate.WriterFeeDrops;
            text.AppendLine($"  {entry.Key}: {entry.Bytes} bytes, {transactions} transactions, fee {Xrp(fee)} XRP");
        }

        text.AppendLine($"Total: {quote.Transactions} transactions, fee {Xrp(quote.FeeDrops)} XRP");
        text.AppendLine($"Writer {writer}: spendable {Xrp(quote.First.WriterSpendableDrops)} XRP, required {Xrp(quote.WriterRequiredDrops)} XRP");
        text.AppendLine($"Payer {payer}: spendable {Xrp(quote.First.PayerSpendableDrops)} XRP, required {Xrp(quote.PayerRequiredDrops)} XRP");
        text.Append("Required is each account's base reserve once plus the worst-case fees of every file; the expected fee is lower.");

        return text.ToString();
    }

    /// <summary>True only for the exact phrase; surrounding whitespace is trimmed, anything inside it is not.</summary>
    public static bool IsConfirmed(string? line) => string.Equals(line?.Trim(), ConfirmationPhrase, StringComparison.Ordinal);

    /// <summary>The cap for one entry's session: the estimate that was printed for it, with no headroom.</summary>
    public SpendingApproval ApprovalFor(string key, string writer, string payer)
    {
        EntryQuote entry = Entries.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"no quote for '{key}'");
        return SpendingApproval.For(entry.Estimate, writer, payer);
    }

    private static string Xrp(long drops) => (drops / 1_000_000m).ToString("0.000000", CultureInfo.InvariantCulture);
}
