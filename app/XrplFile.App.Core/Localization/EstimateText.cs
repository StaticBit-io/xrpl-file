using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>The Estimate screen: what a publication costs, before it is agreed to, and why it cannot go ahead.</summary>
public static class EstimateText
{
    public static readonly TextId Title = Id();
    public static readonly TextId Publishing = Id();
    public static readonly TextId NodeLine = Id();
    public static readonly TextId MethodBatch = Id();

    public static readonly TextId RowChunks = Id();
    public static readonly TextId RowBatches = Id();
    public static readonly TextId RowSoloTransactions = Id();
    public static readonly TextId RowTransactions = Id();
    public static readonly TextId RowFeeUnit = Id();
    public static readonly TextId RowFeePerTransaction = Id();
    public static readonly TextId Drops = Id();

    public static readonly TextId PayerHeading = Id();
    public static readonly TextId WriterHeading = Id();
    public static readonly TextId SpentTitle = Id();
    public static readonly TextId HeldTitle = Id();

    public static readonly TextId RowFees = Id();
    public static readonly TextId RowFeesWorstCase = Id();
    public static readonly TextId RowFeesRetried = Id();
    public static readonly TextId RowTicketReserve = Id();
    public static readonly TextId RowAccountReserve = Id();
    public static readonly TextId AccountReserveStays = Id();
    public static readonly TextId RowRequired = Id();
    public static readonly TextId RowBalance = Id();
    public static readonly TextId RowAccountBalance = Id();
    public static readonly TextId RowSpendable = Id();
    public static readonly TextId RowTime = Id();

    public static readonly TextId LargeTitle = Id();
    public static readonly TextId WriterNeeds = Id();
    public static readonly TextId PayerNeeds = Id();
    public static readonly TextId WriterFeesWorstCase = Id();
    public static readonly TextId PayerFeesWorstCase = Id();
    public static readonly TextId ConfirmationLabel = Id();

    public static readonly TextId BackButton = Id();
    public static readonly TextId PublishButton = Id();
    public static readonly TextId NothingReady = Id();

    public static readonly TextId TooSmallForBatch = Id();
    public static readonly TextId NoTicketRoom = Id();
    public static readonly TextId NotAffordable = Id();
    public static readonly TextId NotAffordableMostReserve = Id();
    public static readonly TextId NotAffordableSomeReserve = Id();
    public static readonly TextId ShortfallWriter = Id();
    public static readonly TextId ShortfallPayer = Id();
    public static readonly TextId ShortfallBoth = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Estimate", name);
}
