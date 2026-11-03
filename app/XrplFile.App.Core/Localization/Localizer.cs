using System.Globalization;

namespace XrplFile.App.Core.Localization;

/// <summary>
/// The application's one localization service: which language the screens speak, and the catalog that
/// renders it. A singleton; the shell cascades <see cref="Text"/> and re-renders on <see cref="Changed"/>.
/// </summary>
public sealed class Localizer
{
    private readonly ILanguageSettingsStore _store;

    /// <param name="systemCulture">The OS UI culture at start-up, the default when nothing was chosen.</param>
    public Localizer(ILanguageSettingsStore store, CultureInfo systemCulture)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(systemCulture);

        _store = store;
        Language = Pick(store.Read(), systemCulture);
        Text = TextCatalog.For(Language);
    }

    public UiLanguage Language { get; private set; }

    public TextCatalog Text { get; private set; }

    public IReadOnlyList<UiLanguage> Choices => UiLanguage.All;

    /// <summary>Raised after the language changed; the handler may run on any thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// The remembered choice when it names a language the application has; else the OS UI culture by
    /// primary subtag, walking up its parents (a custom culture can carry an unknown tag over a known
    /// parent); else English. The site's <c>getLang</c>, with the OS in place of the browser.
    /// </summary>
    public static UiLanguage Pick(string? remembered, CultureInfo systemCulture)
    {
        if (UiLanguage.FromCode(remembered) is { } chosen) return chosen;

        for (CultureInfo culture = systemCulture; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
        {
            if (UiLanguage.FromTag(culture.Name) is { } system) return system;
        }

        return UiLanguage.English;
    }

    /// <summary>
    /// Switches the screens to <paramref name="language"/> and remembers it. The switch happens even when
    /// the choice cannot be written; the return value says whether it will survive a restart.
    /// </summary>
    public bool Choose(UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);

        bool remembered = true;
        try
        {
            _store.Write(language.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            remembered = false;
        }

        if (language != Language)
        {
            Language = language;
            Text = TextCatalog.For(language);
            Changed?.Invoke();
        }

        return remembered;
    }
}
