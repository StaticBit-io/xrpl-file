using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class BatchAvailabilityTests
{
    [Fact]
    public async Task A_read_is_cached_per_network_for_five_minutes_then_read_again()
    {
        FakeConnectionFactory connections = new();
        ManualTimeProvider time = new();
        BatchAvailability availability = new(connections, time);

        Assert.True(await availability.IsEnabledAsync("testnet", TestContext.Current.CancellationToken));

        connections.Ledger.EnabledAmendments.Clear();
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(await availability.IsEnabledAsync("testnet", TestContext.Current.CancellationToken));

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await availability.IsEnabledAsync("testnet", TestContext.Current.CancellationToken));
        Assert.Equal(2, connections.Connections);
    }

    [Fact]
    public async Task A_failed_read_is_unknown_and_not_cached()
    {
        FakeConnectionFactory connections = new() { ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "down") };
        BatchAvailability availability = new(connections, new ManualTimeProvider());

        Assert.Null(await availability.IsEnabledAsync("testnet", TestContext.Current.CancellationToken));

        connections.ConnectThrows = null;
        Assert.True(await availability.IsEnabledAsync("testnet", TestContext.Current.CancellationToken));
    }
}
