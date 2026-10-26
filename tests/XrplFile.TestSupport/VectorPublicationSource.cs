using System.Security.Cryptography;
using System.Text;

namespace XrplFile.Tests;

/// <summary>
/// <see cref="VectorStream.Bytes"/> of any length as a source, computed for each read and never
/// held: what a test of hundreds of megabytes publishes without allocating them.
/// </summary>
public sealed class VectorPublicationSource(string seed, long length) : IPublicationSource
{
    private const int BlockBytes = 32;
    private readonly byte[] _seed = Encoding.UTF8.GetBytes(seed);
    private int _reads;

    public long Length { get; } = length;

    public int Reads => Volatile.Read(ref _reads);

    public int ReadChunk(long index, Span<byte> destination)
    {
        long chunks = FileTree.ChunkCount(Length, MemoLayout.ChunkBytes);
        if (index < 0 || index >= chunks) throw new ArgumentOutOfRangeException(nameof(index), index, $"the file has {chunks} chunks");

        long offset = index * MemoLayout.ChunkBytes;
        int count = (int)Math.Min(MemoLayout.ChunkBytes, Length - offset);
        if (destination.Length < count) throw new ArgumentException("the destination is shorter than the chunk", nameof(destination));

        Span<byte> input = stackalloc byte[_seed.Length + 4];
        _seed.CopyTo(input);
        Span<byte> block = stackalloc byte[BlockBytes];

        for (int filled = 0; filled < count;)
        {
            long at = offset + filled;
            uint counter = (uint)(at / BlockBytes);
            int within = (int)(at % BlockBytes);

            input[^4] = (byte)(counter >> 24);
            input[^3] = (byte)(counter >> 16);
            input[^2] = (byte)(counter >> 8);
            input[^1] = (byte)counter;
            SHA256.HashData(input, block);

            int take = Math.Min(BlockBytes - within, count - filled);
            block.Slice(within, take).CopyTo(destination[filled..]);
            filled += take;
        }

        Interlocked.Increment(ref _reads);
        return count;
    }

    public ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadChunk(index, destination.Span));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
