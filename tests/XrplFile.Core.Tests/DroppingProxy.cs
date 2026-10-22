using System.Net;
using System.Net.Sockets;

namespace XrplFile.Tests;

/// <summary>
/// A TCP relay in front of the stand that can cut every live connection at once, the way a public
/// node closes a client it finds too slow, or silence them, the way a connection behind a NAT or a
/// load balancer dies without a close. It keeps listening, so a client that reconnects gets
/// through again.
/// </summary>
internal sealed class DroppingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly Lock _gate = new();
    private readonly List<Pair> _pairs = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    private sealed class Pair(TcpClient inbound, TcpClient outbound)
    {
        public TcpClient Inbound { get; } = inbound;

        public TcpClient Outbound { get; } = outbound;

        public volatile bool Silenced;
    }

    public DroppingProxy(Uri target)
    {
        _targetHost = target.Host;
        _targetPort = target.Port;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public Uri Endpoint => new($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    public void DropAll()
    {
        lock (_gate)
        {
            foreach (Pair pair in _pairs)
            {
                // A reset rather than an orderly close: the client sees the connection fail.
                try
                {
                    pair.Inbound.Client.LingerState = new LingerOption(true, 0);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                    // Already closed from the other side; closing it below is all that is left.
                }

                pair.Inbound.Close();
                pair.Outbound.Close();
            }

            _pairs.Clear();
        }
    }

    /// <summary>
    /// Every live connection stays open and stops carrying bytes either way: the client's socket
    /// still reads as open, and nothing it sends is ever answered. Connections made afterwards work.
    /// </summary>
    public void SilenceAll()
    {
        lock (_gate)
        {
            foreach (Pair pair in _pairs) pair.Silenced = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        DropAll();

        try
        {
            await _accepting;
        }
        catch (Exception)
        {
            // The listener is gone; nothing waits on the loop any more.
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient inbound;

            try
            {
                inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            TcpClient outbound = new();

            try
            {
                await outbound.ConnectAsync(_targetHost, _targetPort, _stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException)
            {
                inbound.Close();
                outbound.Dispose();
                continue;
            }

            Pair pair = new(inbound, outbound);
            lock (_gate) _pairs.Add(pair);

            _ = PumpAsync(pair, inbound.GetStream(), outbound.GetStream());
            _ = PumpAsync(pair, outbound.GetStream(), inbound.GetStream());
        }
    }

    private static async Task PumpAsync(Pair pair, NetworkStream from, NetworkStream to)
    {
        byte[] buffer = new byte[64 * 1024];

        try
        {
            while (true)
            {
                int read = await from.ReadAsync(buffer);
                if (read == 0) break;
                if (pair.Silenced) continue;
                await to.WriteAsync(buffer.AsMemory(0, read));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            // The pair was dropped.
        }
        finally
        {
            // A silenced pair is left open: closing it would tell the client what silence must not.
            if (!pair.Silenced)
            {
                from.Close();
                to.Close();
            }
        }
    }
}
