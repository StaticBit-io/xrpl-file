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
}
