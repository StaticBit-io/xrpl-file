namespace XrplFile;

/// <summary>Reads of the Batch path: one that fails is taken again (spec B6), until the node has been unreachable for <see cref="BatchStrategyOptions.NodeOutageLimit"/>.</summary>
internal static class BatchReads
{
    public static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> read, BatchStrategyOptions options, CancellationToken cancellationToken)
    {
        DateTimeOffset? firstFailure = null;

        while (true)
        {
            try
            {
                return await read(cancellationToken).ConfigureAwait(false);
            }
            catch (XrplFileException e) when (e.Error == XrplFileError.NodeUnreachable)
            {
                DateTimeOffset now = options.Time.GetUtcNow();
                firstFailure ??= now;

                if (now - firstFailure.Value >= options.NodeOutageLimit) throw;

                await Task.Delay(options.ChainCheck, options.Time, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
