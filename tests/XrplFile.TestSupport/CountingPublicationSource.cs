namespace XrplFile.Tests;

/// <summary>Counts the chunks read through it and whether it was disposed.</summary>
public sealed class CountingPublicationSource(IPublicationSource inner) : IPublicationSource
{
    private int _reads;
    private int _disposed;

    public long Length => inner.Length;

    public int Reads => Volatile.Read(ref _reads);

    public bool Disposed => Volatile.Read(ref _disposed) != 0;

    public int ReadChunk(long index, Span<byte> destination)
    {
        Interlocked.Increment(ref _reads);
        return inner.ReadChunk(index, destination);
    }

    public ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        return inner.ReadChunkAsync(index, destination, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return inner.DisposeAsync();
    }
}
