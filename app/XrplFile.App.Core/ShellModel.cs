namespace XrplFile.App.Core;

/// <summary>The tabs the shell shows once the vault is open.</summary>
public enum AppTab
{
    Keys,
    Accounts,
    Publish,
    Read,
    History,
    Settings,
}

/// <summary>
/// Which tab is showing (design 4.4). This lived in a private field of the shell component until
/// the publications list needed to open the Read tab with a link in hand — and a decision nothing
/// outside the markup can reach is a decision nothing can test, which is what C1's own E2 says not
/// to have.
/// </summary>
public sealed class ShellModel
{
    /// <summary>
    /// The tab the shell draws. It starts at <see cref="AppTab.Keys"/>, which is where the shell
    /// has always opened, and only <see cref="Show"/> moves it.
    /// </summary>
    public AppTab Tab { get; private set; } = AppTab.Keys;

    /// <summary>
    /// Whether the phone-width More menu (Accounts, Settings) is open. Only <see cref="ToggleMore"/>
    /// opens it; choosing any tab, <see cref="CloseMore"/>, and the window widening past the phone
    /// layout close it (design 2026-10-13, section 4).
    /// </summary>
    public bool MoreOpen { get; private set; }

    /// <summary>
    /// Whether the More entry is the one to mark current: the tab showing is one More holds. A phone
    /// user who chose Settings from More would otherwise see no entry marked at all.
    /// </summary>
    public bool IsMoreCurrent => Tab is AppTab.Accounts or AppTab.Settings;

    /// <summary>Shows <paramref name="tab"/> and closes the More menu.</summary>
    /// <param name="tab">
    /// The tab to draw. Nothing is validated: <see cref="AppTab"/> is closed, every caller passes
    /// one of its members by name, and the shell's render chain names each tab it draws rather than
    /// falling through to a default, so a member nothing has taught it to draw draws nothing.
    /// </param>
    public void Show(AppTab tab)
    {
        bool changed = MoreOpen;
        MoreOpen = false;

        if (Tab != tab)
        {
            Tab = tab;
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Opens the More menu when it is closed, and closes it when it is open.</summary>
    public void ToggleMore()
    {
        MoreOpen = !MoreOpen;
        Changed?.Invoke();
    }

    /// <summary>Closes the More menu; says nothing when it was already closed.</summary>
    public void CloseMore()
    {
        if (!MoreOpen)
        {
            return;
        }

        MoreOpen = false;
        Changed?.Invoke();
    }

    /// <summary>
    /// Raised after <see cref="Tab"/> or <see cref="MoreOpen"/> changes, and only then.
    /// </summary>
    /// <remarks>
    /// The shell's own tab buttons never needed this: a click on a component's own markup re-renders
    /// that component. A caller anywhere else -- the publications list's Read button, in another
    /// component -- changed <see cref="Tab"/> and nothing drew it, so the button loaded its link and
    /// appeared to do nothing at all. That was found in a running window; no test in this repository
    /// can see whether a component redraws.
    /// </remarks>
    public event Action? Changed;
}
