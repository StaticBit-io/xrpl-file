namespace XrplFile.App.Core.Tests;

/// <summary>A held file that fails the second time one chunk is read: hashing reads it once, sending reads it again.</summary>
public sealed class FailingSecondReadSource(IPublicationSource inner, long failingChunk) : IPublicationSource
{
    private int _reads;

    public long Length => inner.Length;

    public int ReadChunk(long index, Span<byte> destination)
    {
        Check(index);
        return inner.ReadChunk(index, destination);
    }

    public ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken)
    {
        Check(index);
        return inner.ReadChunkAsync(index, destination, cancellationToken);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private void Check(long index)
    {
        if (index == failingChunk && Interlocked.Increment(ref _reads) >= 2) throw new IOException("fake: the drive the file was on went away");
    }
}
