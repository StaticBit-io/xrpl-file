using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class ActivityLeaseTests
{
    [Fact]
    public void It_touches_at_once_then_every_minute_until_disposed()
    {
        ManualTimeProvider time = new();
        int touches = 0;

        ActivityLease lease = ActivityLease.Start(() => touches++, time);
        Assert.Equal(1, touches);

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, touches);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, touches);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(3, touches);

        lease.Dispose();
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(3, touches);
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        ActivityLease lease = ActivityLease.Start(() => { }, new ManualTimeProvider());

        lease.Dispose();
        lease.Dispose();
    }
}
