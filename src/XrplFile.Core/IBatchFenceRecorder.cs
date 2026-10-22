namespace XrplFile;

/// <summary>
/// Persists the fence (spec B23): the highest deadline any transmission of the run has registered.
/// Returns once it is saved; a save that fails throws, and the transmission it was for is never sent.
/// </summary>
public interface IBatchFenceRecorder
{
    Task RecordFenceAsync(long deadline, CancellationToken cancellationToken);
}
