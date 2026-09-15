using XrplFile.Tests;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// The node, and only the node, replaced. Every connection it hands out shares one
/// <see cref="FakeLedgerSource"/> and one <see cref="FakeLedgerSink"/>, so a test can set the ledger
/// up once and then watch an estimate and a run agree about it.
/// </summary>
public sealed class FakeConnectionFactory : IConnectionFactory
{
    public FakeLedgerSource Ledger { get; } = new();

    public FakeLedgerSink Sink { get; }

    public int Connections { get; private set; }

    public int Disposals { get; private set; }

    /// <summary>When set, <see cref="ConnectAsync"/> throws it instead of connecting.</summary>
    public Exception? ConnectThrows { get; set; }

    public FakeConnectionFactory() => Sink = new FakeLedgerSink(Ledger);

    public Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
    {
        if (ConnectThrows is not null) throw ConnectThrows;

        Connections++;
        return Task.FromResult<ILedgerConnection>(new Connection(this));
    }

    private sealed class Connection(FakeConnectionFactory owner) : ILedgerConnection
    {
        public ILedgerSource Source => owner.Ledger;

        public ILedgerSink Sink => owner.Sink;

        public string Url => "fake://node";

        public ValueTask DisposeAsync()
        {
            owner.Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
