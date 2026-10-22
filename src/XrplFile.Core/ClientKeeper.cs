using System.Runtime.CompilerServices;
using Xrpl.Client;
using Xrpl.Client.Exceptions;

namespace XrplFile;

/// <summary>
/// The one owner of reconnection (spec B24): a client this repository builds has the SDK's own
/// reconnection switched off, and the next request after a dropped socket connects it again here.
/// The sink and the source of one connection share one client, and so one keeper: two callers that
/// meet the drop together reconnect once. A client its owner retired is refused, never brought back.
/// </summary>
public sealed class ClientKeeper
{
    private static readonly ConditionalWeakTable<IXrplClient, ClientKeeper> Keepers = new();

    private readonly IXrplClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _retired;

    /// <summary>
    /// Right after a drop <c>IsConnected()</c> is already false but the SDK is still tearing the dead
    /// socket down, and a <c>Connect</c> in that window fails -- measured on the stand: about 100 ms,
    /// then it connects. Those failures are retried; every other one is the node's.
    /// </summary>
    private const int CleanupAttempts = 5;

    private static readonly TimeSpan CleanupPoll = TimeSpan.FromMilliseconds(250);

    private ClientKeeper(IXrplClient client) => _client = client;

    /// <summary>A client for <paramref name="url"/> whose reconnection this class owns: the SDK's own is off.</summary>
    public static XrplClient CreateClient(string url) =>
        new(url, new XrplClient.ClientOptions { MaxReconnectAttempts = 0, StopAfterMaxAttempts = true });

    /// <summary>The client's owner has closed it: from now on every request on it is refused.</summary>
    public static void Retire(IXrplClient client) => For(client)._retired = true;

    internal static ClientKeeper For(IXrplClient client) => Keepers.GetValue(client, static owner => new ClientKeeper(owner));

    internal async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_retired) throw Closed();
        if (_client.IsConnected()) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_retired) throw Closed();
            if (_client.IsConnected()) return;

            for (int attempt = 1; ; attempt++)
            {
                if (_retired) throw Closed();

                bool connected = false;

                try
                {
                    await _client.Connect(cancellationToken).ConfigureAwait(false);
                    connected = true;
                }
                catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // The owner's Disconnect cancels a Connect in flight; that must not be retried.
                    if (_retired) throw Closed();

                    if (attempt >= CleanupAttempts || !SocketStillBeingCleanedUp(e))
                    {
                        throw new XrplFileException(XrplFileError.NodeUnreachable, $"the node did not take a new connection: {e.Message}", inner: e);
                    }
                }

                if (connected)
                {
                    if (!_retired) return;

                    // Retired while the connection was being made: close what was just opened.
                    await DisconnectQuietlyAsync().ConfigureAwait(false);
                    throw Closed();
                }

                await Task.Delay(CleanupPoll, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisconnectQuietlyAsync()
    {
        try
        {
            await _client.Disconnect().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The client is being abandoned; nothing a close throws leaves this method.
        }
    }

    // Matches Xrpl 11.2.0 Connection.ConnectInternalAsync ("Websocket connection never cleaned up."); re-verify on an SDK upgrade.
    private static bool SocketStillBeingCleanedUp(Exception e) =>
        e is OperationCanceledException
        || (e is XrplException && e.Message.Contains("never cleaned up", StringComparison.Ordinal));

    private static XrplFileException Closed() => new(XrplFileError.NodeUnreachable, "the connection was closed by its owner");
}
