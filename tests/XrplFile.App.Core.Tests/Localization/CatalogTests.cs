using System.Reflection;
using System.Text.RegularExpressions;
using XrplFile.App.Core.Localization;
using Xunit;

namespace XrplFile.App.Core.Tests.Localization;

/// <summary>
/// English is the contract: every other catalog carries exactly its phrases, with the same placeholder
/// names and the plural forms its language needs; and the code declares exactly the phrases English has.
/// </summary>
public sealed partial class CatalogTests
{
    public static TheoryData<string> OtherLanguages() =>
        new(UiLanguage.All.Where(language => language != UiLanguage.English).Select(language => language.Code));

    public static TheoryData<string> AllLanguages() => new(UiLanguage.All.Select(language => language.Code));

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void Every_language_has_exactly_the_English_phrases(string code)
    {
        TextCatalog english = TextCatalog.For(UiLanguage.English);
        TextCatalog other = TextCatalog.For(UiLanguage.FromCode(code)!);

        string[] missing = english.Phrases.Keys.Except(other.Phrases.Keys).Order(StringComparer.Ordinal).ToArray();
        string[] extra = other.Phrases.Keys.Except(english.Phrases.Keys).Order(StringComparer.Ordinal).ToArray();

        Assert.True(missing.Length == 0, $"{code} lacks: {string.Join(", ", missing)}");
        Assert.True(extra.Length == 0, $"{code} has phrases English does not: {string.Join(", ", extra)}");
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void Every_phrase_takes_the_same_values_as_in_English(string code)
    {
        TextCatalog english = TextCatalog.For(UiLanguage.English);
        TextCatalog other = TextCatalog.For(UiLanguage.FromCode(code)!);

        List<string> wrong = [];
        foreach ((string key, TextCatalog.Phrase phrase) in other.Phrases)
        {
            if (!english.Phrases.TryGetValue(key, out TextCatalog.Phrase? reference)) continue;

            string expected = string.Join(",", Placeholders(reference));
            foreach (string text in phrase.Texts)
            {
                string actual = string.Join(",", Placeholders(text));
                if (!string.Equals(expected, actual, StringComparison.Ordinal)) wrong.Add($"{key}: [{actual}] not [{expected}]");
            }

            if ((reference.Forms is null) != (phrase.Forms is null)) wrong.Add($"{key}: plural in one language only");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_plural_phrase_has_the_forms_its_language_needs(string code)
    {
        UiLanguage language = UiLanguage.FromCode(code)!;
        TextCatalog catalog = TextCatalog.For(language);
        IReadOnlyList<PluralForm> needed = PluralRules.FormsOf(language);

        string[] wrong = catalog.Phrases
            .Where(entry => entry.Value.Forms is { } forms && !forms.Keys.Order().SequenceEqual(needed.Order()))
            .Select(entry => $"{entry.Key}: {string.Join("/", entry.Value.Forms!.Keys)} not {string.Join("/", needed)}")
            .ToArray();

        Assert.True(wrong.Length == 0, string.Join(Environment.NewLine, wrong));
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_phrase_renders(string code)
    {
        TextCatalog catalog = TextCatalog.For(UiLanguage.FromCode(code)!);

        foreach ((string key, TextCatalog.Phrase phrase) in catalog.Phrases)
        {
            // A plural phrase is chosen by `count` whether or not any of its forms prints the number ("It stays" / "They stay").
            string[] names = phrase.Texts.SelectMany(Placeholders).Concat(phrase.Forms is null ? [] : ["count"]).Distinct(StringComparer.Ordinal).ToArray();
            foreach (long count in new long[] { 0, 1, 2, 5, 21 })
            {
                (string, object)[] values = names.Select(name => (name, (object)(name == "count" ? count : 2L))).ToArray();
                string rendered = catalog.Render(UiText.Of(new TextId(key), values));
                Assert.False(string.IsNullOrWhiteSpace(rendered), $"{code} {key} renders empty");
            }
        }
    }

    [Fact]
    public void The_code_declares_exactly_the_English_phrases()
    {
        List<string> declared = [];
        List<string> misnamed = [];
        foreach (Type area in typeof(TextId).Assembly.GetTypes().Where(type =>
                     type.Namespace == typeof(TextId).Namespace && type.Name.EndsWith("Text", StringComparison.Ordinal)))
        {
            foreach (FieldInfo field in area.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(TextId)))
            {
                string key = ((TextId)field.GetValue(null)!).Key;
                declared.Add(key);
                string expected = $"{area.Name[..^"Text".Length]}.{field.Name}";
                if (!string.Equals(key, expected, StringComparison.Ordinal)) misnamed.Add($"{area.Name}.{field.Name} is '{key}', not '{expected}'");
            }
        }

        string[] english = TextCatalog.For(UiLanguage.English).Phrases.Keys.ToArray();
        string[] undeclared = english.Except(declared).Order(StringComparer.Ordinal).ToArray();
        string[] unwritten = declared.Except(english).Order(StringComparer.Ordinal).ToArray();

        Assert.True(misnamed.Count == 0, string.Join(Environment.NewLine, misnamed));
        Assert.True(unwritten.Length == 0, $"Declared but not in en.json: {string.Join(", ", unwritten)}");
        Assert.True(undeclared.Length == 0, $"In en.json but not declared: {string.Join(", ", undeclared)}");
        Assert.Equal(declared.Count, declared.Distinct(StringComparer.Ordinal).Count());
    }

    private static IEnumerable<string> Placeholders(TextCatalog.Phrase phrase) =>
        phrase.Texts.SelectMany(Placeholders).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    private static IEnumerable<string> Placeholders(string text) =>
        PlaceholderPattern().Matches(text.Replace("{{", string.Empty, StringComparison.Ordinal))
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)(?::[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
