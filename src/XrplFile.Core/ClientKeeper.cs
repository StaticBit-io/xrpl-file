using System.Runtime.CompilerServices;
using Xrpl.Client;
using Xrpl.Client.Exceptions;

namespace XrplFile;

/// <summary>
/// The one owner of reconnection (spec B24): a client this repository builds has the SDK's own
/// reconnection switched off, and the next request after a dropped socket connects it again here.
/// A socket that died without a close still reads as open, so a request left unanswered with
/// nothing else answered since it was sent costs the connection too: the next request replaces it.
/// The sink and the source of one connection share one client, and so one keeper: two callers that
/// meet the drop together reconnect once. A client its owner retired is refused, never brought back.
/// </summary>
public sealed class ClientKeeper
{
    private static readonly ConditionalWeakTable<IXrplClient, ClientKeeper> Keepers = new();

    private readonly IXrplClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _state = new();
    private volatile bool _retired;

    /// <summary>Counts the connections this keeper made; a request belongs to the one it was sent over.</summary>
    private long _generation;

    /// <summary>The connection a request left unanswered, with nothing answered after it was sent; -1 when none.</summary>
    private long _silentGeneration = -1;

    /// <summary>Answers received over any connection of this client, the node's error answers included.</summary>
    private long _answers;

    /// <summary>
    /// Right after a drop <c>IsConnected()</c> is already false but the SDK is still tearing the dead
    /// socket down, and a <c>Connect</c> in that window fails -- measured on the stand: about 100 ms,
    /// then it connects. Those failures are retried; every other one is the node's.
    /// </summary>
    private const int CleanupAttempts = 5;

    private static readonly TimeSpan CleanupPoll = TimeSpan.FromMilliseconds(250);

    private ClientKeeper(IXrplClient client) => _client = client;

    /// <summary>A client for <paramref name="url"/> whose reconnection this class owns: the SDK's own is off.</summary>
    public static XrplClient CreateClient(string url)
    {
        XrplClient.ClientOptions options = new()
        {
            MaxReconnectAttempts = 0,
            StopAfterMaxAttempts = true,

            // Xrpl 11.2.0's ping check replaces a socket it finds silent on its own, past
            // MaxReconnectAttempts = 0, and requests sent meanwhile wait out its connection wait;
            // measured on the stand, it noticed a silent socket only after about 85 s and left the
            // client reading Disconnected over an open socket. Silence is the keeper's to act on.
            UseCustomPing = false,
            UseCheckHealth = false,

            // A request over a socket that is not open fails at once instead of waiting inside the
            // SDK for a connection; the keeper makes the next one.
            RequestPolicy = RequestFailurePolicy.ImmediateFail,
        };

        // Connect waits for the socket up to this long after the attempt itself; the SDK's default is
        // 5 min, and nothing here wants a connect to outlast one attempt.
        options.ConnectionAcquisitionTimeout = options.ConnectionAttemptTimeout;

        return new XrplClient(url, options);
    }

    /// <summary>The client's owner has closed it: from now on every request on it is refused.</summary>
    public static void Retire(IXrplClient client) => For(client)._retired = true;

    internal static ClientKeeper For(IXrplClient client) => Keepers.GetValue(client, static owner => new ClientKeeper(owner));

    /// <summary>
    /// Sends one request over a connection this keeper vouches for, and records whether it was
    /// answered. Every failure reaches the caller unchanged.
    /// </summary>
    internal async Task<T> SendAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken)
    {
        (long generation, long answersAtSend) = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        T answer;

        try
        {
            answer = await request().ConfigureAwait(false);
        }
        catch (RippledException)
        {
            Interlocked.Increment(ref _answers);
            throw;
        }
        catch (Exception e) when (Unanswered(e, cancellationToken))
        {
            MarkSilentIfNothingAnswered(generation, answersAtSend);
            throw;
        }

        Interlocked.Increment(ref _answers);

        return answer;
    }

    /// <returns>The connection the next request goes over, and the answers counted before it is sent.</returns>
    internal async Task<(long Generation, long Answers)> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_retired) throw Closed();
        if (!IsSilent() && _client.IsConnected()) return Current();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_retired) throw Closed();

            bool silent = IsSilent();
            if (!silent && _client.IsConnected()) return Current();

            // Closed by this keeper, not retired: the owner's client stays usable, and the requests
            // still waiting on the silent socket are released now instead of at their own timeouts.
            if (silent) await DisconnectQuietlyAsync().ConfigureAwait(false);

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
                    if (!_retired)
                    {
                        lock (_state) _generation++;

                        return Current();
                    }

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

    private (long Generation, long Answers) Current()
    {
        lock (_state) return (_generation, Interlocked.Read(ref _answers));
    }

    private bool IsSilent()
    {
        lock (_state) return _silentGeneration == _generation;
    }

    /// <summary>
    /// A request that failed while other answers kept arriving met a slow node, not a dead socket.
    /// One sent over a connection since replaced says nothing about the current one.
    /// </summary>
    private void MarkSilentIfNothingAnswered(long generation, long answersAtSend)
    {
        lock (_state)
        {
            if (generation == _generation && Interlocked.Read(ref _answers) == answersAtSend) _silentGeneration = generation;
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

    /// <summary>
    /// A request that came back with nothing from the node: a timeout, a connection lost or never
    /// made, or a cancellation the caller's token did not cause -- the SDK cancels requests in flight
    /// when a socket goes. A node's own error answer (<see cref="RippledException"/>) is an answer.
    /// </summary>
    private static bool Unanswered(Exception e, CancellationToken cancellationToken) =>
        e is XrplException and not RippledException
        || e is System.TimeoutException
        || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    // Matches Xrpl 11.2.0 Connection.ConnectInternalAsync ("Websocket connection never cleaned up."); re-verify on an SDK upgrade.
    private static bool SocketStillBeingCleanedUp(Exception e) =>
        e is OperationCanceledException
        || (e is XrplException && e.Message.Contains("never cleaned up", StringComparison.Ordinal));

    private static XrplFileException Closed() => new(XrplFileError.NodeUnreachable, "the connection was closed by its owner");
}
