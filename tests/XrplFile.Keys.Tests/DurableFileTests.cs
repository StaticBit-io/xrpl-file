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
