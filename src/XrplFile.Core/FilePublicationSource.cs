using Microsoft.Win32.SafeHandles;

namespace XrplFile;

/// <summary>Positional reads, so a test can stand in for a file that answers in pieces.</summary>
internal interface IPositionalReader
{
    int Read(Span<byte> buffer, long offset);

    ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken);
}

/// <summary>
/// A file held open for the whole publication through one <see cref="SafeFileHandle"/> (spec S2,
/// S24). The handle shares reading only: on local Windows file systems nobody else can write,
/// delete or replace the file by rename while it is held, so the bytes that were estimated are the
/// bytes that are published. Positional reads on one handle are thread-safe, so the strategy and
/// the writer can read it at once. Other file systems give what their driver gives.
/// </summary>
public sealed class FilePublicationSource : IPublicationSource, IDisposable
{
    private readonly SafeFileHandle? _handle;
    private readonly IPositionalReader _reader;
    private int _disposed;

    /// <summary>Takes ownership of <paramref name="handle"/>: disposing the source closes it.</summary>
    /// <exception cref="ArgumentException">The handle is a pipe or a socket, which cannot be read at an offset.</exception>
    public FilePublicationSource(SafeFileHandle handle)
        : this(handle, path: null)
    {
    }

    internal FilePublicationSource(IPositionalReader reader, long length)
    {
        _reader = reader;
        Length = length;
    }

    private FilePublicationSource(SafeFileHandle handle, string? path)
    {
        ArgumentNullException.ThrowIfNull(handle);

        long length;

        try
        {
            length = RandomAccess.GetLength(handle);
        }
        catch (NotSupportedException e)
        {
            throw new ArgumentException("a publication is read at offsets, and this handle is a pipe or a socket", nameof(handle), e);
        }

        _handle = handle;
        _reader = new HandleReader(handle);
        Length = length;
        Path = path;
    }

    public long Length { get; }

    /// <summary>The full path it was opened from, or null when it was built from a handle.</summary>
    public string? Path { get; }

    /// <summary>Opens the file for reading and denies other writers, deletes and renames while it is held.</summary>
    public static FilePublicationSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string full = System.IO.Path.GetFullPath(path);
        SafeFileHandle handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);

        try
        {
            return new FilePublicationSource(handle, full);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public int ReadChunk(long index, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        (long offset, int length) = ChunkReads.Locate(Length, index);
        ChunkReads.RequireRoom(length, destination.Length);

        for (int filled = 0; filled < length;)
        {
            int read = _reader.Read(destination.Slice(filled, length - filled), offset + filled);
            if (read == 0) throw ChunkReads.EndedEarly(index, offset + filled, Length);
            filled += read;
        }

        return length;
    }

    public async ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        (long offset, int length) = ChunkReads.Locate(Length, index);
        ChunkReads.RequireRoom(length, destination.Length);

        for (int filled = 0; filled < length;)
        {
            int read = await _reader.ReadAsync(destination.Slice(filled, length - filled), offset + filled, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw ChunkReads.EndedEarly(index, offset + filled, Length);
            filled += read;
        }

        return length;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _handle?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class HandleReader(SafeFileHandle handle) : IPositionalReader
    {
        public int Read(Span<byte> buffer, long offset) => RandomAccess.Read(handle, buffer, offset);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken) =>
            RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken);
    }
}
