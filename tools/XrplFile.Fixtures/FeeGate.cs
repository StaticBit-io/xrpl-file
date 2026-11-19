using System.Globalization;

namespace XrplFile;

/// <summary>
/// Waits out a short rise of the open-ledger fee. A load spike on mainnet lasts seconds, but the Core stops a run at
/// the first read above <see cref="BatchFees.MaxOpenLedgerFeeDrops"/>, which would send the owner back to retype both
/// seeds and the phrase; waiting here keeps the process, and the typed secrets, alive.
/// </summary>
internal sealed class FeeGate
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);

    private readonly Func<CancellationToken, Task<OpenLedgerFee>> _readFee;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string> _log;

    /// <param name="readFee">The ledger source's open-ledger fee read.</param>
    /// <param name="delay">Injected so tests do not sleep.</param>
    /// <param name="now">Injected so a fake delay can advance the clock the limit is measured on.</param>
    /// <param name="log">Where the progress lines go; the console by default.</param>
    public FeeGate(
        Func<CancellationToken, Task<OpenLedgerFee>> readFee,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? now = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(readFee);

        _readFee = readFee;
        _delay = delay ?? Task.Delay;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? Console.WriteLine;
    }

    /// <summary>
    /// Returns once the open-ledger fee is at or below the limit a run starts at and, when <paramref name="unitCeilingDrops"/>
    /// is given, the fee unit derived from it is within that approved ceiling: a session is refused against its approval
    /// even when the fee is back under the start limit, so releasing earlier would only trade one stop for another.
    /// </summary>
    /// <exception cref="XrplFileException"><see cref="XrplFileError.FeeTooHigh"/>: the fee stayed above the limit for <see cref="MaxWait"/>.</exception>
    public async Task WaitForFeeAsync(string key, CancellationToken cancellationToken, long? unitCeilingDrops = null)
    {
        long limit = BatchFees.MaxOpenLedgerFeeDrops;
        string target = unitCeilingDrops is { } ceiling
            ? $"above {limit} or a fee unit above the approved {ceiling}"
            : $"above {limit}";
        OpenLedgerFee fee = await _readFee(cancellationToken).ConfigureAwait(false);

        if (IsWithin(fee, limit, unitCeilingDrops)) return;

        DateTimeOffset started = _now();
        DateTimeOffset lastReport = started;
        _log($"  {key}: the open-ledger fee is {Drops(fee.OpenLedgerFeeDrops)} drops, {target}; waiting for it to fall");

        while (true)
        {
            if (_now() - started >= MaxWait)
            {
                throw new XrplFileException(XrplFileError.FeeTooHigh,
                    $"the open-ledger fee is {fee.OpenLedgerFeeDrops} drops ({target}) and did not fall within "
                    + $"{(int)MaxWait.TotalMinutes} minutes; run the same command again to resume");
            }

            await _delay(PollInterval, cancellationToken).ConfigureAwait(false);
            fee = await _readFee(cancellationToken).ConfigureAwait(false);

            if (IsWithin(fee, limit, unitCeilingDrops))
            {
                _log($"  {key}: the open-ledger fee is {Drops(fee.OpenLedgerFeeDrops)} drops; resuming after {(_now() - started).TotalSeconds:0} s");
                return;
            }

            if (_now() - lastReport >= ReportInterval)
            {
                lastReport = _now();
                _log($"  {key}: the open-ledger fee is still {Drops(fee.OpenLedgerFeeDrops)} drops ({target}); waited {(lastReport - started).TotalSeconds:0} s");
            }
        }
    }

    private static bool IsWithin(OpenLedgerFee fee, long limit, long? unitCeilingDrops) =>
        fee.OpenLedgerFeeDrops <= limit && (unitCeilingDrops is not { } ceiling || BatchFees.Unit(fee) <= ceiling);

    private static string Drops(long drops) => drops.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Runs one publication and, whenever it stops on a fee spike, waits for the fee and runs it again. The number of
/// spikes is not limited: each wait is bounded by <see cref="FeeGate.MaxWait"/>, and that is what ends a run.
/// </summary>
internal static class FeeRetry
{
    /// <param name="publish">One attempt; it resumes whatever state the previous attempt left, so repeating it is safe.</param>
    /// <param name="waitForFee">Returns when the fee is back under the limit, or throws.</param>
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> publish, Func<CancellationToken, Task> waitForFee, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(waitForFee);

        while (true)
        {
            try
            {
                return await publish(cancellationToken).ConfigureAwait(false);
            }
            catch (XrplFileException e) when (e.Error == XrplFileError.FeeTooHigh)
            {
                await waitForFee(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
