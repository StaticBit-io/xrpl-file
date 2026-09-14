using Xunit;

namespace XrplFile.Keys.Tests;

public class DurableFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-vault-").FullName;

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task What_was_written_is_what_is_read_back()
    {
        await DurableFile.WriteAsync(Path("v"), "first"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal("first"u8.ToArray(), await DurableFile.ReadAsync(Path("v"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missing_file_reads_as_nothing_rather_than_throwing()
    {
        Assert.Null(await DurableFile.ReadAsync(Path("absent"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_previous_contents_are_kept_beside_the_new_ones()
    {
        await DurableFile.WriteAsync(Path("v"), "first"u8.ToArray(), TestContext.Current.CancellationToken);
        await DurableFile.WriteAsync(Path("v"), "second"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal("second"u8.ToArray(), await File.ReadAllBytesAsync(Path("v"), TestContext.Current.CancellationToken));
        Assert.Equal("first"u8.ToArray(), await File.ReadAllBytesAsync(Path("v.bak"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_write_interrupted_before_the_replace_leaves_the_old_file_whole()
    {
        // The temporary file is what an interrupted write leaves behind. Simulating the crash
        // means creating that leftover by hand and then reading: the reader must ignore it.
        await DurableFile.WriteAsync(Path("v"), "first"u8.ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path("v.tmp"), "half-writ"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal("first"u8.ToArray(), await DurableFile.ReadAsync(Path("v"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_destroyed_file_falls_back_to_the_kept_copy()
    {
        await DurableFile.WriteAsync(Path("v"), "first"u8.ToArray(), TestContext.Current.CancellationToken);
        await DurableFile.WriteAsync(Path("v"), "second"u8.ToArray(), TestContext.Current.CancellationToken);
        File.Delete(Path("v"));

        Assert.Equal("first"u8.ToArray(), await DurableFile.ReadAsync(Path("v"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_file_that_vanishes_while_it_is_being_read_falls_back_to_the_kept_copy()
    {
        // The window between asking whether the file is there and reading it. Asking first and
        // trusting the answer turned a file that went in that instant into StorageFailed — a hard
        // failure, reported while a whole good copy sat beside it untouched.
        //
        // No test can open that window on a real filesystem without racing it, so the read itself
        // is the seam: it reports the vault file as gone, exactly as the operating system would,
        // and reads everything else for real.
        CancellationToken token = TestContext.Current.CancellationToken;
        await DurableFile.WriteAsync(Path("v"), "first"u8.ToArray(), token);
        await DurableFile.WriteAsync(Path("v"), "second"u8.ToArray(), token);

        Task<byte[]> Vanished(string path, CancellationToken cancellationToken) =>
            string.Equals(path, Path("v"), StringComparison.Ordinal)
                ? throw new FileNotFoundException("It was there a moment ago.", path)
                : File.ReadAllBytesAsync(path, cancellationToken);

        Assert.Equal("first"u8.ToArray(), await DurableFile.ReadAsync(Path("v"), Vanished, token));
    }

    [Fact]
    public async Task A_pair_that_both_vanish_reads_as_nothing_rather_than_throwing()
    {
        // The same window over both files. Neither being there is a missing vault, which is not a
        // failure — and must not become one just because the absence was discovered by reading
        // rather than by asking.
        CancellationToken token = TestContext.Current.CancellationToken;
        await DurableFile.WriteAsync(Path("v"), "first"u8.ToArray(), token);
        await DurableFile.WriteAsync(Path("v"), "second"u8.ToArray(), token);

        static Task<byte[]> Vanished(string path, CancellationToken cancellationToken) =>
            throw new FileNotFoundException("It was there a moment ago.", path);

        Assert.Null(await DurableFile.ReadAsync(Path("v"), Vanished, token));
    }

    [Fact]
    public async Task A_read_that_genuinely_fails_is_still_a_storage_failure()
    {
        // The other side of the fallback: only absence may be answered by moving on. A disk that
        // refuses must not be read as "there is no vault here", which is what a caller would act
        // on by offering to create one over the top of it.
        CancellationToken token = TestContext.Current.CancellationToken;
        await DurableFile.WriteAsync(Path("v"), "first"u8.ToArray(), token);

        static Task<byte[]> Refused(string path, CancellationToken cancellationToken) =>
            throw new IOException("The device is not ready.");

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => DurableFile.ReadAsync(Path("v"), Refused, token));

        Assert.Equal(VaultError.StorageFailed, error.Error);
    }

    [Fact]
    public async Task A_leftover_temporary_file_is_replaced_rather_than_left_behind()
    {
        await DurableFile.WriteAsync(Path("v"), "first"u8.ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path("v.tmp"), "half-writ"u8.ToArray(), TestContext.Current.CancellationToken);

        await DurableFile.WriteAsync(Path("v"), "second"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path("v.tmp")));
        Assert.Equal("second"u8.ToArray(), await DurableFile.ReadAsync(Path("v"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Writing_for_the_first_time_leaves_no_backup()
    {
        await DurableFile.WriteAsync(Path("v"), "first"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path("v.bak")));
    }
}
