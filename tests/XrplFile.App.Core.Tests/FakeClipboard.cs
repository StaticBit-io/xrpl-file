namespace XrplFile.App.Core.Tests;

/// <summary>
/// A clipboard that just remembers what was copied and for how long. Originally a type declared
/// alongside <c>HistoryModelTests</c>; promoted to its own file, the way <see cref="NeverVault"/>
/// was, once a second test class (<c>FundingModelTests</c>) needed the exact same double.
/// </summary>
public sealed class FakeClipboard : IClipboardService
{
    public string? Copied { get; private set; }

    public TimeSpan? Expiry { get; private set; }

    public Task CopySecretAsync(string value, TimeSpan expiry, CancellationToken cancellationToken)
    {
        Copied = value;
        Expiry = expiry;
        return Task.CompletedTask;
    }

    public Task CopyAsync(string value, CancellationToken cancellationToken)
    {
        Copied = value;
        Expiry = null;
        return Task.CompletedTask;
    }
}
