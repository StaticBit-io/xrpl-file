using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class ShellModelTests
{
    [Fact]
    public void The_keys_tab_is_where_the_application_opens()
    {
        Assert.Equal(AppTab.Keys, new ShellModel().Tab);
    }

    [Fact]
    public void Showing_a_tab_changes_which_one_is_current()
    {
        ShellModel model = new();

        model.Show(AppTab.Read);

        Assert.Equal(AppTab.Read, model.Tab);
    }

    [Fact]
    public void Every_tab_can_be_shown()
    {
        ShellModel model = new();

        foreach (AppTab tab in Enum.GetValues<AppTab>())
        {
            model.Show(tab);
            Assert.Equal(tab, model.Tab);
        }
    }

    /// <summary>
    /// The publications list's Read button changes the tab from another component, and the shell
    /// only redraws if it is told. Without this the button loaded its link onto a tab nobody showed.
    /// </summary>
    [Fact]
    public void Changing_the_tab_says_so()
    {
        ShellModel model = new();
        int raised = 0;
        model.Changed += () => raised++;

        model.Show(AppTab.Read);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Showing_the_tab_already_shown_says_nothing()
    {
        ShellModel model = new();
        int raised = 0;
        model.Changed += () => raised++;

        model.Show(AppTab.Keys);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void History_is_a_tab_of_its_own()
    {
        ShellModel model = new();

        model.Show(AppTab.History);

        Assert.Equal(AppTab.History, model.Tab);
    }

    [Fact]
    public void More_starts_closed()
    {
        Assert.False(new ShellModel().MoreOpen);
    }

    [Fact]
    public void Toggling_More_opens_and_closes_it_and_says_so_each_time()
    {
        ShellModel model = new();
        int raised = 0;
        model.Changed += () => raised++;

        model.ToggleMore();
        Assert.True(model.MoreOpen);

        model.ToggleMore();
        Assert.False(model.MoreOpen);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Choosing_a_tab_closes_More()
    {
        ShellModel model = new();
        model.ToggleMore();

        model.Show(AppTab.Accounts);

        Assert.False(model.MoreOpen);
        Assert.Equal(AppTab.Accounts, model.Tab);
    }

    /// <summary>
    /// Settings is current, More is opened and Settings chosen again: the tab does not change, but
    /// the menu must still close, and the shell must hear about it or the panel stays drawn.
    /// </summary>
    [Fact]
    public void Choosing_the_current_tab_from_More_still_closes_it_and_says_so()
    {
        ShellModel model = new();
        model.Show(AppTab.Settings);
        model.ToggleMore();
        int raised = 0;
        model.Changed += () => raised++;

        model.Show(AppTab.Settings);

        Assert.False(model.MoreOpen);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Closing_More_when_it_is_open_says_so()
    {
        ShellModel model = new();
        model.ToggleMore();
        int raised = 0;
        model.Changed += () => raised++;

        model.CloseMore();

        Assert.False(model.MoreOpen);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Closing_More_when_it_is_already_closed_says_nothing()
    {
        ShellModel model = new();
        int raised = 0;
        model.Changed += () => raised++;

        model.CloseMore();

        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData(AppTab.Accounts, true)]
    [InlineData(AppTab.Settings, true)]
    [InlineData(AppTab.Keys, false)]
    [InlineData(AppTab.Publish, false)]
    [InlineData(AppTab.Read, false)]
    [InlineData(AppTab.History, false)]
    public void More_counts_as_current_exactly_for_the_tabs_it_holds(AppTab tab, bool expected)
    {
        ShellModel model = new();

        model.Show(tab);

        Assert.Equal(expected, model.IsMoreCurrent);
    }
}
