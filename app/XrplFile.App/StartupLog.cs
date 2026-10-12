using Microsoft.Extensions.Logging;

namespace XrplFile.App;

/// <summary>
/// The one thing this application writes down about its own startup, and the only diagnostic it
/// has on a machine that is not a developer's.
///
/// <para>
/// Everything else here reports through <c>Microsoft.Extensions.Logging</c>, which
/// <c>MauiProgram</c> wires to <c>AddDebug()</c> and only under <c>#if DEBUG</c> — a sink that
/// writes to an attached debugger and therefore to nothing at all when somebody simply runs the
/// executable. A window that comes up blank then leaves no trace anywhere: not in the event log,
/// which sees no crash because nothing crashed, and not on standard output, which a Windows GUI
/// process never writes to. This class exists so that failure says something.
/// </para>
///
/// <para>
/// It deliberately avoids <c>FileSystem.AppDataDirectory</c> and every other MAUI API: it has to
/// work before MAUI is initialised, which is exactly when the interesting failures happen. Every
/// method swallows its own errors — a logger that can throw turns a diagnosable fault into a
/// second, undiagnosable one.
/// </para>
/// </summary>
public static class StartupLog
{
    private static readonly object Gate = new();

    private static readonly string Path = BuildPath();

    /// <summary>Where this log is written, so a screen or a report can tell somebody where to look.</summary>
    public static string Location => Path;

    /// <summary>Appends one timestamped line. Never throws.</summary>
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // A diagnostic that can break the thing it is diagnosing is worse than none.
        }
    }

    /// <summary>Appends a failure with its exception, including every inner one. Never throws.</summary>
    public static void Write(string message, Exception error) =>
        Write($"{message}: {error.GetType().FullName}: {error.Message}{Environment.NewLine}{error}");

    /// <summary>
    /// Catches what nothing else does: an exception on a thread with no handler, and a faulted
    /// task nobody awaited. Both are silent by default, and both can leave a window drawn but
    /// empty — which is the failure this whole class was added to explain.
    /// </summary>
    public static void CatchEverythingElse()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error)
            {
                Write("unhandled exception", error);
            }
            else
            {
                Write($"unhandled non-exception throw: {e.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    private static string BuildPath()
    {
        try
        {
            string directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XrplFile");

            Directory.CreateDirectory(directory);
            return System.IO.Path.Combine(directory, "startup.log");
        }
        catch (Exception)
        {
            // Last resort: the temporary directory always exists and is always writable.
            return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xrpl-file-startup.log");
        }
    }
}

/// <summary>
/// Sends what this application logs to <see cref="StartupLog"/>, so that it is written down
/// somewhere rather than nowhere.
///
/// <para>
/// This exists because of a failure seen in a running window: a file dialog threw, Blazor caught
/// it the way it catches every exception from a component's event handler, and the screen showed
/// its generic "An unhandled error has occurred" bar. Nothing reached
/// <see cref="AppDomain.UnhandledException"/> — Blazor had handled it — and nothing reached a
/// file, because the only logging provider this application had was <c>AddDebug()</c> under
/// <c>#if DEBUG</c>. The person is told something broke and neither they nor anybody helping them
/// can find out what.
/// </para>
/// </summary>
public sealed class StartupLogLoggerProvider : ILoggerProvider
{
    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new StartupLogLogger(categoryName);

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private sealed class StartupLogLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>
        /// Warnings and worse only. This file is a last resort somebody reads after something went
        /// wrong, not a trace: filling it with routine chatter would bury the one line that matters.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = $"{logLevel} {category}: {formatter(state, exception)}";

            if (exception is null)
            {
                StartupLog.Write(message);
            }
            else
            {
                StartupLog.Write(message, exception);
            }
        }
    }
}
