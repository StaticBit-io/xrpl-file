using Xunit;

namespace XrplFile.Tests;

public class BatchWindowTests
{
    [Fact]
    public async Task The_window_admits_its_size_and_holds_the_next_until_a_release()
    {
        BatchWindow window = new(2);
        await window.AcquireAsync(TestContext.Current.CancellationToken);
        await window.AcquireAsync(TestContext.Current.CancellationToken);

        Task third = window.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.False(third.IsCompleted);

        window.Release(busy: false);
        await third.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, window.InFlight);
    }

    [Fact]
    public async Task Three_busy_answers_in_a_row_halve_it()
    {
        BatchWindow window = new(16);

        for (int i = 0; i < 3; i++)
        {
            await window.AcquireAsync(TestContext.Current.CancellationToken);
            window.Release(busy: true);
        }

        Assert.Equal(8, window.Size);
    }

    [Fact]
    public async Task A_clean_answer_between_busy_ones_starts_the_count_again()
    {
        BatchWindow window = new(16);

        foreach (bool busy in new[] { true, true, false, true, true })
        {
            await window.AcquireAsync(TestContext.Current.CancellationToken);
            window.Release(busy);
        }

        Assert.Equal(16, window.Size);
    }

    [Fact]
    public async Task Ten_clean_sends_widen_it_by_one_and_it_never_goes_below_one()
    {
        BatchWindow window = new(1);

        for (int i = 0; i < 6; i++)
        {
            await window.AcquireAsync(TestContext.Current.CancellationToken);
            window.Release(busy: true);
        }

        Assert.Equal(1, window.Size);

        for (int i = 0; i < 10; i++)
        {
            await window.AcquireAsync(TestContext.Current.CancellationToken);
            window.Release(busy: false);
        }

        Assert.Equal(2, window.Size);
    }

    [Fact]
    public async Task Clean_sends_never_widen_it_past_its_ceiling()
    {
        BatchWindow window = new(16, maximum: 32);

        for (int i = 0; i < 1000; i++)
        {
            await window.AcquireAsync(TestContext.Current.CancellationToken);
            window.Release(busy: false);
        }

        Assert.Equal(32, window.Size);
    }

    [Fact]
    public void A_start_above_the_ceiling_starts_at_the_ceiling()
    {
        Assert.Equal(8, new BatchWindow(16, maximum: 8).Size);
    }

    [Fact]
    public async Task A_returned_slot_admits_the_next_and_counts_as_no_answer()
    {
        BatchWindow window = new(1);

        for (int i = 0; i < 12; i++)
        {
            await window.AcquireAsync(TestContext.Current.CancellationToken);
            window.Return();
        }

        Assert.Equal(1, window.Size);
        Assert.Equal(0, window.InFlight);
    }

    [Fact]
    public async Task A_cancelled_wait_does_not_take_a_slot()
    {
        BatchWindow window = new(1);
        await window.AcquireAsync(TestContext.Current.CancellationToken);

        using CancellationTokenSource cancel = new();
        Task waiting = window.AcquireAsync(cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        window.Release(busy: false);
        Assert.Equal(0, window.InFlight);
    }
}
