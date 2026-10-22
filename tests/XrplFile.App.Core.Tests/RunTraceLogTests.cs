using Xrpl.Wallet;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class RunTraceLogTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-trace-log-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Past_its_bound_the_log_rolls_and_keeps_only_the_newest_files()
    {
        string path = Path.Combine(_directory, "batch-trace.log");
        RunTraceLog log = new(path, maxBytes: 200, keep: 3);

        for (int run = 0; run < 10; run++)
        {
            await log.AppendAsync([.. Enumerable.Range(0, 5).Select(i => $"run {run} transmit:{i}:tesSUCCESS")], Token);
        }

        string[] files = Directory.GetFiles(_directory);
        Assert.Equal(3, files.Length);
        Assert.Contains("run 9 ", await File.ReadAllTextAsync(path, Token), StringComparison.Ordinal);
        Assert.All(files, file => Assert.DoesNotContain("run 0 ", File.ReadAllText(file), StringComparison.Ordinal));
        Assert.All(files, file => Assert.True(new FileInfo(file).Length <= 200, $"{file} grew past its bound"));
    }

    [Fact]
    public async Task A_line_that_looks_like_a_seed_or_a_key_is_withheld()
    {
        XrplWallet wallet = XrplWallet.Generate();
        string path = Path.Combine(_directory, "batch-trace.log");

        await new RunTraceLog(path).AppendAsync(
            ["transmit:3:tesSUCCESS", $"halt:{wallet.Seed}", $"blob {wallet.PrivateKey}{wallet.PrivateKey}"], Token);

        string text = await File.ReadAllTextAsync(path, Token);
        Assert.Contains("transmit:3:tesSUCCESS", text, StringComparison.Ordinal);
        Assert.DoesNotContain(wallet.Seed, text, StringComparison.Ordinal);
        Assert.DoesNotContain(wallet.PrivateKey, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_log_that_cannot_be_written_does_not_throw()
    {
        string blocker = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x", Token);

        await new RunTraceLog(Path.Combine(blocker, "batch-trace.log")).AppendAsync(["round:5"], Token);
    }
}
