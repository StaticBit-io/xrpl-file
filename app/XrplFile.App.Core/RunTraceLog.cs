using System.Text;
using System.Text.RegularExpressions;

namespace XrplFile.App.Core;

/// <summary>
/// The application's record of what each Batch run did (its round trace), so that a run that stalls
/// on somebody's machine can be explained afterwards: the 2026-10-22 testnet stall could only be
/// reconstructed from the ledger, because nothing had kept the submit answers.
/// </summary>
/// <remarks>
/// <para>
/// Bounded: the file rolls over before it would pass <c>maxBytes</c>, and only the newest
/// <c>keep</c> files are kept (<c>batch-trace.log</c>, <c>batch-trace.1.log</c>, ...). A 7 MB run
/// writes about 50 KB.
/// </para>
/// <para>
/// It never holds key material. The trace carries only ordinals, ledger indexes, fee units and
/// engine results; as a second line of defence a line with a seed-shaped token or hex longer than a
/// transaction hash is withheld rather than written.
/// </para>
/// <para>
/// It never throws: a diagnostic that can fail the run it describes is worse than none.
/// </para>
/// </remarks>
public sealed partial class RunTraceLog
{
    /// <summary>The size one file is kept under, in bytes.</summary>
    public const long DefaultMaxBytes = 1024 * 1024;

    /// <summary>How many files are kept, the current one included.</summary>
    public const int DefaultKeep = 3;

    private const string Withheld = "[line withheld: it looked like key material]";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _maxBytes;
    private readonly int _keep;

    /// <param name="path">The current file; rolled-over ones sit beside it.</param>
    /// <param name="maxBytes">The size one file is kept under.</param>
    /// <param name="keep">How many files are kept, the current one included.</param>
    public RunTraceLog(string path, long maxBytes = DefaultMaxBytes, int keep = DefaultKeep)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);

        Path = path;
        _maxBytes = maxBytes;
        _keep = keep;
    }

    /// <summary>Where the current file is.</summary>
    public string Path { get; }

    /// <summary>A family seed (base58, starting with <c>s</c>).</summary>
    [GeneratedRegex(@"(?<![1-9A-HJ-NP-Za-km-z])s[1-9A-HJ-NP-Za-km-z]{24,34}(?![1-9A-HJ-NP-Za-km-z])")]
    private static partial Regex SeedShaped();

    /// <summary>A key, a signature or a signed blob: hex longer than a 64-character hash.</summary>
    [GeneratedRegex("[0-9A-Fa-f]{65,}")]
    private static partial Regex LongHex();

    /// <summary>Appends the lines as one block, rolling the file over first if the block would take it past its bound. Never throws.</summary>
    public async Task AppendAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // Built after the gate is taken, so a writer waiting for it holds no buffer (spec S18).
            StringBuilder block = new();
            foreach (string line in lines) block.Append(IsSafe(line) ? line : Withheld).Append('\n');

            byte[] bytes = Encoding.UTF8.GetBytes(block.ToString());

            // Only a block larger than a whole file is cut: from its start, since its end says how the run ended.
            if (bytes.Length > _maxBytes) bytes = bytes[^(int)_maxBytes..];

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            if (File.Exists(Path) && new FileInfo(Path).Length + bytes.Length > _maxBytes) Roll();

            // Not cancelled with the run: the trace of a cancelled run is worth keeping too.
            await using FileStream stream = new(Path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A diagnostic that can break the thing it is diagnosing is worse than none.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether a line can be written: no seed-shaped token and no hex longer than a hash.</summary>
    public static bool IsSafe(string line) => !SeedShaped().IsMatch(line) && !LongHex().IsMatch(line);

    private void Roll()
    {
        File.Delete(Numbered(_keep - 1));

        for (int index = _keep - 2; index >= 1; index--)
        {
            if (File.Exists(Numbered(index))) File.Move(Numbered(index), Numbered(index + 1));
        }

        if (_keep > 1) File.Move(Path, Numbered(1));
        else File.Delete(Path);
    }

    private string Numbered(int index) =>
        index == 0
            ? Path
            : System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Path)!,
                $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.{index}{System.IO.Path.GetExtension(Path)}");
}
