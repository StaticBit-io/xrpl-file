using System.Globalization;
using XrplFile.App.Core.Localization;
using Xunit;

namespace XrplFile.App.Core.Tests.Localization;

public sealed class LanguageTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-language-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("ru-RU", "ru")]
    [InlineData("de-AT", "de")]
    [InlineData("es-419", "es")]
    [InlineData("pt-PT", "pt")]
    [InlineData("zh-TW", "zh")]
    [InlineData("zh-Hans-CN", "zh")]
    [InlineData("en-US", "en")]
    [InlineData("fr-FR", "en")]
    [InlineData("", "en")]
    public void With_nothing_chosen_the_OS_language_is_used_when_the_application_has_it(string culture, string expected)
    {
        Assert.Equal(expected, Localizer.Pick(null, CultureInfo.GetCultureInfo(culture)).Code);
    }

    [Fact]
    public void A_remembered_choice_wins_over_the_OS()
    {
        Assert.Equal("de", Localizer.Pick("de", CultureInfo.GetCultureInfo("ru-RU")).Code);
    }

    [Fact]
    public void A_remembered_value_naming_no_language_falls_back_to_the_OS()
    {
        Assert.Equal("ru", Localizer.Pick("klingon", CultureInfo.GetCultureInfo("ru-RU")).Code);
    }

    [Fact]
    public void A_choice_is_remembered_across_starts_and_announced()
    {
        string path = Path.Combine(_directory, "ui-language.txt");
        Localizer first = new(new FileLanguageSettingsStore(path), CultureInfo.GetCultureInfo("en-US"));
        int changes = 0;
        first.Changed += () => changes++;

        Assert.True(first.Choose(UiLanguage.Chinese));

        Assert.Equal(1, changes);
        Assert.Same(TextCatalog.For(UiLanguage.Chinese), first.Text);
        Assert.Same(UiLanguage.Chinese, new Localizer(new FileLanguageSettingsStore(path), CultureInfo.GetCultureInfo("en-US")).Language);
    }

    [Fact]
    public void Choosing_the_current_language_changes_nothing()
    {
        Localizer localizer = new(new FileLanguageSettingsStore(Path.Combine(_directory, "ui-language.txt")), CultureInfo.GetCultureInfo("ru-RU"));
        int changes = 0;
        localizer.Changed += () => changes++;

        localizer.Choose(UiLanguage.Russian);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_choice_that_cannot_be_saved_still_applies()
    {
        Localizer localizer = new(new FailingStore(), CultureInfo.GetCultureInfo("en-US"));

        Assert.False(localizer.Choose(UiLanguage.German));
        Assert.Same(UiLanguage.German, localizer.Language);
    }

    [Fact]
    public void An_unreadable_settings_file_remembers_nothing()
    {
        string path = Path.Combine(_directory, "ui-language.txt");
        Directory.CreateDirectory(path);

        Assert.Null(new FileLanguageSettingsStore(path).Read());
    }

    [Theory]
    [InlineData(1, PluralForm.One)]
    [InlineData(2, PluralForm.Few)]
    [InlineData(4, PluralForm.Few)]
    [InlineData(5, PluralForm.Many)]
    [InlineData(11, PluralForm.Many)]
    [InlineData(12, PluralForm.Many)]
    [InlineData(21, PluralForm.One)]
    [InlineData(22, PluralForm.Few)]
    [InlineData(111, PluralForm.Many)]
    [InlineData(0, PluralForm.Many)]
    public void Russian_counts_take_three_forms(long count, PluralForm expected)
    {
        Assert.Equal(expected, PluralRules.FormOf(UiLanguage.Russian, count));
    }

    [Theory]
    [InlineData("pt", 0, PluralForm.One)]
    [InlineData("pt", 1, PluralForm.One)]
    [InlineData("pt", 2, PluralForm.Other)]
    [InlineData("en", 0, PluralForm.Other)]
    [InlineData("en", 1, PluralForm.One)]
    [InlineData("zh", 1, PluralForm.Other)]
    public void Other_languages_follow_their_CLDR_rule(string code, long count, PluralForm expected)
    {
        Assert.Equal(expected, PluralRules.FormOf(UiLanguage.FromCode(code)!, count));
    }

    [Fact]
    public void Numbers_and_amounts_are_formatted_in_the_language_locale()
    {
        UiText text = UiText.Of(CommonText.DurationHoursMinutes, ("hours", 12345L), ("minutes", 5L));

        Assert.Equal("12,345 h 5 min", TestText.En(text));
        Assert.Equal("0,2 XRP", new XrpAmount(200_000).ToString(UiLanguage.Russian.Culture));
        Assert.Equal("0.2 XRP", XrpAmount.Format(200_000));
    }

    [Fact]
    public void A_duration_range_whose_ends_read_the_same_is_one_figure()
    {
        Assert.Equal("3 min", TestText.En(DurationText.Of(new DurationRange(TimeSpan.FromSeconds(170), TimeSpan.FromSeconds(185)))));
    }

    private sealed class FailingStore : ILanguageSettingsStore
    {
        public string? Read() => null;

        public void Write(string code) => throw new IOException("read-only");
    }
}
