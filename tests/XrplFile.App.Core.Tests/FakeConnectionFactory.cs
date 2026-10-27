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

    /// <summary>When set, <see cref="ConnectAsync"/> returns a failed task with it.</summary>
    public Exception? ConnectThrows { get; set; }

    /// <summary>
    /// Awaited first in <see cref="ConnectAsync"/> with the network asked for: a test may throw from
    /// it, hold the connection on a gate, or ignore the token to model a node that does not answer.
    /// </summary>
    public Func<string, CancellationToken, Task>? BeforeConnect { get; set; }

    /// <summary>The network of every connection handed out, in order.</summary>
    public List<string> ConnectedNetworks { get; } = [];

    /// <summary>The Batch node, once <see cref="EnableBatch"/> created it; connections then hand it out as their sink.</summary>
    public FakeBatchLedgerSink? BatchSink { get; private set; }

    public FakeConnectionFactory() => Sink = new FakeLedgerSink(Ledger);

    /// <summary>
    /// Switches every later connection to a <see cref="FakeBatchLedgerSink"/> over the same ledger.
    /// Not the default: that sink closes a ledger on every read, which the one-account tests do not expect.
    /// </summary>
    public FakeBatchLedgerSink EnableBatch() => BatchSink ??= new FakeBatchLedgerSink(Ledger);

    public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
    {
        if (BeforeConnect is not null) await BeforeConnect(network, cancellationToken);

        if (ConnectThrows is not null) throw ConnectThrows;

        Connections++;
        ConnectedNetworks.Add(network);
        return new Connection(this);
    }

    private sealed class Connection(FakeConnectionFactory owner) : ILedgerConnection
    {
        public ILedgerSource Source => owner.Ledger;

        public ILedgerSink Sink => (ILedgerSink?)owner.BatchSink ?? owner.Sink;

        public string Url => "fake://node";

        public ValueTask DisposeAsync()
        {
            owner.Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
