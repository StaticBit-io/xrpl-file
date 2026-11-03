using System.Runtime.CompilerServices;

namespace XrplFile.App.Core.Localization;

/// <summary>Importing a key, exporting a backup, revealing a secret, and what those say when they fail.</summary>
public static class KeysText
{
    public static readonly TextId ExportReminder = Id();
    public static readonly TextId ImportTitle = Id();
    public static readonly TextId TabSeed = Id();
    public static readonly TextId TabBackup = Id();
    public static readonly TextId SeedLabel = Id();
    public static readonly TextId PreviewButton = Id();
    public static readonly TextId BackupPasswordOptionalLabel = Id();
    public static readonly TextId ChooseFileButton = Id();
    public static readonly TextId ColumnAddress = Id();
    public static readonly TextId ColumnAlgorithm = Id();
    public static readonly TextId ColumnBackupLabel = Id();
    public static readonly TextId ImportKeyButton = Id();
    public static readonly TextId ImportingAddress = Id();
    public static readonly TextId CloseBackupButton = Id();
    public static readonly TextId StoredAs = Id();
    public static readonly TextId AddToVaultButton = Id();
    public static readonly TextId AddedToVault = Id();

    public static readonly TextId EnterSeed = Id();
    public static readonly TextId InvalidSeed = Id();
    public static readonly TextId KeyNoLongerAvailable = Id();
    public static readonly TextId BackupKeyInvalidSeed = Id();
    public static readonly TextId ContainerAddressMismatch = Id();
    public static readonly TextId ContainerRegularKey = Id();
    public static readonly TextId EnterLabel = Id();
    public static readonly TextId PickNetwork = Id();
    public static readonly TextId BackupNotOurs = Id();
    public static readonly TextId BackupWrongPassword = Id();
    public static readonly TextId BackupPasswordless = Id();
    public static readonly TextId BackupIterations = Id();
    public static readonly TextId BackupUnreadable = Id();
    public static readonly TextId StoreLocked = Id();
    public static readonly TextId StoreFailed = Id();

    public static readonly TextId ExportTitle = Id();
    public static readonly TextId ExportIntro = Id();
    public static readonly TextId BackupPasswordLabel = Id();
    public static readonly TextId CreateBackupButton = Id();
    public static readonly TextId SkipButton = Id();
    public static readonly TextId BackupCreated = Id();
    public static readonly TextId Saved = Id();
    public static readonly TextId SaveToFileButton = Id();
    public static readonly TextId ShowQrButton = Id();
    public static readonly TextId QrTooLarge = Id();
    public static readonly TextId BackupPasswordTooShort = Id();
    public static readonly TextId BackupSavedNotRecorded = Id();
    public static readonly TextId BackupNotWritten = Id();

    public static readonly TextId RevealTitle = Id();
    public static readonly TextId RevealWarning = Id();
    public static readonly TextId RevealButton = Id();
    public static readonly TextId HideNowButton = Id();
    public static readonly TextId CopyButton = Id();

    private static TextId Id([CallerMemberName] string name = "") => TextId.Of("Keys", name);
}
