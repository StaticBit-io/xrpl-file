using System.Threading.Channels;

namespace XrplFile.App.Core;

/// <summary>
/// The way from a run's trace to its log (spec S18): a bounded channel of
/// <see cref="Capacity"/> lines, drained by one background writer with awaited writes. When the log
/// cannot keep up, the oldest pending line is dropped and counted, so a slow disk never queues
/// without bound behind a run; the count is written when the channel completes.
/// </summary>
public sealed class RunTraceChannel
{
    public const int Capacity = 10_000;

    /// <summary>The most lines one write is handed.</summary>
    private const int Batch = 512;

    private readonly Func<IReadOnlyList<string>, Task> _write;
    private readonly Channel<string> _channel;
    private readonly Task _drain;
    private long _dropped;

    public RunTraceChannel(Func<IReadOnlyList<string>, Task> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        _write = write;
        _channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));
        _drain = Task.Run(DrainAsync);
    }

    /// <summary>Lines dropped because the channel was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Lines waiting for the writer; never more than <see cref="Capacity"/>.</summary>
    public int Pending => _channel.Reader.Count;

    /// <summary>Queues a line without waiting; a full channel drops its oldest line instead.</summary>
    public void Post(string line) => _channel.Writer.TryWrite(line);

    /// <summary>Stops taking lines and waits until every queued one, and the count of the dropped, is written.</summary>
    public async Task CompleteAsync()
    {
        _channel.Writer.TryComplete();
        await _drain.ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            List<string> lines = new(Batch);
            while (lines.Count < Batch && _channel.Reader.TryRead(out string? line)) lines.Add(line);

            await WriteQuietlyAsync(lines).ConfigureAwait(false);
        }

        long dropped = Dropped;
        if (dropped > 0) await WriteQuietlyAsync([$"dropped {dropped} trace entries: the log could not keep up"]).ConfigureAwait(false);
    }

    /// <summary>A diagnostic that can fail the run it describes is worse than none.</summary>
    private async Task WriteQuietlyAsync(IReadOnlyList<string> lines)
    {
        try
        {
            await _write(lines).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
