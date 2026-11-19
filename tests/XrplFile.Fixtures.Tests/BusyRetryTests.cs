using Xunit;

namespace XrplFile.Fixtures.Tests;

public sealed class BusyRetryTests
{
    private const string NotLanded =
        "the memo marked 78662F632F323936 did not land in the 3 candidates a session allows it";

    private static XrplFileException Refused(string message, string? engineResult = null) =>
        new(XrplFileError.SubmitRefused, message, engineResult: engineResult);

    private sealed class Harness
    {
        public List<TimeSpan> Delays { get; } = [];

        public List<string> Log { get; } = [];

        public int Gates { get; private set; }

        public int Attempts { get; private set; }

        public Task<string> RunAsync(Func<int, string> attempt) =>
            BusyRetry.RunAsync(
                "video",
                _ =>
                {
                    Attempts++;
                    return Task.FromResult(attempt(Attempts));
                },
                _ =>
                {
                    Gates++;
                    return Task.CompletedTask;
                },
                (span, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Delays.Add(span);
                    return Task.CompletedTask;
                },
                Log.Add,
                CancellationToken.None);
    }

    [Fact]
    public void RecognisesTheCoreMessageWithoutAnEngineResult() =>
        Assert.True(BusyRetry.IsNotLanded(Refused(NotLanded)));

    [Fact]
    public void RejectsARefusalThatCarriesAnEngineResult() =>
        Assert.False(BusyRetry.IsNotLanded(Refused(NotLanded, "temMALFORMED")));

    [Fact]
    public void RejectsADuplicateMarkerRefusal() =>
        Assert.False(BusyRetry.IsNotLanded(Refused("two memos carry the same marker 78662F632F323936")));

    [Theory]
    [InlineData(XrplFileError.FeeTooHigh)]
    [InlineData(XrplFileError.ResumeMismatch)]
    public void RejectsOtherErrorCodes(XrplFileError error) =>
        Assert.False(BusyRetry.IsNotLanded(new XrplFileException(error, NotLanded)));

    [Fact]
    public async Task PausesPassesTheGateAndPublishesAgainAfterANotLandedBatch()
    {
        Harness h = new();

        string link = await h.RunAsync(n => n == 1 ? throw Refused(NotLanded) : "xrpl-file://mainnet/abc");

        Assert.Equal("xrpl-file://mainnet/abc", link);
        Assert.Equal(2, h.Attempts);
        Assert.Equal(1, h.Gates);
        Assert.Equal([TimeSpan.FromSeconds(60)], h.Delays);
        Assert.Equal(
            ["  video: a batch did not land after 3 tries while the network was busy; pausing 60 s, then resuming this file"],
            h.Log);
    }

    [Fact]
    public async Task RetriesTwentyFiveSuccessiveNotLandedResults()
    {
        Harness h = new();

        string link = await h.RunAsync(n => n <= 25 ? throw Refused(NotLanded) : "done");

        Assert.Equal("done", link);
        Assert.Equal(26, h.Attempts);
        Assert.Equal(25, h.Delays.Count);
        Assert.Equal(25, h.Gates);
    }

    [Fact]
    public async Task PropagatesAMalformedRefusalAtOnce()
    {
        Harness h = new();

        XrplFileException e = await Assert.ThrowsAsync<XrplFileException>(
            () => h.RunAsync(_ => throw Refused("the node refused the batch", "temMALFORMED")));

        Assert.Equal("temMALFORMED", e.EngineResult);
        Assert.Equal(1, h.Attempts);
        Assert.Empty(h.Delays);
    }

    [Fact]
    public async Task FeeTooHighStillWaitsForTheGateWithoutThePause()
    {
        Harness h = new();

        string link = await h.RunAsync(n => n == 1 ? throw new XrplFileException(XrplFileError.FeeTooHigh, "fee") : "ok");

        Assert.Equal("ok", link);
        Assert.Equal(1, h.Gates);
        Assert.Empty(h.Delays);
        Assert.Empty(h.Log);
    }

    [Fact]
    public void CoreStillRaisesTheWordingTheRecogniserMatches()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "XrplFile.Core", "BatchStrategy.cs"));

        Assert.Contains("did not land in the", source, StringComparison.Ordinal);
        Assert.Contains("candidates a session allows it", source, StringComparison.Ordinal);
        Assert.Contains("XrplFileError.SubmitRefused", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XrplFile.sln"))) return directory.FullName;
        }

        throw new InvalidOperationException($"XrplFile.sln was not found above {AppContext.BaseDirectory}");
    }
}
