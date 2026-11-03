using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>The Fund this account screen.</summary>
public static class FundingText
{
    public static readonly TextId Title = Id();
    public static readonly TextId InstructionAccountExists = Id();
    public static readonly TextId InstructionAccountMissing = Id();
    public static readonly TextId AddressLabel = Id();
    public static readonly TextId CopyAddressButton = Id();
    public static readonly TextId BalanceChecking = Id();
    public static readonly TextId BalanceNotRead = Id();
    public static readonly TextId Balance = Id();
    public static readonly TextId NotFunded = Id();
    public static readonly TextId PollNote = Id();
    public static readonly TextId CheckNowButton = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Funding", name);
}
