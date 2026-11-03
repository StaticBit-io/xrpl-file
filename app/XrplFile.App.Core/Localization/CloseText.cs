using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>The Close account screen and what is said of a deletion that did not happen.</summary>
public static class CloseText
{
    public static readonly TextId Title = Id();
    public static readonly TextId Closing = Id();
    public static readonly TextId CurrentlyHolds = Id();
    public static readonly TextId ImportedWarning = Id();
    public static readonly TextId Intro = Id();
    public static readonly TextId IntroWarning = Id();
    public static readonly TextId SendRemainderLabel = Id();
    public static readonly TextId YourAccountsLabel = Id();
    public static readonly TextId AddressPlaceholder = Id();
    public static readonly TextId CheckButton = Id();

    public static readonly TextId ConfirmSend = Id();
    public static readonly TextId FeeLabel = Id();
    public static readonly TextId ArrivingLabel = Id();
    public static readonly TextId ConfirmWarning = Id();
    public static readonly TextId ClosePermanentlyButton = Id();

    public static readonly TextId NotYetLedger = Id();
    public static readonly TextId LedgersRemaining = Id();
    public static readonly TextId LedgersRemainingWithTime = Id();

    public static readonly TextId ClosedSummary = Id();
    public static readonly TextId TransactionHash = Id();

    public static readonly TextId ChooseDestination = Id();
    public static readonly TextId AnotherAccountClosing = Id();
    public static readonly TextId Interrupted = Id();
    public static readonly TextId RecordNotSaved = Id();

    public static readonly TextId RefusedFeeClaimed = Id();
    public static readonly TextId RefusedNothingCharged = Id();
    public static readonly TextId OutcomeReady = Id();
    public static readonly TextId OutcomeNotYet = Id();
    public static readonly TextId OutcomeDestinationMissing = Id();
    public static readonly TextId OutcomeDestinationRefuses = Id();
    public static readonly TextId OutcomeSubmitted = Id();
    public static readonly TextId OutcomeQueued = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Close", name);
}
