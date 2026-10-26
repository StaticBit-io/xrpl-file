namespace XrplFile;

/// <summary>
/// A round's candidates, registered under the round's lock (spec 4.1 steps 3–4). Registration
/// persists the deadline first and reserves the candidate for every memo it carries; a halt is set
/// under the same lock, so once it is set nothing more registers and a signed blob is discarded unsent.
/// </summary>
internal sealed class BatchRegistry(BatchFence fence, byte[] candidatesPerMemo, BatchTrace trace)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Lock _listGate = new();
    private readonly List<BatchCandidate> _candidates = [];
    private volatile string? _halt;

    /// <summary>The fence this registry covers its deadlines with: the strategy's, shared by every round.</summary>
    internal BatchFence Fence => fence;

    public bool IsHalted => _halt is not null;

    public string? HaltReason => _halt;

    public IReadOnlyList<BatchCandidate> Candidates
    {
        get { lock (_listGate) return [.. _candidates]; }
    }

    /// <summary>The registered outers, by ordinal. A single sender registers them in order.</summary>
    public IReadOnlyList<BatchCandidate> Outers
    {
        get { lock (_listGate) return [.. _candidates.Where(candidate => candidate.IsOuter).OrderBy(candidate => candidate.Ordinal)]; }
    }

    public async Task<bool> TryRegisterAsync(BatchCandidate candidate, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_halt is not null)
            {
                trace.Add($"discard:{candidate.Ordinal}");
                return false;
            }

            await fence.CoverAsync(candidate.LastLedger, cancellationToken).ConfigureAwait(false);

            foreach (int memo in candidate.Memos)
            {
                candidatesPerMemo[memo] = checked((byte)(candidatesPerMemo[memo] + 1));
            }

            lock (_listGate) _candidates.Add(candidate);
            trace.Add($"register:{candidate.Ordinal}");
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task HaltAsync(string reason, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_halt is not null) return;

            _halt = reason;
            trace.Add($"halt:{reason}");
        }
        finally
        {
            _lock.Release();
        }
    }
}
