namespace XrplFile;

/// <summary>
/// The strategy's side of the fence (spec B23): asks the recorder to persist a deadline only when it
/// rises past the one already saved. Called under a round's registration lock, so never concurrently.
/// </summary>
internal sealed class BatchFence(IBatchFenceRecorder recorder)
{
    private long _recorded;

    public long Recorded => Interlocked.Read(ref _recorded);

    public async Task CoverAsync(long deadline, CancellationToken cancellationToken)
    {
        if (deadline <= Recorded) return;

        await recorder.RecordFenceAsync(deadline, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _recorded, deadline);
    }
}
