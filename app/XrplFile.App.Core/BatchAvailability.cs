namespace XrplFile.App.Core;

/// <summary>
/// Whether a network enables <c>BatchV1_1</c>, as the Publish screen needs it (spec B14): read once
/// per network and kept for five minutes. A failed read is "unknown" and is not kept, so the screen
/// stays usable and asks again next time. The estimate reads again before every run, and that read decides.
/// </summary>
public sealed class BatchAvailability(IConnectionFactory connections, TimeProvider time)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (bool Enabled, DateTimeOffset ReadAt)> _cache = new(StringComparer.Ordinal);

    public async Task<bool?> IsEnabledAsync(string network, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(network);

        lock (_gate)
        {
            if (_cache.TryGetValue(network, out (bool Enabled, DateTimeOffset ReadAt) cached) && time.GetUtcNow() - cached.ReadAt < CacheFor)
            {
                return cached.Enabled;
            }
        }

        try
        {
            await using ILedgerConnection connection = await connections.ConnectAsync(network, cancellationToken).ConfigureAwait(false);
            bool enabled = await connection.Source.IsAmendmentEnabledAsync(Amendments.Batch, cancellationToken).ConfigureAwait(false);

            lock (_gate) _cache[network] = (enabled, time.GetUtcNow());

            return enabled;
        }
        catch (XrplFileException)
        {
            return null;
        }
    }
}
