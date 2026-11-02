using Xrpl.Client;

namespace XrplFile.App.Core;

public sealed class XrplConnectionFactory : IConnectionFactory
{
    private readonly INodeSettingsStore _settings;
    private readonly Func<string, IXrplClient> _createClient;

    public XrplConnectionFactory(INodeSettingsStore settings)
        : this(settings, ClientKeeper.CreateClient)
    {
    }

    /// <summary>For tests: a client that fails a connect in a chosen way without a node.</summary>
    internal XrplConnectionFactory(INodeSettingsStore settings, Func<string, IXrplClient> createClient)
    {
        _settings = settings;
        _createClient = createClient;
    }

    public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(network);

        string url = NodeEndpoint.Resolve(_settings, network)
            ?? throw new XrplFileException(
                XrplFileError.NodeUnreachable,
                $"no node URL is known for the network '{network}'; add one in Settings");

        IXrplClient? client = null;

        try
        {
            client = _createClient(url);
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
                catch (Exception)
                {
                    // Nothing a close throws leaves this method: the caller's outcome is protected.
                }
            }

            // Cancellation is not an unreachable node, and a caller that asked for the operation to stop
            // must not be told the node is down. Only the caller's own cancellation, though: one the
            // SDK raised by itself (a connection wait it swept) is a connect that failed, and callers
            // that catch XrplFileException alone would otherwise read it as their screen closing.
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;

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
                ClientKeeper.Retire(_client);
                await _client.Disconnect().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Nothing a close throws leaves this method: the caller's outcome is protected.
            }
        }
    }
}
