using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class ConnectionFactoryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-connect-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_network_with_no_url_is_refused_before_any_socket_is_opened()
    {
        FileNodeSettingsStore settings = new(Path.Combine(_directory, "node-urls.json"));
        XrplConnectionFactory factory = new(settings);

        XrplFileException thrown = await Assert.ThrowsAsync<XrplFileException>(
            () => factory.ConnectAsync("standalone", TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, thrown.Error);
        Assert.Contains("standalone", thrown.Message, StringComparison.Ordinal);
    }
}
