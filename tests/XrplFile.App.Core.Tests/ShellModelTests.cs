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
}
