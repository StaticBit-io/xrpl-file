namespace XrplFile.App.Core;

/// <summary>The tabs the shell shows once the vault is open.</summary>
public enum AppTab
{
    Keys,
    Accounts,
    Publish,
    Read,
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

    /// <summary>Shows <paramref name="tab"/>.</summary>
    /// <param name="tab">
    /// The tab to draw. Nothing is validated: <see cref="AppTab"/> is closed, every caller passes
    /// one of its members by name, and the shell's render chain names each tab it draws rather than
    /// falling through to a default, so a member nothing has taught it to draw draws nothing.
    /// </param>
    public void Show(AppTab tab) => Tab = tab;
}
