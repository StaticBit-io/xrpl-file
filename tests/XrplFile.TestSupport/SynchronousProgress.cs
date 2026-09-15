namespace XrplFile.Tests;

/// <summary>
/// An IProgress that runs its callback on the thread that reported, not on a later one.
///
/// System.Progress&lt;T&gt; marshals through a synchronization context, or the thread pool when
/// there is none, so a test that asserts straight after an await can read the list before the
/// callbacks have landed. Against a fake that completes synchronously that race is the common
/// case, not the rare one. This double removes the timing from the test without removing what
/// the test is about: how many times progress was reported, and with what.
/// </summary>
public sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
