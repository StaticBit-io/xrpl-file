using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>Creating, unlocking and changing the vault, its damage and location, and what a vault failure says.</summary>
public static class VaultText
{
    public static readonly TextId UnlockTitle = Id();
    public static readonly TextId MasterPasswordLabel = Id();
    public static readonly TextId UnlockButton = Id();
    public static readonly TextId UnlockWithDeviceButton = Id();

    public static readonly TextId UnlockSuccessHeadline = Id();
    public static readonly TextId UnlockSuccessDetail = Id();
    public static readonly TextId UnlockWrongPasswordHeadline = Id();
    public static readonly TextId UnlockWrongPasswordDetail = Id();
    public static readonly TextId UnlockMissingHeadline = Id();
    public static readonly TextId UnlockMissingDetail = Id();
    public static readonly TextId UnlockCorruptHeadline = Id();
    public static readonly TextId UnlockCorruptDetail = Id();
    public static readonly TextId UnlockDegradedHeadline = Id();
    public static readonly TextId UnlockDegradedDetail = Id();
    public static readonly TextId UnlockUnsupportedHeadline = Id();
    public static readonly TextId UnlockUnsupportedDetail = Id();
    public static readonly TextId UnlockUnavailableHeadline = Id();
    public static readonly TextId UnlockUnavailableDetail = Id();

    public static readonly TextId CreateTitle = Id();
    public static readonly TextId CreateFirstRun = Id();
    public static readonly TextId CreateNoRecoveryLead = Id();
    public static readonly TextId CreateNoRecoveryBody = Id();
    public static readonly TextId CreateButton = Id();

    public static readonly TextId CreateSuccessHeadline = Id();
    public static readonly TextId CreateSuccessDetail = Id();
    public static readonly TextId CreateExistsHeadline = Id();
    public static readonly TextId CreateExistsDetail = Id();
    public static readonly TextId CreateStorageHeadline = Id();
    public static readonly TextId CreateStorageDetail = Id();
    public static readonly TextId CreateTooShortHeadline = Id();
    public static readonly TextId CreateTooShortDetail = Id();

    public static readonly TextId UnreachableTitle = Id();
    public static readonly TextId UnreachableLead = Id();
    public static readonly TextId UnreachableNotFresh = Id();
    public static readonly TextId UnreachableCause = Id();
    public static readonly TextId UnreachableRetry = Id();
    public static readonly TextId UnreachableChoose = Id();
    public static readonly TextId StartFreshTitle = Id();
    public static readonly TextId StartFreshBody = Id();
    public static readonly TextId ForgetLocationButton = Id();

    public static readonly TextId DamageTitle = Id();
    public static readonly TextId DamageIntro = Id();
    public static readonly TextId DamageWritesRefused = Id();
    public static readonly TextId DamageAcknowledge = Id();

    public static readonly TextId ChangePasswordTitle = Id();
    public static readonly TextId CurrentPasswordLabel = Id();
    public static readonly TextId NewPasswordLabel = Id();
    public static readonly TextId ChangePasswordButton = Id();
    public static readonly TextId CurrentPasswordWrong = Id();
    public static readonly TextId ChangeTooShort = Id();
    public static readonly TextId ChangeDone = Id();
    public static readonly TextId ChangeDeviceUnlockDropped = Id();
    public static readonly TextId ChangeOldCopyRemains = Id();
    public static readonly TextId ChangeIntegrityRepaired = Id();

    public static readonly TextId ErrorCorrupt = Id();
    public static readonly TextId ErrorUnsupported = Id();
    public static readonly TextId ErrorLocked = Id();
    public static readonly TextId ErrorNoSuchEntry = Id();
    public static readonly TextId ErrorDuplicateAddress = Id();
    public static readonly TextId ErrorEntryDamaged = Id();
    public static readonly TextId ErrorStorageFailed = Id();
    public static readonly TextId ErrorDegraded = Id();
    public static readonly TextId ErrorAddressMismatch = Id();

    public static readonly TextId DeviceUnlockNotCreated = Id();

    public static readonly TextId LocationNotSaved = Id();
    public static readonly TextId LocationSaved = Id();
    public static readonly TextId LocationNotForgotten = Id();
    public static readonly TextId LocationForgotten = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Vault", name);
}
