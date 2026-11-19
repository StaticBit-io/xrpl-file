using System.Text.Json;
using Xrpl.Client.Exceptions;
using Xrpl.Models.Methods;
using Xrpl.Models.Subscriptions;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// A public node that meters by units per 10 seconds answers slowDown or tooBusy once a resume's
/// reconciliation reads have spent the quota; the read has to wait inside itself rather than fail
/// and be restarted into the same burst.
/// </summary>
public class XrplClientLedgerSourceRateLimitTests
{
    private const string AmendmentsEntry = """{"node":{"Amendments":[]}}""";

    private static RippledException Thrown(string code, string message) =>
        new($"{code} - {message}", new ErrorResponse { Error = code, ErrorMessage = message });

    [Fact]
    public async Task A_slow_down_with_a_hint_waits_the_hinted_time_and_asks_again()
    {
        ClockThatRecordsWaits clock = new();
        Script node = new();
        node.Throw(Thrown("slowDown", "rate limit: units quota (2000 per 10s) exhausted, retry in ~1500ms"));
        node.Answer(AmendmentsEntry);

        bool enabled = await new XrplClientLedgerSource(node.HandleAsync, time: clock)
            .IsAmendmentEnabledAsync(Amendments.Batch, TestContext.Current.CancellationToken);

        Assert.False(enabled);
        Assert.Equal([TimeSpan.FromMilliseconds(1500)], clock.Waits);
        Assert.Equal(2, node.Calls);
    }

    [Fact]
    public async Task A_slow_down_embedded_in_an_answer_is_waited_out_the_same_way()
    {
        ClockThatRecordsWaits clock = new();
        Script node = new();
        node.Answer("""{"error":"slowDown","error_message":"rate limit: retry in ~19353ms"}""");
        node.Answer(AmendmentsEntry);

        await new XrplClientLedgerSource(node.HandleAsync, time: clock)
            .IsAmendmentEnabledAsync(Amendments.Batch, TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromMilliseconds(19353)], clock.Waits);
    }

    [Fact]
    public async Task A_hint_is_clamped_between_one_and_sixty_seconds()
    {
        ClockThatRecordsWaits clock = new();
        Script node = new();
        node.Throw(Thrown("slowDown", "retry in ~5ms"));
        node.Throw(Thrown("slowDown", "retry in ~900000ms"));
        node.Answer(AmendmentsEntry);

        await new XrplClientLedgerSource(node.HandleAsync, time: clock)
            .IsAmendmentEnabledAsync(Amendments.Batch, TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)], clock.Waits);
    }

    [Fact]
    public async Task Too_busy_without_a_hint_backs_off_from_two_seconds_and_doubles()
    {
        ClockThatRecordsWaits clock = new();
        Script node = new();
        node.Throw(Thrown("tooBusy", "The server is too busy to help you now."));
        node.Throw(Thrown("tooBusy", "The server is too busy to help you now."));
        node.Answer(AmendmentsEntry);

        await new XrplClientLedgerSource(node.HandleAsync, time: clock)
            .IsAmendmentEnabledAsync(Amendments.Batch, TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], clock.Waits);
    }

    [Fact]
    public async Task The_backoff_is_capped_at_thirty_seconds()
    {
        ClockThatRecordsWaits clock = new();
        Script node = new();
        for (int i = 0; i < 6; i++) node.Throw(Thrown("tooBusy", "busy"));
        node.Answer(AmendmentsEntry);

        await new XrplClientLedgerSource(node.HandleAsync, time: clock)
            .IsAmendmentEnabledAsync(Amendments.Batch, TestContext.Current.CancellationToken);

        Assert.Equal(
            [2, 4, 8, 16, 30, 30],
            clock.Waits.Select(wait => (int)wait.TotalSeconds).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Waiting_longer_than_two_minutes_for_one_request_is_node_unreachable(bool thrown)
    {
        ClockThatRecordsWaits clock = new();
        Script node = new();
        node.Always(thrown
            ? () => throw Thrown("slowDown", "rate limit: retry in ~30000ms")
            : () => JsonDocument.Parse("""{"error":"slowDown","error_message":"rate limit: retry in ~30000ms"}""").RootElement);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplClientLedgerSource(node.HandleAsync, time: clock)
                .IsAmendmentEnabledAsync(Amendments.Batch, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Contains("slowDown", error.Message, StringComparison.Ordinal);
        Assert.Contains("ledger_entry", error.Message, StringComparison.Ordinal);
        Assert.Equal(4, clock.Waits.Count);
        Assert.Equal(TimeSpan.FromMinutes(2), TimeSpan.FromTicks(clock.Waits.Sum(wait => wait.Ticks)));
    }

    [Fact]
    public async Task An_account_page_read_waits_out_a_rate_limit_too()
    {
        ClockThatRecordsWaits clock = new();
        Script node = new();
        node.Throw(Thrown("slowDown", "retry in ~2000ms"));
        node.Answer("""{"transactions":[]}""");

        List<LedgerTransaction> read = [];
        await foreach (LedgerTransaction tx in new XrplClientLedgerSource(node.HandleAsync, time: clock)
            .AccountTransactionsAsync("rWriter", 1, 2, true, TestContext.Current.CancellationToken))
        {
            read.Add(tx);
        }

        Assert.Empty(read);
        Assert.Equal([TimeSpan.FromSeconds(2)], clock.Waits);
    }

    [Fact]
    public async Task Another_error_is_not_waited_for()
    {
        ClockThatRecordsWaits clock = new();
        Script node = new();
        node.Throw(Thrown("noNetwork", "not connected"));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => new XrplClientLedgerSource(node.HandleAsync, time: clock)
                .IsAmendmentEnabledAsync(Amendments.Batch, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Empty(clock.Waits);
        Assert.Equal(1, node.Calls);
    }

    [Fact]
    public async Task An_absent_answer_is_still_an_answer_and_is_not_waited_for()
    {
        ClockThatRecordsWaits clock = new();
        Script node = new();
        node.Throw(Thrown("actNotFound", "Account not found."));

        AccountState state = await new XrplClientLedgerSource(node.HandleAsync, time: clock)
            .AccountStateAsync("rNobody", TestContext.Current.CancellationToken);

        Assert.Equal(AccountState.Absent, state);
        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task Cancelling_during_the_wait_stops_the_read()
    {
        using CancellationTokenSource cancelled = new();
        ClockThatRecordsWaits clock = new() { OnWait = cancelled.Cancel };
        Script node = new();
        node.Always(() => throw Thrown("slowDown", "retry in ~1000ms"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new XrplClientLedgerSource(node.HandleAsync, time: clock)
                .IsAmendmentEnabledAsync(Amendments.Batch, cancelled.Token));
    }

    /// <summary>A clock whose timers fire at once and remember how long each was asked to wait.</summary>
    private sealed class ClockThatRecordsWaits : TimeProvider
    {
        public List<TimeSpan> Waits { get; } = [];

        public Action? OnWait { get; init; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Waits.Add(dueTime);
            OnWait?.Invoke();
            Task.Run(() => callback(state));
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Script
    {
        private readonly Queue<Func<JsonElement>> _answers = new();
        private Func<JsonElement>? _always;

        public int Calls { get; private set; }

        public void Answer(string json) => _answers.Enqueue(() => JsonDocument.Parse(json).RootElement);

        public void Throw(Exception exception) => _answers.Enqueue(() => throw exception);

        public void Always(Func<JsonElement> answer) => _always = answer;

        public Task<JsonElement> HandleAsync(BaseRequest request, CancellationToken cancellationToken)
        {
            Calls++;

            if (_answers.Count > 0) return Task.FromResult(_answers.Dequeue()());
            if (_always is not null) return Task.FromResult(_always());

            throw new InvalidOperationException("the script ran out of answers");
        }
    }
}
