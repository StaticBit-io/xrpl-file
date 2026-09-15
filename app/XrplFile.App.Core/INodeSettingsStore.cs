namespace XrplFile.App.Core;

/// <summary>
/// The node URL each network is reached at, when the application is not to use
/// <see cref="Endpoints.For"/>'s own answer (design F4). A store that cannot answer reports no
/// overrides at all rather than throwing: this is read during screen setup, and a malformed
/// settings file must not be able to stop a screen from opening.
/// </summary>
public interface INodeSettingsStore
{
    IReadOnlyDictionary<string, string> Read();

    void Write(IReadOnlyDictionary<string, string> urls);
}
