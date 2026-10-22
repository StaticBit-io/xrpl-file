using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class ScreenLifetimeTests
{
    [Fact]
    public void A_live_screen_hands_out_a_token_that_is_not_cancelled()
    {
        ScreenLifetime lifetime = new();

        Assert.False(lifetime.Token.IsCancellationRequested);
    }

    [Fact]
    public void Reading_the_token_after_the_screen_ended_gives_a_cancelled_token_instead_of_throwing()
    {
        ScreenLifetime lifetime = new();
        CancellationToken before = lifetime.Token;

        lifetime.End();

        Assert.True(before.IsCancellationRequested);
        Assert.True(lifetime.Token.IsCancellationRequested);
    }

    [Fact]
    public void Ending_twice_is_harmless()
    {
        ScreenLifetime lifetime = new();

        lifetime.End();
        lifetime.End();

        Assert.True(lifetime.Token.IsCancellationRequested);
    }

    /// <summary>
    /// The shape that froze the window: a lifecycle method still awaiting when its screen was closed
    /// reads the token afterwards. It must end the way ComponentBase ignores — as a cancelled task —
    /// and not with ObjectDisposedException, which BlazorWebView treats as fatal.
    /// </summary>
    [Fact]
    public async Task An_await_that_outlives_its_screen_ends_as_a_cancelled_task()
    {
        ScreenLifetime lifetime = new();
        TaskCompletionSource firstStep = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task InitializeAsync()
        {
            await firstStep.Task;
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
        }

        Task initializing = InitializeAsync();
        lifetime.End();
        firstStep.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initializing);
        Assert.True(initializing.IsCanceled);
    }
}
