using System.Reflection;

using Xrpl.Client;

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

    [Fact]
    public async Task A_cancellation_the_caller_did_not_ask_for_is_an_unreachable_node()
    {
        // The SDK can end a connect with an OperationCanceledException of its own (a connection
        // wait swept on its side). Callers such as BatchAvailability and the publish screen catch
        // XrplFileException only, and a raw cancellation there is read as the screen being left.
        XrplConnectionFactory factory = new(
            new FileNodeSettingsStore(Path.Combine(_directory, "node-urls.json")),
            _ => FailingClient.Create(new OperationCanceledException("the SDK gave up waiting")));

        XrplFileException thrown = await Assert.ThrowsAsync<XrplFileException>(
            () => factory.ConnectAsync("testnet", TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, thrown.Error);
        Assert.IsType<OperationCanceledException>(thrown.InnerException);
    }

    [Fact]
    public async Task A_cancellation_the_caller_asked_for_stays_a_cancellation()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        XrplConnectionFactory factory = new(
            new FileNodeSettingsStore(Path.Combine(_directory, "node-urls.json")),
            _ => FailingClient.Create(new OperationCanceledException(cancelled.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.ConnectAsync("testnet", cancelled.Token));
    }

    /// <summary>An <see cref="IXrplClient"/> whose <c>Connect</c> fails with a given exception; every other member is inert.</summary>
    public class FailingClient : DispatchProxy
    {
        private Exception _failure = new InvalidOperationException();

        public static IXrplClient Create(Exception failure)
        {
            IXrplClient client = Create<IXrplClient, FailingClient>();
            ((FailingClient)(object)client)._failure = failure;
            return client;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IXrplClient.Connect)
                ? Task.FromException(_failure)
                : null;
    }
}
