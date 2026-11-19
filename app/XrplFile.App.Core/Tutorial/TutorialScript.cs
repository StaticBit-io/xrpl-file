using XrplFile.App.Core.Localization;
using R = XrplFile.App.Core.Tutorial.TutorialRegions;

namespace XrplFile.App.Core.Tutorial;

/// <summary>The tutorials, in the order the list shows them.</summary>
public enum TutorialTopic
{
    CreateAccount,
    AddKey,
    ExportKey,
    Publish,
    Read,
}

/// <summary>What moves a step on besides the Next button.</summary>
public enum TutorialAdvance
{
    Manual,
    OnLookupSucceeded,
    OnReadSucceeded,
}

/// <summary>A menu the tutorial opens for a step.</summary>
public enum TutorialMenu
{
    None,

    /// <summary>The actions menu of the row the topic is about.</summary>
    ExportRow,
}

/// <summary>The screen a step needs to be showing.</summary>
public sealed record TutorialScreen(
    AppTab Tab,
    AccountsView Accounts = AccountsView.List,
    ImportTab Import = ImportTab.Seed,
    TutorialMenu Menu = TutorialMenu.None);

/// <summary>One bubble: where it is, what it lights, what the user may touch and when it moves on.</summary>
/// <param name="Lit">Regions left bright in the overlay.</param>
/// <param name="Usable">Lit regions that respond to the user; every other lit region is inert.</param>
/// <param name="Buttons">Placeholder name to the catalog phrase of the button it stands for.</param>
public sealed record TutorialStep(
    TutorialScreen Screen,
    TextId Title,
    TextId Body,
    IReadOnlyList<string> Lit,
    IReadOnlyList<string> Usable,
    TutorialAdvance Advance,
    IReadOnlyList<(string Name, TextId Button)> Buttons);

/// <summary>
/// The tutorial content as data: topics, steps, lit regions and text ids. A topic always has the same
/// steps whatever the vault holds, so "step n of m" never changes under the user.
/// </summary>
public static class TutorialScript
{
    /// <summary>The project logo, the same link the site shows.</summary>
    public const string SampleLink = "xrpl-file://mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5";

    private static readonly IReadOnlyList<TutorialStep> CreateAccountSteps =
    [
        Step(new TutorialScreen(AppTab.Accounts), TutorialText.CreateAccountSectionTitle, TutorialText.CreateAccountSectionBody, [R.AccountsPublishing]),
        Step(new TutorialScreen(AppTab.Accounts), TutorialText.CreateAccountNewButtonTitle, TutorialText.CreateAccountNewButtonBody, [R.AccountsNew],
            buttons: [("newAccount", AccountsText.NewAccountButton)]),
        Step(new TutorialScreen(AppTab.Accounts, AccountsView.New), TutorialText.CreateAccountFieldsTitle, TutorialText.CreateAccountFieldsBody, [R.NewLabel, R.NewNetwork]),
        Step(new TutorialScreen(AppTab.Accounts, AccountsView.New), TutorialText.CreateAccountGenerateTitle, TutorialText.CreateAccountGenerateBody, [R.NewGenerate],
            buttons: [("generate", AccountsText.NewGenerateButton)]),
        Step(new TutorialScreen(AppTab.Accounts), TutorialText.CreateAccountFundTitle, TutorialText.CreateAccountFundBody, [R.AccountsFund],
            buttons: [("fund", AccountsText.FundButton)]),
        Step(new TutorialScreen(AppTab.Accounts), TutorialText.CreateAccountDoneTitle, TutorialText.CreateAccountDoneBody, []),
    ];

    private static readonly IReadOnlyList<TutorialStep> AddKeySteps =
    [
        Step(new TutorialScreen(AppTab.Accounts), TutorialText.AddKeyImportButtonTitle, TutorialText.AddKeyImportButtonBody, [R.AccountsImport],
            buttons: [("importKey", AccountsText.ImportKeyButton)]),
        Step(new TutorialScreen(AppTab.Accounts, AccountsView.AddKey, ImportTab.Seed), TutorialText.AddKeySeedTitle, TutorialText.AddKeySeedBody, [R.ImportSeed, R.ImportPreview],
            buttons: [("preview", KeysText.PreviewButton)]),
        Step(new TutorialScreen(AppTab.Accounts, AccountsView.AddKey, ImportTab.Backup), TutorialText.AddKeyBackupTitle, TutorialText.AddKeyBackupBody, [R.ImportBackupTab, R.ImportBackup],
            buttons: [("backupTab", KeysText.TabBackup)]),
        Step(new TutorialScreen(AppTab.Accounts), TutorialText.AddKeyDoneTitle, TutorialText.AddKeyDoneBody, [R.AccountsKeys]),
    ];

    private static readonly IReadOnlyList<TutorialStep> ExportKeySteps =
    [
        // The tutorial model swaps AccountsNotExported for AccountsRow when no row carries the tag.
        Step(new TutorialScreen(AppTab.Accounts), TutorialText.ExportKeyRowTitle, TutorialText.ExportKeyRowBody, [R.AccountsNotExported]),
        Step(new TutorialScreen(AppTab.Accounts, Menu: TutorialMenu.ExportRow), TutorialText.ExportKeyMenuTitle, TutorialText.ExportKeyMenuBody, [R.AccountsMenuExport],
            buttons: [("exportKey", AccountsText.MenuExportKey)]),
        Step(new TutorialScreen(AppTab.Accounts, AccountsView.Export), TutorialText.ExportKeyPasswordTitle, TutorialText.ExportKeyPasswordBody, [R.ExportPassword, R.ExportCreate],
            buttons: [("createBackup", KeysText.CreateBackupButton)]),
        Step(new TutorialScreen(AppTab.Accounts, AccountsView.Export), TutorialText.ExportKeyDoneTitle, TutorialText.ExportKeyDoneBody, [],
            buttons: [("saveToFile", KeysText.SaveToFileButton), ("showQr", KeysText.ShowQrButton)]),
    ];

    private static readonly IReadOnlyList<TutorialStep> PublishSteps =
    [
        Step(new TutorialScreen(AppTab.Publish), TutorialText.PublishFileTitle, TutorialText.PublishFileBody, [R.PublishFile]),
        Step(new TutorialScreen(AppTab.Publish), TutorialText.PublishFromTitle, TutorialText.PublishFromBody, [R.PublishFrom]),
        Step(new TutorialScreen(AppTab.Publish), TutorialText.PublishPayerTitle, TutorialText.PublishPayerBody, [R.PublishPayer]),
        Step(new TutorialScreen(AppTab.Publish), TutorialText.PublishEstimateTitle, TutorialText.PublishEstimateBody, [R.PublishEstimate],
            buttons: [("estimate", PublishText.EstimateButton)]),
        Step(new TutorialScreen(AppTab.Publish), TutorialText.PublishHistoryTitle, TutorialText.PublishHistoryBody, [R.TabHistory],
            buttons: [("history", ShellText.TabHistory)]),
        Step(new TutorialScreen(AppTab.Publish), TutorialText.PublishDoneTitle, TutorialText.PublishDoneBody, []),
    ];

    private static readonly IReadOnlyList<TutorialStep> ReadSteps =
    [
        Step(new TutorialScreen(AppTab.Read), TutorialText.ReadLinkRealTitle, TutorialText.ReadLinkRealBody, [R.ReadLink, R.ReadLookup],
            usable: [R.ReadLookup], advance: TutorialAdvance.OnLookupSucceeded, buttons: [("lookUp", ReadText.LookUpButton)]),
        Step(new TutorialScreen(AppTab.Read), TutorialText.ReadResultTitle, TutorialText.ReadResultBody, [R.ReadResult, R.ReadRead],
            usable: [R.ReadRead], advance: TutorialAdvance.OnReadSucceeded, buttons: [("read", ReadText.ReadButton)]),
        Step(new TutorialScreen(AppTab.Read), TutorialText.ReadSaveTitle, TutorialText.ReadSaveBody, [R.ReadSave],
            usable: [R.ReadSave], buttons: [("save", ReadText.SaveButton)]),
        Step(new TutorialScreen(AppTab.Read), TutorialText.ReadDoneTitle, TutorialText.ReadDoneBody, []),
    ];

    private static readonly IReadOnlyList<TutorialStep> ReadShownSteps =
    [
        Step(new TutorialScreen(AppTab.Read), TutorialText.ReadLinkShownTitle, TutorialText.ReadLinkShownBody, [R.ReadLink, R.ReadLookup],
            buttons: [("lookUp", ReadText.LookUpButton)]),
        Step(new TutorialScreen(AppTab.Read), TutorialText.ReadResultTitle, TutorialText.ReadResultBody, [R.ReadResult, R.ReadRead],
            buttons: [("read", ReadText.ReadButton)]),
        Step(new TutorialScreen(AppTab.Read), TutorialText.ReadSaveTitle, TutorialText.ReadSaveBody, [R.ReadSave],
            buttons: [("save", ReadText.SaveButton)]),
        Step(new TutorialScreen(AppTab.Read), TutorialText.ReadDoneTitle, TutorialText.ReadDoneBody, []),
    ];

    private static readonly IReadOnlyList<TutorialStep> ExportWithoutKeySteps =
    [
        Step(new TutorialScreen(AppTab.Accounts), TutorialText.ExportKeyNoKeyTitle, TutorialText.ExportKeyNoKeyBody, [],
            buttons: [("newAccount", AccountsText.NewAccountButton)]),
    ];

    /// <summary>The topics in the order the list shows them.</summary>
    public static IReadOnlyList<TutorialTopic> Topics { get; } =
        [TutorialTopic.CreateAccount, TutorialTopic.AddKey, TutorialTopic.ExportKey, TutorialTopic.Publish, TutorialTopic.Read];

    /// <summary>The Read topic as it runs when the tab already holds something: shown, nothing responds.</summary>
    public static IReadOnlyList<TutorialStep> ReadShown => ReadShownSteps;

    /// <summary>The single step of the export topic when the vault holds no key.</summary>
    public static IReadOnlyList<TutorialStep> ExportWithoutKey => ExportWithoutKeySteps;

    /// <summary>Every step list, for tests.</summary>
    public static IReadOnlyList<TutorialStep> AllSteps { get; } =
        [.. CreateAccountSteps, .. AddKeySteps, .. ExportKeySteps, .. PublishSteps, .. ReadSteps, .. ReadShownSteps, .. ExportWithoutKeySteps];

    /// <summary>The list line of <paramref name="topic"/>.</summary>
    /// <remarks>
    /// A <c>switch</c> expression with no default arm on purpose: a topic added to <see cref="TutorialTopic"/>
    /// without an entry here is CS8509 under this repository's <c>TreatWarningsAsErrors</c>.
    /// </remarks>
#pragma warning disable CS8524
    public static TextId NameOf(TutorialTopic topic) => topic switch
    {
        TutorialTopic.CreateAccount => TutorialText.TopicCreateAccount,
        TutorialTopic.AddKey => TutorialText.TopicAddKey,
        TutorialTopic.ExportKey => TutorialText.TopicExportKey,
        TutorialTopic.Publish => TutorialText.TopicPublish,
        TutorialTopic.Read => TutorialText.TopicRead,
    };
#pragma warning restore CS8524

    /// <summary>The steps of <paramref name="topic"/>; for Read, the list that is done for real.</summary>
    /// <remarks>
    /// A <c>switch</c> expression with no default arm on purpose: a topic added to <see cref="TutorialTopic"/>
    /// without an entry here is CS8509 under this repository's <c>TreatWarningsAsErrors</c>.
    /// </remarks>
#pragma warning disable CS8524
    public static IReadOnlyList<TutorialStep> StepsOf(TutorialTopic topic) => topic switch
    {
        TutorialTopic.CreateAccount => CreateAccountSteps,
        TutorialTopic.AddKey => AddKeySteps,
        TutorialTopic.ExportKey => ExportKeySteps,
        TutorialTopic.Publish => PublishSteps,
        TutorialTopic.Read => ReadSteps,
    };
#pragma warning restore CS8524

    private static TutorialStep Step(
        TutorialScreen screen,
        TextId title,
        TextId body,
        string[] lit,
        string[]? usable = null,
        TutorialAdvance advance = TutorialAdvance.Manual,
        (string Name, TextId Button)[]? buttons = null) =>
        new(screen, title, body, lit, usable ?? [], advance, buttons ?? []);
}
