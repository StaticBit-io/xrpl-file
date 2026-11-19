using Xunit;

namespace XrplFile.Fixtures.Tests;

public sealed class FeeGateTests
{
    private sealed class Harness
    {
        private readonly Queue<long> _fees;
        private long _last;

        public Harness(params long[] fees) => _fees = new Queue<long>(fees);

        public DateTimeOffset Now { get; private set; } = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        public List<TimeSpan> Delays { get; } = [];

        public List<string> Log { get; } = [];

        public int Reads { get; private set; }

        public FeeGate Gate => new(
            _ =>
            {
                Reads++;
                if (_fees.Count > 0) _last = _fees.Dequeue();
                return Task.FromResult(new OpenLedgerFee(_last, 10));
            },
            (span, token) =>
            {
                token.ThrowIfCancellationRequested();
                Delays.Add(span);
                Now += span;
                return Task.CompletedTask;
            },
            () => Now,
            Log.Add);
    }

    [Fact]
    public async Task ReturnsAtOnceWhenTheFeeIsWithinTheLimit()
    {
        Harness h = new(200);

        await h.Gate.WaitForFeeAsync("cover", CancellationToken.None);

        Assert.Equal(1, h.Reads);
        Assert.Empty(h.Delays);
        Assert.Empty(h.Log);
    }

    [Fact]
    public async Task WaitsThroughASpikeAndReturnsOnTheThirdRead()
    {
        Harness h = new(9745, 49080, 10);

        await h.Gate.WaitForFeeAsync("cover", CancellationToken.None);

        Assert.Equal(3, h.Reads);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)], h.Delays);
        Assert.Equal(2, h.Log.Count);
        Assert.Equal("  cover: the open-ledger fee is 9745 drops, above 200; waiting for it to fall", h.Log[0]);
        Assert.Contains("resuming", h.Log[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAtLeastOnceAMinuteWhileWaiting()
    {
        // High for 4 minutes (24 polls of 10 s), then it falls.
        Harness h = new(Enumerable.Repeat(5000L, 25).Append(10).ToArray());

        await h.Gate.WaitForFeeAsync("video", CancellationToken.None);

        Assert.Equal(25, h.Delays.Count);
        Assert.Equal(1 + 4 + 1, h.Log.Count);
    }

    [Fact]
    public async Task ThrowsFeeTooHighWhenTheFeeStaysHighForFifteenMinutes()
    {
        Harness h = new(49080);

        XrplFileException e = await Assert.ThrowsAsync<XrplFileException>(() => h.Gate.WaitForFeeAsync("cover", CancellationToken.None));

        Assert.Equal(XrplFileError.FeeTooHigh, e.Error);
        Assert.Contains("15 minutes", e.Message, StringComparison.Ordinal);
        Assert.Contains("run the same command again", e.Message, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(15), h.Delays.Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay));
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        Harness h = new(49080);
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Gate.WaitForFeeAsync("cover", cancel.Token));
    }
}

public sealed class FeeRetryTests
{
    private static XrplFileException FeeTooHigh() => new(XrplFileError.FeeTooHigh, "the open-ledger fee is 9745 drops");

    [Fact]
    public async Task AFeeStopThenSuccessPublishesOnceMoreAndReturnsTheResult()
    {
        int attempts = 0;
        int waits = 0;

        string result = await FeeRetry.RunAsync<string>(
            _ => ++attempts == 1 ? throw FeeTooHigh() : Task.FromResult("link"),
            _ =>
            {
                waits++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("link", result);
        Assert.Equal(2, attempts);
        Assert.Equal(1, waits);
    }

    [Fact]
    public async Task OtherErrorsAreNotRetried()
    {
        int attempts = 0;
        int waits = 0;

        XrplFileException e = await Assert.ThrowsAsync<XrplFileException>(() => FeeRetry.RunAsync<string>(
            _ =>
            {
                attempts++;
                throw new XrplFileException(XrplFileError.BatchNotEnabled, "no");
            },
            _ =>
            {
                waits++;
                return Task.CompletedTask;
            },
            CancellationToken.None));

        Assert.Equal(XrplFileError.BatchNotEnabled, e.Error);
        Assert.Equal(1, attempts);
        Assert.Equal(0, waits);
    }

    [Fact]
    public async Task ManySuccessiveFeeStopsAreAllWaitedOut()
    {
        int attempts = 0;
        int waits = 0;

        string result = await FeeRetry.RunAsync<string>(
            _ => ++attempts <= 25 ? throw FeeTooHigh() : Task.FromResult("link"),
            _ =>
            {
                waits++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("link", result);
        Assert.Equal(26, attempts);
        Assert.Equal(25, waits);
    }
}
