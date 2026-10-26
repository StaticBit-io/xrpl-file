using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace XrplFile.Tests;

public sealed class FilePublicationSourceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-source-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Written(long size, string seed = "source")
    {
        string path = Path.Combine(_directory, $"{seed}-{size}.bin");
        File.WriteAllBytes(path, VectorStream.Bytes(seed, size));
        return path;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(1_000)]
    [InlineData(1_001)]
    [InlineData(2_500)]
    public async Task Every_chunk_reads_back_as_the_file_has_it_sync_and_async(long size)
    {
        byte[] file = VectorStream.Bytes("source", size);
        await using FilePublicationSource source = FilePublicationSource.Open(Written(size));
        byte[] buffer = new byte[MemoLayout.ChunkBytes];
        long chunks = FileTree.ChunkCount(size, MemoLayout.ChunkBytes);

        Assert.Equal(size, source.Length);

        for (long index = 0; index < chunks; index++)
        {
            int expected = (int)Math.Min(MemoLayout.ChunkBytes, size - index * MemoLayout.ChunkBytes);

            Assert.Equal(expected, source.ReadChunk(index, buffer));
            Assert.Equal(file.AsSpan((int)(index * MemoLayout.ChunkBytes), expected).ToArray(), buffer.AsSpan(0, expected).ToArray());

            Assert.Equal(expected, await source.ReadChunkAsync(index, buffer, Token));
            Assert.Equal(file.AsSpan((int)(index * MemoLayout.ChunkBytes), expected).ToArray(), buffer.AsSpan(0, expected).ToArray());
        }
    }

    [Fact]
    public void The_short_final_chunk_leaves_the_rest_of_a_full_buffer_untouched()
    {
        using FilePublicationSource source = FilePublicationSource.Open(Written(2_500));
        byte[] buffer = new byte[MemoLayout.ChunkBytes];
        Array.Fill(buffer, (byte)0xEE);

        Assert.Equal(500, source.ReadChunk(2, buffer));
        Assert.All(buffer.AsSpan(500).ToArray(), value => Assert.Equal(0xEE, value));
    }

    [Fact]
    public void A_destination_shorter_than_the_chunk_is_refused()
    {
        using FilePublicationSource source = FilePublicationSource.Open(Written(2_500));

        Assert.Throws<ArgumentException>(() => source.ReadChunk(0, new byte[999]));
        Assert.Equal(500, source.ReadChunk(2, new byte[500]));
    }

    [Fact]
    public void A_chunk_past_the_end_is_refused()
    {
        using FilePublicationSource source = FilePublicationSource.Open(Written(2_500));

        Assert.Throws<ArgumentOutOfRangeException>(() => source.ReadChunk(3, new byte[MemoLayout.ChunkBytes]));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ReadChunk(-1, new byte[MemoLayout.ChunkBytes]));
    }

    [Fact]
    public async Task Partial_reads_are_retried_until_the_chunk_is_full()
    {
        byte[] file = VectorStream.Bytes("partial", 2_500);
        await using FilePublicationSource source = new(new TrickleReader(file, most: 7), file.Length);
        byte[] buffer = new byte[MemoLayout.ChunkBytes];

        Assert.Equal(1_000, source.ReadChunk(1, buffer));
        Assert.Equal(file.AsSpan(1_000, 1_000).ToArray(), buffer);

        Assert.Equal(500, await source.ReadChunkAsync(2, buffer, Token));
        Assert.Equal(file.AsSpan(2_000, 500).ToArray(), buffer.AsSpan(0, 500).ToArray());
    }

    [Fact]
    public async Task A_file_cut_short_under_the_source_throws_IOException_instead_of_inventing_bytes()
    {
        string path = Written(2_500);
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using FilePublicationSource source = new(handle);

        using (FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            writer.SetLength(1_200);
        }

        Assert.Equal(1_000, source.ReadChunk(0, new byte[MemoLayout.ChunkBytes]));
        Assert.Throws<IOException>(() => source.ReadChunk(1, new byte[MemoLayout.ChunkBytes]));
        await Assert.ThrowsAsync<IOException>(async () => await source.ReadChunkAsync(2, new byte[MemoLayout.ChunkBytes], Token));
    }

    [Fact]
    public async Task While_held_nobody_else_can_write_the_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the sharing tests are skipped off Windows: FileShare is advisory on Unix");

        string path = Written(2_500);
        await using FilePublicationSource source = FilePublicationSource.Open(path);

        Assert.ThrowsAny<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
    }

    [Fact]
    public async Task While_held_the_file_cannot_be_deleted_or_renamed_on_Windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "delete and rename sharing is what Windows' sharing modes give (spec S2)");

        string path = Written(2_500);
        await using FilePublicationSource source = FilePublicationSource.Open(path);

        Assert.ThrowsAny<IOException>(() => File.Delete(path));
        Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved"));
    }

    [Fact]
    public async Task After_disposal_the_file_is_free_again()
    {
        string path = Written(2_500);
        FilePublicationSource source = FilePublicationSource.Open(path);
        await source.DisposeAsync();

        File.Delete(path);
        Assert.False(File.Exists(path));
        Assert.Throws<ObjectDisposedException>(() => source.ReadChunk(0, new byte[MemoLayout.ChunkBytes]));
    }

    [Fact]
    public void A_handle_that_cannot_seek_is_refused()
    {
        using AnonymousPipeServerStream pipe = new(PipeDirection.Out);
        using SafeFileHandle handle = new(pipe.SafePipeHandle.DangerousGetHandle(), ownsHandle: false);

        Assert.Throws<ArgumentException>(() => new FilePublicationSource(handle));
    }

    [Fact]
    public async Task A_memory_source_reads_the_same_chunks()
    {
        byte[] file = VectorStream.Bytes("memory", 2_500);
        await using MemoryPublicationSource source = new(file);
        byte[] buffer = new byte[MemoLayout.ChunkBytes];

        Assert.Equal(2_500, source.Length);
        Assert.Equal(500, await source.ReadChunkAsync(2, buffer, Token));
        Assert.Equal(file.AsSpan(2_000).ToArray(), buffer.AsSpan(0, 500).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ReadChunk(3, buffer));
    }

    /// <summary>A file that never hands out more than <paramref name="most"/> bytes per read, as a slow network share might.</summary>
    private sealed class TrickleReader(byte[] file, int most) : IPositionalReader
    {
        public int Read(Span<byte> buffer, long offset)
        {
            int take = (int)Math.Min(Math.Min(most, buffer.Length), Math.Max(0, file.Length - offset));
            file.AsSpan((int)offset, take).CopyTo(buffer);
            return take;
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Read(buffer.Span, offset));
    }
}
