using XrplFile.App.Core.Localization;

namespace XrplFile.App.Core;

/// <summary>One account's part of a publication, as the summary shows it (spec S12).</summary>
/// <param name="Role">The writer or the payer.</param>
/// <param name="Name">The key's label, or its address when the label is not known.</param>
/// <param name="RequiredDrops">What the account must hold for the run, reserve included.</param>
/// <param name="WorstCaseFeeDrops">What the account's fees come to when every transaction is retried.</param>
public sealed record AccountCost(AccountRole Role, string Name, long RequiredDrops, long WorstCaseFeeDrops)
{
    /// <summary>The row label for <see cref="RequiredDrops"/>: the role and the key it names.</summary>
    public UiText NeedsLabel => UiText.Of(Role == AccountRole.Payer ? EstimateText.PayerNeeds : EstimateText.WriterNeeds, ("name", Name));

    /// <summary>The row label for <see cref="WorstCaseFeeDrops"/>.</summary>
    public UiText WorstCaseFeesLabel => Role == AccountRole.Payer ? EstimateText.PayerFeesWorstCase : EstimateText.WriterFeesWorstCase;
}

/// <summary>
/// What a run will cost in time and money (spec S12): the duration range, the transactions, and each
/// account's required balance and worst-case fees. Above an hour or one XRP of either account's worst
/// case, it has to be confirmed.
/// </summary>
/// <param name="Duration">How long the run takes.</param>
/// <param name="Transactions">Every transaction the run submits.</param>
/// <param name="Accounts">The payer first when there is one, then the writer.</param>
public sealed record CostSummary(DurationRange Duration, long Transactions, IReadOnlyList<AccountCost> Accounts)
{
    public static readonly TimeSpan ConfirmationDuration = TimeSpan.FromHours(1);

    public const long ConfirmationDrops = 1_000_000;

    public bool NeedsConfirmation =>
        Duration.Longest > ConfirmationDuration || Accounts.Any(account => account.WorstCaseFeeDrops > ConfirmationDrops);

    public static CostSummary Of(PublicationEstimate estimate, PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(request);

        return new CostSummary(
            PublicationDuration.ForOneAccount(estimate), estimate.Transactions,
            [new AccountCost(AccountRole.Writer, request.WriterLabel ?? request.Account, estimate.RequiredDrops, estimate.FeeDropsWorstCase)]);
    }

    public static CostSummary Of(BatchPublicationEstimate estimate, PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(request);

        return Priced(PublicationDuration.ForBatch(estimate), estimate, request);
    }

    /// <summary>
    /// A Batch resume's summary, made of the chunks still to write. It is shown after the hash pass and
    /// the history read, so its duration is the remaining work alone (<see cref="PublicationDuration.ForBatchRemaining"/>).
    /// </summary>
    public static CostSummary OfResume(BatchPublicationEstimate remaining, PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(remaining);
        ArgumentNullException.ThrowIfNull(request);

        return Priced(PublicationDuration.ForBatchRemaining(remaining), remaining, request);
    }

    private static CostSummary Priced(DurationRange duration, BatchPublicationEstimate estimate, PublishRequest request)
    {
        return new CostSummary(
            duration, estimate.Memos + estimate.Batches,
            [
                new AccountCost(AccountRole.Payer, request.PayerLabel ?? request.PayerAccount ?? string.Empty, estimate.PayerRequiredDrops, estimate.PayerFeeDropsWorstCase),
                new AccountCost(AccountRole.Writer, request.WriterLabel ?? request.Account, estimate.WriterRequiredDrops, estimate.WriterFeeDropsWorstCase),
            ]);
    }
}
