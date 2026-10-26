namespace XrplFile;

/// <summary>Where a chunk sits in a file, in checked arithmetic, and the checks every source makes before reading it.</summary>
internal static class ChunkReads
{
    public static long ChunkCount(long length) => FileTree.ChunkCount(length, MemoLayout.ChunkBytes);

    /// <summary>The chunk's first byte and its length; a chunk the file does not have is refused.</summary>
    public static (long Offset, int Length) Locate(long length, long index)
    {
        long count = ChunkCount(length);

        if (index < 0 || index >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"the file has {count} chunks");
        }

        long offset = checked(index * MemoLayout.ChunkBytes);
        return (offset, (int)Math.Min(MemoLayout.ChunkBytes, length - offset));
    }

    public static void RequireRoom(int chunkLength, int destinationLength)
    {
        if (destinationLength < chunkLength)
        {
            throw new ArgumentException($"the destination holds {destinationLength} bytes and the chunk is {chunkLength}", "destination");
        }
    }

    public static IOException EndedEarly(long index, long at, long length) =>
        new($"the file ended at byte {at}, inside chunk {index}; it was {length} bytes when it was opened");
}
