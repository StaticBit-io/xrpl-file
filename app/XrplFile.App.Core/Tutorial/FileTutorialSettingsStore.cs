using System.Text;

namespace XrplFile.App.Core.Tutorial;

/// <summary>
/// The "tutorial was offered" flag as the existence of <c>tutorial-offered.txt</c> beside
/// <c>ui-language.txt</c>; the single line <c>offered</c> inside is for a human looking at the folder.
/// </summary>
/// <remarks>
/// Unlike the other settings stores, a flag that exists but cannot be read still counts as offered.
/// Reading the content decides nothing here, and treating an unreadable file as "never offered"
/// would show the offer again on every start, which is worse than not showing it once more.
/// </remarks>
public sealed class FileTutorialSettingsStore(string? settingPath = null) : ITutorialSettingsStore
{
    private const string SettingFileName = "tutorial-offered.txt";

    private readonly string _settingPath = settingPath ?? Path.Combine(
        Path.GetDirectoryName(VaultLocation.DefaultPath)!, SettingFileName);

    public bool WasOffered()
    {
        try
        {
            return File.Exists(_settingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public bool TryMarkOffered()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingPath)!);
            File.WriteAllText(_settingPath, "offered", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Offering again next start costs one dismissal; it must not cost the run.
            return false;
        }
    }
}
