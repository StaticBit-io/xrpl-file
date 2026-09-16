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
    public AppTab Tab { get; private set; } = AppTab.Keys;

    public void Show(AppTab tab) => Tab = tab;
}
