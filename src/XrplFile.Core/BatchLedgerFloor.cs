namespace XrplFile;

/// <summary>
/// The highest validated ledger any snapshot of this run has read (spec B6). A snapshot below it comes
/// from a node that is behind the one the run has already been served by -- a load balancer's other
/// backend, or another node after a reconnect -- and its sequences are older than ones the run has
/// acted on, so the run does not use it. It lives as long as the strategy and never moves down.
/// </summary>
internal sealed class BatchLedgerFloor
{
    private readonly Lock _gate = new();
    private long _value;

    public long Value
    {
        get { lock (_gate) return _value; }
    }

    public void Raise(long ledgerIndex)
    {
        lock (_gate)
        {
            if (ledgerIndex > _value) _value = ledgerIndex;
        }
    }
}
