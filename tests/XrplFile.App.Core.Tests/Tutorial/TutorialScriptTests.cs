using System.Reflection;
using System.Text.RegularExpressions;
using XrplFile.App.Core.Localization;
using XrplFile.App.Core.Tutorial;
using Xunit;

namespace XrplFile.App.Core.Tests.Tutorial;

public sealed partial class TutorialScriptTests
{
    public static TheoryData<string> AllLanguages() => new(UiLanguage.All.Select(language => language.Code));

    [Fact]
    public void Every_topic_has_the_spec_step_count()
    {
        Assert.Equal(6, TutorialScript.StepsOf(TutorialTopic.CreateAccount).Count);
        Assert.Equal(4, TutorialScript.StepsOf(TutorialTopic.AddKey).Count);
        Assert.Equal(4, TutorialScript.StepsOf(TutorialTopic.ExportKey).Count);
        Assert.Equal(6, TutorialScript.StepsOf(TutorialTopic.Publish).Count);
        Assert.Equal(4, TutorialScript.StepsOf(TutorialTopic.Read).Count);
        Assert.Equal(4, TutorialScript.ReadShown.Count);
        Assert.Single(TutorialScript.ExportWithoutKey);
        Assert.Equal(5, TutorialScript.Topics.Count);
    }

    [Fact]
    public void Only_the_read_topic_has_usable_regions()
    {
        foreach (TutorialTopic topic in TutorialScript.Topics.Where(topic => topic != TutorialTopic.Read))
        {
            Assert.All(TutorialScript.StepsOf(topic), step => Assert.Empty(step.Usable));
        }

        Assert.All(TutorialScript.ReadShown, step => Assert.Empty(step.Usable));
        Assert.All(TutorialScript.ExportWithoutKey, step => Assert.Empty(step.Usable));

        IReadOnlyList<TutorialStep> read = TutorialScript.StepsOf(TutorialTopic.Read);
        Assert.Equal([TutorialRegions.ReadLookup], read[0].Usable);
        Assert.Equal([TutorialRegions.ReadRead], read[1].Usable);
        Assert.Equal([TutorialRegions.ReadSave], read[2].Usable);
        Assert.Empty(read[3].Usable);
        Assert.Equal(TutorialAdvance.OnLookupSucceeded, read[0].Advance);
        Assert.Equal(TutorialAdvance.OnReadSucceeded, read[1].Advance);
    }

    [Fact]
    public void Usable_regions_are_lit()
    {
        Assert.All(TutorialScript.AllSteps, step => Assert.All(step.Usable, name => Assert.Contains(name, step.Lit)));
    }

    [Fact]
    public void Region_names_are_unique()
    {
        string[] names = typeof(TutorialRegions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_lit_region_is_a_declared_region()
    {
        HashSet<string> declared = typeof(TutorialRegions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);

        Assert.All(TutorialScript.AllSteps, step => Assert.All(step.Lit, name => Assert.Contains(name, declared)));
    }

    [Fact]
    public void Sample_link_is_the_site_link()
    {
        Assert.Equal("xrpl-file://mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5", TutorialScript.SampleLink);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_button_placeholder_is_filled_by_the_buttons_own_phrase(string code)
    {
        TextCatalog catalog = TextCatalog.For(UiLanguage.FromCode(code)!);
        List<string> wrong = [];

        foreach (TutorialStep step in TutorialScript.AllSteps)
        {
            string[] expected = step.Buttons.Select(button => button.Name).Order(StringComparer.Ordinal).ToArray();
            string[] actual = Placeholders(catalog, step.Title).Concat(Placeholders(catalog, step.Body))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            {
                wrong.Add($"{step.Body.Key}: [{string.Join(",", actual)}] not [{string.Join(",", expected)}]");
                continue;
            }

            (string, object)[] values = step.Buttons.Select(button => (button.Name, (object)button.Button)).ToArray();
            string rendered = catalog.Render(UiText.Of(step.Body, values));
            foreach ((string Name, TextId Button) button in step.Buttons)
            {
                if (!rendered.Contains(catalog[button.Button], StringComparison.Ordinal)) wrong.Add($"{step.Body.Key}: lacks '{catalog[button.Button]}'");
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        Assert.Equal(["learn"], Placeholders(catalog, TutorialText.ListNotNowHint));
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Step_counter_reads_as_a_plain_phrase(string code)
    {
        TextCatalog catalog = TextCatalog.For(UiLanguage.FromCode(code)!);

        string rendered = catalog.Render(UiText.Of(TutorialText.StepOf, ("number", 2), ("count", 6)));

        Assert.Contains("2", rendered, StringComparison.Ordinal);
        Assert.Contains("6", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Tutorial_copy_has_no_long_or_en_dash(string code)
    {
        TextCatalog catalog = TextCatalog.For(UiLanguage.FromCode(code)!);

        string[] wrong = catalog.Phrases
            .Where(entry => entry.Key.StartsWith("Tutorial.", StringComparison.Ordinal) || entry.Key == ShellText.Learn.Key)
            .Where(entry => entry.Value.Texts.Any(text => text.Contains('\u2014') || text.Contains('\u2013')))
            .Select(entry => entry.Key).ToArray();

        Assert.True(wrong.Length == 0, string.Join(", ", wrong));
    }

    private static string[] Placeholders(TextCatalog catalog, TextId id) =>
        catalog.Phrases[id.Key].Texts.SelectMany(text => PlaceholderPattern().Matches(text).Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"(?<!\{)\{(\w+)(?::[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
