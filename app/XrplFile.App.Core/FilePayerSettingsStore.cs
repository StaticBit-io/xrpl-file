using System.Text;
using System.Text.Json;

namespace XrplFile.App.Core;

/// <summary>
/// The payer memory as a small JSON object of network name to vault reference, in
/// <c>payers.json</c> beside <c>node-urls.json</c>. Read the defensive way the node settings are: a
/// file that cannot be read remembers nothing rather than stopping the Publish screen from opening.
/// </summary>
public sealed class FilePayerSettingsStore(string? settingPath = null) : IPayerSettingsStore
{
    private const string SettingFileName = "payers.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _settingPath = settingPath ?? Path.Combine(Path.GetDirectoryName(VaultLocation.DefaultPath)!, SettingFileName);

    public string? LastPayer(string network) => Read().TryGetValue(network, out string? reference) ? reference : null;

    public void Remember(string network, string payerReference)
    {
        ArgumentException.ThrowIfNullOrEmpty(network);
        ArgumentException.ThrowIfNullOrEmpty(payerReference);

        Dictionary<string, string> payers = Read();
        payers[network] = payerReference;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingPath)!);
            File.WriteAllBytes(_settingPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payers, SerializerOptions)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A default that is not remembered costs one click next time; it must not cost the run.
        }
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(_settingPath)) return new Dictionary<string, string>(StringComparer.Ordinal);

            Dictionary<string, string>? read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(_settingPath));
            return read is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(read, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
