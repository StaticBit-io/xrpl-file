using System.Net;
using System.Net.Sockets;

namespace XrplFile.Tests;

/// <summary>
/// A TCP relay in front of the stand that can cut every live connection at once, the way a public
/// node closes a client it finds too slow. It keeps listening, so a client that reconnects gets
/// through again.
/// </summary>
internal sealed class DroppingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly Lock _gate = new();
    private readonly List<(TcpClient Inbound, TcpClient Outbound)> _pairs = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

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
            foreach ((TcpClient inbound, TcpClient outbound) in _pairs)
            {
                // A reset rather than an orderly close: the client sees the connection fail.
                inbound.Client.LingerState = new LingerOption(true, 0);
                inbound.Close();
                outbound.Close();
            }

            _pairs.Clear();
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

            lock (_gate) _pairs.Add((inbound, outbound));

            _ = PumpAsync(inbound.GetStream(), outbound.GetStream());
            _ = PumpAsync(outbound.GetStream(), inbound.GetStream());
        }
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        byte[] buffer = new byte[64 * 1024];

        try
        {
            while (true)
            {
                int read = await from.ReadAsync(buffer);
                if (read == 0) break;
                await to.WriteAsync(buffer.AsMemory(0, read));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            // The pair was dropped.
        }
        finally
        {
            from.Close();
            to.Close();
        }
    }
}
