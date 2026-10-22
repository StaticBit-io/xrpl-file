namespace XrplFile.Tests;

/// <summary>A fence recorder that remembers every fence it was asked to persist, and can fail as a full disk does.</summary>
internal sealed class RecordingFence : IBatchFenceRecorder
{
    private readonly Lock _gate = new();
    private readonly List<long> _recorded = [];

    public bool Fail { get; set; }

    public Action<long>? OnRecord { get; set; }

    public IReadOnlyList<long> Recorded
    {
        get { lock (_gate) return [.. _recorded]; }
    }

    public long Highest
    {
        get { lock (_gate) return _recorded.Count == 0 ? 0 : _recorded.Max(); }
    }

    public Task RecordFenceAsync(long deadline, CancellationToken cancellationToken)
    {
        if (Fail) throw new IOException("fake: the state file could not be written");

        lock (_gate) _recorded.Add(deadline);
        OnRecord?.Invoke(deadline);
        return Task.CompletedTask;
    }
}
