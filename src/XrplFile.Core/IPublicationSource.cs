namespace XrplFile;

/// <summary>
/// The bytes of one publication, read a chunk at a time (spec S2), so that no part of the
/// publication path has to hold the file. A session borrows a source; whoever created it owns it
/// and disposes it.
/// </summary>
public interface IPublicationSource : IAsyncDisposable
{
    /// <summary>The file's length in bytes, as it was when the source was opened.</summary>
    long Length { get; }

    /// <summary>
    /// Fills <paramref name="destination"/> with chunk <paramref name="index"/>: exactly
    /// min(<see cref="MemoLayout.ChunkBytes"/>, <see cref="Length"/> − index × <see cref="MemoLayout.ChunkBytes"/>)
    /// bytes, retrying reads that return fewer, and returns that count. Bytes of
    /// <paramref name="destination"/> past the count are left as they were.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The file has no such chunk.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the chunk.</exception>
    /// <exception cref="IOException">The file ended before the chunk did.</exception>
    int ReadChunk(long index, Span<byte> destination);

    /// <summary>The asynchronous form of <see cref="ReadChunk"/>, with the same contract.</summary>
    ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken);
}
