using System.Text;
using System.Text.Json;

namespace XrplFile.App.Core;

/// <summary>
/// The overrides as a small JSON object of network name to URL, kept beside C1's
/// <c>vault-location.txt</c> in the application's data directory.
/// </summary>
public sealed class FileNodeSettingsStore : INodeSettingsStore
{
    private const string SettingFileName = "node-urls.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _settingPath;

    public FileNodeSettingsStore(string? settingPath = null) =>
        _settingPath = settingPath ?? Path.Combine(
            Path.GetDirectoryName(VaultLocation.DefaultPath)!, SettingFileName);

    internal string SettingPath => _settingPath;

    public IReadOnlyDictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(_settingPath))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            Dictionary<string, string>? read = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllBytes(_settingPath));

            return read is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(read, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The same contract FileVaultLocationStore follows for the same reason: a settings file
            // left half-written by a power cut, or one on media that has gone away, must read as
            // "nothing has been chosen" rather than travel out of a screen's setup as an exception.
            // The unreadable file is left on disk rather than deleted -- overwriting what a person
            // configured, because this process could not parse it once, loses more than it fixes.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public void Write(IReadOnlyDictionary<string, string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);

        Directory.CreateDirectory(Path.GetDirectoryName(_settingPath)!);
        File.WriteAllBytes(
            _settingPath,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(urls, SerializerOptions)));
    }
}
