using Xrpl.Client;

namespace XrplFile.App.Core;

public sealed class XrplConnectionFactory(INodeSettingsStore settings) : IConnectionFactory
{
    public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(network);

        string url = NodeEndpoint.Resolve(settings, network)
            ?? throw new XrplFileException(
                XrplFileError.NodeUnreachable,
                $"no node URL is known for the network '{network}'; add one in Settings");

        IXrplClient? client = null;

        try
        {
            client = new XrplClient(url);
            await client.Connect(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Disposed on every failure, cancellation included: the client is being abandoned either
            // way, and only what is thrown afterwards differs.
            if (client is not null)
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception disposeEx) when (disposeEx is not OperationCanceledException)
                {
                    // A close that fails has nothing left to report -- throwing here would replace the
                    // outcome the caller needs with an unrelated failure to hang up.
                }
            }

            // Cancellation is not an unreachable node, and a caller that asked for the operation to stop
            // must not be told the node is down.
            if (ex is OperationCanceledException) throw;

            // Everything a failed WebSocket handshake can throw -- a refused connection, a DNS
            // failure, a TLS failure, a protocol error -- is the same fact to the person holding
            // the screen, and the inner exception is kept so a log still carries which one it was.
            throw new XrplFileException(
                XrplFileError.NodeUnreachable, $"the node at {url} did not answer", inner: ex);
        }

        return new Connection(client, url);
    }

    private sealed class Connection : ILedgerConnection
    {
        private readonly IXrplClient _client;

        public Connection(IXrplClient client, string url)
        {
            _client = client;
            Url = url;
            Source = new XrplClientLedgerSource(client);
            Sink = new XrplClientLedgerSink(client);
        }

        public ILedgerSource Source { get; }

        public ILedgerSink Sink { get; }

        public string Url { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _client.Disconnect().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A close that fails has nothing left to report: the connection is being abandoned
                // either way, and throwing here would replace whatever outcome the caller was about
                // to deliver with a failure to hang up.
            }
        }
    }
}
