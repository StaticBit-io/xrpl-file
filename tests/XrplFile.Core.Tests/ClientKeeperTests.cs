using System.Reflection;
using Xrpl.Client;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// ClientKeeper over a fake <see cref="IXrplClient"/>. The interface is wide, so the fake is a
/// <see cref="DispatchProxy"/> that answers only Connect, Disconnect, IsConnected and Dispose.
/// </summary>
public class ClientKeeperTests
{
    [Fact]
    public async Task A_client_retired_while_a_connect_is_in_flight_is_refused_and_not_connected_again()
    {
        FakeClient fake = FakeClient.Create(out IXrplClient client);
        TaskCompletionSource connecting = fake.BlockNextConnect();
        ClientKeeper keeper = ClientKeeper.For(client);

        Task ensure = keeper.EnsureConnectedAsync(TestContext.Current.CancellationToken);
        await fake.WaitForConnectsAsync(1);

        ClientKeeper.Retire(client);
        // What the owner's Disconnect does to a Connect still pending.
        connecting.SetException(new OperationCanceledException("Connection was intentionally closed."));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => ensure);
        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Contains("closed by its owner", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, fake.Connects);

        await Assert.ThrowsAsync<XrplFileException>(() => keeper.EnsureConnectedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fake.Connects);
    }

    [Fact]
    public async Task A_connect_that_finishes_after_the_owner_retired_is_closed_again()
    {
        FakeClient fake = FakeClient.Create(out IXrplClient client);
        TaskCompletionSource connecting = fake.BlockNextConnect();
        ClientKeeper keeper = ClientKeeper.For(client);

        Task ensure = keeper.EnsureConnectedAsync(TestContext.Current.CancellationToken);
        await fake.WaitForConnectsAsync(1);

        ClientKeeper.Retire(client);
        fake.Connected = true;
        connecting.SetResult();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => ensure);
        Assert.Contains("closed by its owner", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, fake.Connects);
        Assert.Equal(1, fake.Disconnects);
    }

    [Fact]
    public async Task Two_callers_that_meet_a_drop_together_connect_once()
    {
        FakeClient fake = FakeClient.Create(out IXrplClient client);
        TaskCompletionSource connecting = fake.BlockNextConnect();
        ClientKeeper keeper = ClientKeeper.For(client);

        Task first = keeper.EnsureConnectedAsync(TestContext.Current.CancellationToken);
        Task second = keeper.EnsureConnectedAsync(TestContext.Current.CancellationToken);
        await fake.WaitForConnectsAsync(1);

        fake.Connected = true;
        connecting.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, fake.Connects);
    }

    [Fact]
    public async Task A_connect_failure_that_is_not_the_socket_cleanup_is_not_retried()
    {
        FakeClient fake = FakeClient.Create(out IXrplClient client);
        fake.BlockNextConnect().SetException(new InvalidOperationException("refused"));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => ClientKeeper.For(client).EnsureConnectedAsync(TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Equal(1, fake.Connects);
    }

    public class FakeClient : DispatchProxy
    {
        private readonly Lock _gate = new();
        private readonly Queue<TaskCompletionSource> _connectScript = new();
        private int _connects;

        public bool Connected { get; set; }

        public int Connects => Volatile.Read(ref _connects);

        public int Disconnects { get; private set; }

        public static FakeClient Create(out IXrplClient client)
        {
            client = Create<IXrplClient, FakeClient>();

            return (FakeClient)(object)client;
        }

        /// <summary>The next Connect waits on the returned source; the test completes or faults it.</summary>
        public TaskCompletionSource BlockNextConnect()
        {
            TaskCompletionSource source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _connectScript.Enqueue(source);

            return source;
        }

        public async Task WaitForConnectsAsync(int count)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(10);

            while (Connects < count)
            {
                Assert.True(DateTime.UtcNow < until, "Connect was not reached");
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "Connect":
                    Interlocked.Increment(ref _connects);
                    TaskCompletionSource? source;
                    lock (_gate) _connectScript.TryDequeue(out source);

                    return source?.Task ?? Task.CompletedTask;
                case "Disconnect":
                    Disconnects++;
                    Connected = false;

                    return Task.CompletedTask;
                case "IsConnected":
                    return Connected;
                case "Dispose":
                    return null;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
