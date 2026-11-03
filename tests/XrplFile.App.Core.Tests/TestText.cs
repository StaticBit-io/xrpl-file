using XrplFile.App.Core.Localization;

namespace XrplFile.App.Core.Tests;

/// <summary>Model texts as an English reader sees them, so the suites keep stating their expectations in words.</summary>
internal static class TestText
{
    public static TextCatalog English => TextCatalog.For(UiLanguage.English);

    public static string En(UiText? text) => English.Render(text);
}
