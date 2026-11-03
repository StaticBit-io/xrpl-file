namespace XrplFile.App.Core.Localization;

/// <summary>The language a person chose in Settings, remembered across starts.</summary>
public interface ILanguageSettingsStore
{
    /// <summary>The remembered language code, or null when none was chosen or it cannot be read.</summary>
    string? Read();

    void Write(string code);
}
