namespace XrplFile;

/// <summary>
/// Bytes already in memory as a source (spec 4.1): what the <see cref="ReadOnlyMemory{T}"/>
/// overloads of the session and the writer wrap, and what tests use.
/// </summary>
public sealed class MemoryPublicationSource(ReadOnlyMemory<byte> bytes) : IPublicationSource, IDisposable
{
    private readonly ReadOnlyMemory<byte> _bytes = bytes;

    public long Length => _bytes.Length;

    public int ReadChunk(long index, Span<byte> destination)
    {
        (long offset, int length) = ChunkReads.Locate(Length, index);
        ChunkReads.RequireRoom(length, destination.Length);
        _bytes.Span.Slice((int)offset, length).CopyTo(destination);
        return length;
    }

    public ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadChunk(index, destination.Span));
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
