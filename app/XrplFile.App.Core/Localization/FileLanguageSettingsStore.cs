using System.Text;

namespace XrplFile.App.Core.Localization;

/// <summary>
/// The chosen language code as one line of <c>ui-language.txt</c>, beside <c>node-urls.json</c> in the
/// application's data directory.
/// </summary>
public sealed class FileLanguageSettingsStore : ILanguageSettingsStore
{
    private const string SettingFileName = "ui-language.txt";

    private readonly string _settingPath;

    public FileLanguageSettingsStore(string? settingPath = null) =>
        _settingPath = settingPath ?? Path.Combine(
            Path.GetDirectoryName(VaultLocation.DefaultPath)!, SettingFileName);

    public string? Read()
    {
        try
        {
            if (!File.Exists(_settingPath)) return null;

            string code = File.ReadAllText(_settingPath, Encoding.UTF8).Trim();
            return code.Length == 0 ? null : code;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The contract of every settings store here: a file that cannot be read means nothing was
            // chosen, and it is left on disk rather than deleted.
            return null;
        }
    }

    public void Write(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        Directory.CreateDirectory(Path.GetDirectoryName(_settingPath)!);
        File.WriteAllText(_settingPath, code, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
