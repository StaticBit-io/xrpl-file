using System.Text;

namespace XrplFile.App.Core.Tutorial;

/// <summary>
/// The "tutorial was offered" flag as the existence of <c>tutorial-offered.txt</c> beside
/// <c>ui-language.txt</c>; the single line <c>offered</c> inside is for a human looking at the folder.
/// </summary>
/// <remarks>
/// Existence alone is the flag; the content is never read. Unlike the other settings stores, a file
/// that is empty or cannot be read therefore still counts as offered, which avoids showing the offer
/// again on every start.
/// </remarks>
public sealed class FileTutorialSettingsStore(string? settingPath = null) : ITutorialSettingsStore
{
    private const string SettingFileName = "tutorial-offered.txt";

    private readonly string _settingPath = settingPath ?? Path.Combine(
        Path.GetDirectoryName(VaultLocation.DefaultPath)!, SettingFileName);

    public bool WasOffered()
    {
        // File.Exists never throws: it reports false for an unreadable or inaccessible path.
        return File.Exists(_settingPath);
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
