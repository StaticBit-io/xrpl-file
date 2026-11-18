namespace XrplFile.App.Core.Tutorial;

/// <summary>
/// The names of the screen regions a tutorial step can light. Each value is the <c>data-tour</c>
/// attribute the markup puts on the element, on the rail and on the tab bar alike, so whichever
/// layout is showing is the one measured.
/// </summary>
public static class TutorialRegions
{
    public const string AccountsPublishing = "accounts-publishing";
    public const string AccountsKeys = "accounts-keys";
    public const string AccountsNew = "accounts-new";
    public const string AccountsImport = "accounts-import";
    public const string AccountsFund = "accounts-fund";
    public const string AccountsNotExported = "accounts-not-exported";
    public const string AccountsRow = "accounts-row";
    public const string AccountsMenuExport = "accounts-menu-export";

    public const string NewLabel = "new-label";
    public const string NewNetwork = "new-network";
    public const string NewGenerate = "new-generate";

    public const string ImportSeed = "import-seed";
    public const string ImportPreview = "import-preview";
    public const string ImportBackupTab = "import-backup-tab";
    public const string ImportBackup = "import-backup";

    public const string ExportPassword = "export-password";
    public const string ExportCreate = "export-create";

    public const string PublishFile = "publish-file";
    public const string PublishFrom = "publish-from";
    public const string PublishPayer = "publish-payer";
    public const string PublishEstimate = "publish-estimate";

    public const string TabHistory = "tab-history";

    public const string ReadLink = "read-link";
    public const string ReadLookup = "read-lookup";
    public const string ReadResult = "read-result";
    public const string ReadRead = "read-read";
    public const string ReadSave = "read-save";
}
