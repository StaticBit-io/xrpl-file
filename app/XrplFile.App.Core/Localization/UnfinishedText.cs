using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>The unfinished-runs list and the sentences for resuming or deleting one.</summary>
public static class UnfinishedText
{
    public static readonly TextId Title = Id();
    public static readonly TextId DeleteNote = Id();
    public static readonly TextId TicketNote = Id();
    public static readonly TextId ChooseKeyOption = Id();
    public static readonly TextId ResumeWithPayerButton = Id();
    public static readonly TextId Empty = Id();
    public static readonly TextId ColumnFile = Id();
    public static readonly TextId ColumnMethod = Id();
    public static readonly TextId ColumnStage = Id();
    public static readonly TextId RowFile = Id();
    public static readonly TextId DeleteButton = Id();

    public static readonly TextId MethodBatch = Id();
    public static readonly TextId MethodTickets = Id();
    public static readonly TextId MethodSequence = Id();
    public static readonly TextId UnknownPayer = Id();

    public static readonly TextId RowReason = Id();
    public static readonly TextId RowUnreadable = Id();
    public static readonly TextId NotListed = Id();
    public static readonly TextId NothingToResumeFrom = Id();
    public static readonly TextId PayerKeyGone = Id();
    public static readonly TextId ChoosePayer = Id();
    public static readonly TextId SourceGone = Id();
    public static readonly TextId FileUnreadable = Id();
    public static readonly TextId FileSizeDiffers = Id();
    public static readonly TextId AlreadyRunning = Id();
    public static readonly TextId DeleteFailed = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Unfinished", name);
}
