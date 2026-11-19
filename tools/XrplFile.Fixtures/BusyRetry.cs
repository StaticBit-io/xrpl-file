using System.Text.RegularExpressions;

namespace XrplFile;

/// <summary>
/// Resumes a file whose Batch did not land while the network was busy, or whose node stopped answering. The Core stops a session after a memo misses
/// its allowed candidates (spec B13), but those candidates are not on the ledger and were not charged, and running the
/// same command again resumes the entry as a new session with fresh counts. This does that without sending the owner
/// back to retype both seeds and the phrase.
/// </summary>
internal static partial class BusyRetry
{
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(60);

    // The Core exposes no dedicated error code for this case, so the recogniser matches the wording of
    // BatchStrategy.RequireAnotherCandidate; a test pins the Core source against drift.
    private const string CoreWording = "did not land in the";

    [GeneratedRegex(@"did not land in the (\d+) candidates", RegexOptions.CultureInvariant)]
    private static partial Regex TriesPattern();

    /// <summary>
    /// True only for a refusal raised by the Core's candidate limit: <see cref="XrplFileError.SubmitRefused"/> with no
    /// engine result. A refusal carrying an engine result (a malformed transaction) or any other message stops the run.
    /// </summary>
    public static bool IsNotLanded(XrplFileException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.Error == XrplFileError.SubmitRefused
            && exception.EngineResult is null
            && exception.Message.Contains(CoreWording, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs one publication; on a fee spike waits through <paramref name="waitForFee"/>, and on a batch that did not
    /// land pauses, passes the same gate and publishes again. Neither case is capped: Ctrl+C ends a run, and a fee wait
    /// is bounded by <see cref="FeeGate.MaxWait"/>.
    /// </summary>
    /// <param name="publish">One attempt; it resumes whatever state the previous attempt left, so repeating it is safe.</param>
    /// <param name="waitForFee">Returns when the fee is back within what was approved, or throws.</param>
    /// <param name="delay">Injected so tests do not sleep.</param>
    public static async Task<T> RunAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> publish,
        Func<CancellationToken, Task> waitForFee,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(waitForFee);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(log);

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
            catch (XrplFileException e) when (e.Error == XrplFileError.NodeUnreachable)
            {
                // The Core already waited out a rate limit inside the read; what reaches here is a node that stayed
                // silent or overloaded for minutes, or a dropped connection. Publishing again resumes from the ledger.
                log($"  {key}: the node is not answering ({ShortReason(e.Message)}); "
                    + $"pausing {(int)Pause.TotalSeconds} s, then resuming this file");
                await delay(Pause, cancellationToken).ConfigureAwait(false);
                await waitForFee(cancellationToken).ConfigureAwait(false);
            }
            catch (XrplFileException e) when (IsNotLanded(e))
            {
                Match tries = TriesPattern().Match(e.Message);
                string count = tries.Success ? tries.Groups[1].Value : "several";
                log($"  {key}: a batch did not land after {count} tries while the network was busy; "
                    + $"pausing {(int)Pause.TotalSeconds} s, then resuming this file");
                await delay(Pause, cancellationToken).ConfigureAwait(false);
                await waitForFee(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The first line of the Core's message, cut to a length that keeps the log line readable.</summary>
    private static string ShortReason(string message)
    {
        string line = message.Split('\n', 2)[0].Trim();
        const int limit = 120;
        return line.Length <= limit ? line : line[..limit] + "...";
    }
}
