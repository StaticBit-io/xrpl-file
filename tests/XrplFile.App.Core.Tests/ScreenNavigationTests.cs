using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class ScreenNavigationTests
{
    private static PublishRequest AnyRequest() =>
        new(1024, "application/octet-stream", "big.bin", "ref-w", "rWriter", "standalone", SourcePath: "big.bin");

    private static ScreenNavigation Create(out ShellModel shell)
    {
        shell = new ShellModel();
        return new ScreenNavigation(shell);
    }

    [Fact]
    public void Starts_on_every_main_screen()
    {
        ScreenNavigation nav = Create(out _);

        Assert.Equal(AccountsView.List, nav.AccountsView);
        Assert.Null(nav.ActiveReference);
        Assert.Null(nav.MenuFor);
        Assert.Equal(ImportTab.Seed, nav.ImportTab);
        Assert.Null(nav.Estimate);
    }

    [Fact]
    public void OpenAccounts_export_keeps_the_reference_and_closes_the_menu()
    {
        ScreenNavigation nav = Create(out _);
        nav.OpenMenu("r1");

        nav.OpenAccounts(AccountsView.Export, "r1");

        Assert.Equal(AccountsView.Export, nav.AccountsView);
        Assert.Equal("r1", nav.ActiveReference);
        Assert.Null(nav.MenuFor);
    }

    [Theory]
    [InlineData(AccountsView.Export)]
    [InlineData(AccountsView.Reveal)]
    [InlineData(AccountsView.Funding)]
    [InlineData(AccountsView.Close)]
    public void Row_screens_need_a_reference(AccountsView view)
    {
        ScreenNavigation nav = Create(out _);

        Assert.Throws<ArgumentException>(() => nav.OpenAccounts(view));
    }

    [Theory]
    [InlineData(AccountsView.List)]
    [InlineData(AccountsView.New)]
    [InlineData(AccountsView.AddKey)]
    public void Screens_without_a_row_refuse_a_reference(AccountsView view)
    {
        ScreenNavigation nav = Create(out _);

        Assert.Throws<ArgumentException>(() => nav.OpenAccounts(view, "r1"));
    }

    [Fact]
    public void Opening_import_starts_on_the_seed_tab()
    {
        ScreenNavigation nav = Create(out _);
        nav.ShowImportTab(ImportTab.Backup);

        nav.OpenAccounts(AccountsView.AddKey);

        Assert.Equal(ImportTab.Seed, nav.ImportTab);
    }

    [Fact]
    public void ToggleMenu_opens_closes_and_moves()
    {
        ScreenNavigation nav = Create(out _);

        nav.ToggleMenu("r1");
        Assert.Equal("r1", nav.MenuFor);

        nav.ToggleMenu("r1");
        Assert.Null(nav.MenuFor);

        nav.ToggleMenu("r1");
        nav.ToggleMenu("r2");
        Assert.Equal("r2", nav.MenuFor);
    }

    [Fact]
    public void ResetToMain_clears_everything_and_raises_Changed_once()
    {
        ScreenNavigation nav = Create(out _);
        nav.OpenAccounts(AccountsView.Export, "r1");
        nav.OpenMenu("r2");
        nav.ShowImportTab(ImportTab.Backup);
        nav.ShowEstimate(AnyRequest());
        int raised = 0;
        nav.Changed += () => raised++;

        nav.ResetToMain();

        Assert.Equal(AccountsView.List, nav.AccountsView);
        Assert.Null(nav.ActiveReference);
        Assert.Null(nav.MenuFor);
        Assert.Equal(ImportTab.Seed, nav.ImportTab);
        Assert.Null(nav.Estimate);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ResetToMain_on_main_screens_raises_nothing()
    {
        ScreenNavigation nav = Create(out _);
        int raised = 0;
        nav.Changed += () => raised++;

        nav.ResetToMain();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void A_tab_change_resets_to_main()
    {
        ScreenNavigation nav = Create(out ShellModel shell);
        nav.OpenAccounts(AccountsView.New);
        nav.ShowEstimate(AnyRequest());

        shell.Show(AppTab.Publish);

        Assert.Equal(AccountsView.List, nav.AccountsView);
        Assert.Null(nav.Estimate);
    }

    [Fact]
    public void Showing_the_tab_already_showing_does_not_reset()
    {
        ScreenNavigation nav = Create(out ShellModel shell);
        nav.OpenAccounts(AccountsView.New);

        shell.Show(AppTab.Accounts);

        Assert.Equal(AccountsView.New, nav.AccountsView);
    }
}
