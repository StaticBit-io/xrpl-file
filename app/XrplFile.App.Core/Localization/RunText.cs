using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>The Run screen: the progress line while a publication runs, its outcome, and the sentences for a run that failed before the ledger was reached.</summary>
public static class RunText
{
    public static readonly TextId Title = Id();
    public static readonly TextId ResumingCosts = Id();
    public static readonly TextId ContinueButton = Id();
    public static readonly TextId OutcomeLabel = Id();
    public static readonly TextId Published = Id();
    public static readonly TextId CancelledNote = Id();
    public static readonly TextId ResumeButton = Id();

    public static readonly TextId AwaitingConfirmation = Id();

    public static readonly TextId StageHashing = Id();
    public static readonly TextId StageHistory = Id();
    public static readonly TextId StageChunks = Id();
    public static readonly TextId StageLists = Id();
    public static readonly TextId StageReadBack = Id();
    public static readonly TextId StageManifest = Id();
    public static readonly TextId StageFence = Id();
    public static readonly TextId StageDone = Id();
    public static readonly TextId StageUnknown = Id();

    // The stage names again, written for the middle of "Stopped while {activity}": English lowers the
    // first letter, and another language may need a different form (an infinitive, a noun).
    public static readonly TextId MidHashing = Id();
    public static readonly TextId MidHistory = Id();
    public static readonly TextId MidChunks = Id();
    public static readonly TextId MidLists = Id();
    public static readonly TextId MidReadBack = Id();
    public static readonly TextId MidManifest = Id();
    public static readonly TextId MidFence = Id();
    public static readonly TextId MidDone = Id();
    public static readonly TextId MidStarting = Id();

    public static readonly TextId HeadHashing = Id();
    public static readonly TextId HeadRecords = Id();
    public static readonly TextId HeadProgress = Id();

    public static readonly TextId Starting = Id();
    public static readonly TextId LineFence = Id();
    public static readonly TextId LineTimeLeft = Id();
    public static readonly TextId LineRateTimeLeft = Id();
    public static readonly TextId LineFinishingPass = Id();
    public static readonly TextId LineEstimating = Id();
    public static readonly TextId LineElapsed = Id();

    public static readonly TextId RateChunksWhole = Id();
    public static readonly TextId RateChunksTenths = Id();
    public static readonly TextId RateListsWhole = Id();
    public static readonly TextId RateListsTenths = Id();

    public static readonly TextId FinishedIn = Id();
    public static readonly TextId Finished = Id();
    public static readonly TextId StoppedWhile = Id();

    public static readonly TextId CloseTitle = Id();
    public static readonly TextId CloseButton = Id();
    public static readonly TextId CloseDefaultName = Id();
    public static readonly TextId CloseConfirmPlain = Id();
    public static readonly TextId CloseConfirmTickets = Id();

    public static readonly TextId TicketsUnfinished = Id();
    public static readonly TextId TicketsReleased = Id();
    public static readonly TextId TicketsNoneReleased = Id();
    public static readonly TextId TicketsSomeReleased = Id();
    public static readonly TextId TicketsStay = Id();
    public static readonly TextId TicketsJoined = Id();
    public static readonly TextId StateNote = Id();

    public static readonly TextId ErrorSourceUnreadable = Id();
    public static readonly TextId ErrorSourceReadFailed = Id();
    public static readonly TextId ErrorSourceReadFailedAt = Id();
    public static readonly TextId ErrorSourceReadFailedResuming = Id();
    public static readonly TextId ErrorSourceReadFailedResumingAt = Id();
    public static readonly TextId ErrorSourceChanged = Id();
    public static readonly TextId ErrorSourceNotThisRun = Id();
    public static readonly TextId ErrorStateNotWritten = Id();
    public static readonly TextId ErrorStateUnreadable = Id();
    public static readonly TextId ErrorUnexpected = Id();

    public static readonly TextId HistoryNotRecorded = Id();
    public static readonly TextId HistoryRecordFailed = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Run", name);
}
