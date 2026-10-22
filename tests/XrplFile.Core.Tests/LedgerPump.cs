using Xrpl.Client;
using Xrpl.Models.Methods;

namespace XrplFile.Tests;

/// <summary>Closes a standalone ledger every half second on its own connection, until disposed.</summary>
internal sealed class LedgerPump : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pumping;

    public LedgerPump(string endpoint) => _pumping = PumpAsync(endpoint, _stop.Token);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();

        try
        {
            await _pumping;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private static async Task PumpAsync(string endpoint, CancellationToken token)
    {
        IXrplClient client = new XrplClient(endpoint);
        await client.Connect(token);

        try
        {
            while (!token.IsCancellationRequested)
            {
                await client.AnyRequest(new BaseRequest { Command = "ledger_accept" }, token);
                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
