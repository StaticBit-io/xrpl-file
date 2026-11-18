namespace XrplFile.App.Core;

/// <summary>The screens the Accounts tab can show; <see cref="List"/> is the main one.</summary>
public enum AccountsView
{
    List,
    New,
    AddKey,
    Export,
    Reveal,
    Funding,
    Close,
}

/// <summary>The two ways to import a key, as tabs of the import screen.</summary>
public enum ImportTab
{
    Seed,
    Backup,
}

/// <summary>
/// Which sub-screen each tab is on: the Accounts tab's New / Import / row screens, the open row
/// menu, the import tab, and the Publish estimate. This used to be private fields of the components,
/// which was enough while a tab change disposed the component and so reset them by accident. The
/// tutorial has to open and reset these screens from outside the markup, so the state lives here,
/// and the reset the disposal used to give is now explicit: <see cref="ResetToMain"/> runs when the
/// shell moves to a different tab, and the shell calls it on lock.
/// </summary>
public sealed class ScreenNavigation
{
    private readonly ShellModel _shell;
    private AppTab _lastTab;

    /// <summary>Subscribes to <paramref name="shell"/> so a change of tab returns every sub-screen to its main one.</summary>
    public ScreenNavigation(ShellModel shell)
    {
        _shell = shell;
        _lastTab = shell.Tab;
        shell.Changed += OnShellChanged;
    }

    /// <summary>The screen the Accounts tab draws. It starts on <see cref="AccountsView.List"/>.</summary>
    public AccountsView AccountsView { get; private set; } = AccountsView.List;

    /// <summary>The key reference the Export, Reveal, Funding or Close screen is for; null on every other screen.</summary>
    public string? ActiveReference { get; private set; }

    /// <summary>The reference of the row whose actions menu is open, or null when none is.</summary>
    public string? MenuFor { get; private set; }

    /// <summary>The tab of the import screen showing. It starts on <see cref="ImportTab.Seed"/>.</summary>
    public ImportTab ImportTab { get; private set; } = ImportTab.Seed;

    /// <summary>The request the Publish tab is estimating; non-null means it shows the Estimate screen.</summary>
    public PublishRequest? Estimate { get; private set; }

    /// <summary>Opens an Accounts screen and closes any row menu.</summary>
    /// <param name="view">The screen to show.</param>
    /// <param name="reference">
    /// The key the screen is about. Required for Export, Reveal, Funding and Close, and refused for
    /// List, New and AddKey: a reference left behind would let a later screen act on a stale key.
    /// </param>
    /// <exception cref="ArgumentException">The reference does not fit <paramref name="view"/>.</exception>
    public void OpenAccounts(AccountsView view, string? reference = null)
    {
        bool needsReference = view is AccountsView.Export or AccountsView.Reveal or AccountsView.Funding or AccountsView.Close;
        if (needsReference != (reference is not null))
        {
            throw new ArgumentException(
                needsReference ? $"{view} needs a key reference." : $"{view} takes no key reference.",
                nameof(reference));
        }

        // Import always opens on its first tab; the tab a previous visit ended on is not worth keeping.
        ImportTab nextTab = view == AccountsView.AddKey ? ImportTab.Seed : ImportTab;
        bool changed = AccountsView != view || ActiveReference != reference || MenuFor is not null || ImportTab != nextTab;

        AccountsView = view;
        ActiveReference = reference;
        MenuFor = null;
        ImportTab = nextTab;

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Opens the actions menu of <paramref name="reference"/>, closes it when it is the open one, and moves it when another is.</summary>
    public void ToggleMenu(string reference)
    {
        MenuFor = MenuFor == reference ? null : reference;
        Changed?.Invoke();
    }

    /// <summary>Opens the actions menu of <paramref name="reference"/>, closing any other.</summary>
    public void OpenMenu(string reference)
    {
        if (MenuFor == reference)
        {
            return;
        }

        MenuFor = reference;
        Changed?.Invoke();
    }

    /// <summary>Closes the open row menu; says nothing when none is open.</summary>
    public void CloseMenu()
    {
        if (MenuFor is null)
        {
            return;
        }

        MenuFor = null;
        Changed?.Invoke();
    }

    /// <summary>Shows <paramref name="tab"/> of the import screen.</summary>
    public void ShowImportTab(ImportTab tab)
    {
        if (ImportTab == tab)
        {
            return;
        }

        ImportTab = tab;
        Changed?.Invoke();
    }

    /// <summary>Makes the Publish tab show the Estimate screen for <paramref name="request"/>.</summary>
    public void ShowEstimate(PublishRequest request)
    {
        Estimate = request;
        Changed?.Invoke();
    }

    /// <summary>Takes the Publish tab back to its form; says nothing when it was already there.</summary>
    public void CloseEstimate()
    {
        if (Estimate is null)
        {
            return;
        }

        Estimate = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Returns every sub-screen to its main one. <see cref="Changed"/> is raised only when something
    /// moved, so a lock or a tab change on a tab already at rest redraws nothing.
    /// </summary>
    public void ResetToMain()
    {
        bool changed = AccountsView != AccountsView.List
            || ActiveReference is not null
            || MenuFor is not null
            || ImportTab != ImportTab.Seed
            || Estimate is not null;

        AccountsView = AccountsView.List;
        ActiveReference = null;
        MenuFor = null;
        ImportTab = ImportTab.Seed;
        Estimate = null;

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Raised after any sub-screen state changes, and only then. A change made outside a component's
    /// own markup (a tab switch, the tutorial) is otherwise invisible to it.
    /// </summary>
    public event Action? Changed;

    private void OnShellChanged()
    {
        // ShellModel.Changed also fires for the More menu; only a different tab resets the screens.
        if (_shell.Tab == _lastTab)
        {
            return;
        }

        _lastTab = _shell.Tab;
        ResetToMain();
    }
}
