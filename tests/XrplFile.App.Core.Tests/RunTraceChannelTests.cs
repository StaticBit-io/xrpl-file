using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class RunTraceChannelTests
{
    [Fact]
    public async Task A_slow_writer_drops_the_oldest_entries_counts_them_and_never_queues_more_than_the_channel_holds()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> written = [];
        RunTraceChannel channel = new(async lines =>
        {
            await release.Task;
            lock (written) written.AddRange(lines);
        });

        for (int i = 0; i < 25_000; i++)
        {
            channel.Post($"line {i}");
            Assert.True(channel.Pending <= RunTraceChannel.Capacity);
        }

        release.SetResult();
        await channel.CompleteAsync();

        List<string> lines = [.. written.Where(line => line.StartsWith("line ", StringComparison.Ordinal))];
        Assert.Equal(25_000, lines.Count + channel.Dropped);
        Assert.True(channel.Dropped >= 25_000 - RunTraceChannel.Capacity - 512, $"dropped {channel.Dropped}");
        Assert.Equal("line 24999", lines[^1]);
        Assert.Contains(written, line => line == $"dropped {channel.Dropped} trace entries: the log could not keep up");
    }

    [Fact]
    public async Task A_fast_writer_loses_nothing_and_keeps_the_order()
    {
        List<string> written = [];
        RunTraceChannel channel = new(lines =>
        {
            lock (written) written.AddRange(lines);
            return Task.CompletedTask;
        });

        for (int i = 0; i < 2_000; i++) channel.Post($"line {i}");
        await channel.CompleteAsync();

        Assert.Equal(0, channel.Dropped);
        Assert.Equal([.. Enumerable.Range(0, 2_000).Select(i => $"line {i}")], written);
    }

    [Fact]
    public async Task A_writer_that_throws_does_not_stop_the_drain()
    {
        int calls = 0;
        RunTraceChannel channel = new(_ => Interlocked.Increment(ref calls) == 1 ? throw new IOException("disk") : Task.CompletedTask);

        channel.Post("one");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        channel.Post("two");
        await channel.CompleteAsync();

        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task Completing_stops_waiting_for_a_write_that_never_returns_after_its_limit()
    {
        ManualTimeProvider time = new();
        RunTraceChannel channel = new(_ => new TaskCompletionSource().Task, time);

        channel.Post("one");
        Task done = channel.CompleteAsync();

        Assert.False(done.IsCompleted);

        time.Advance(RunTraceChannel.CompletionLimit);

        await done.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
