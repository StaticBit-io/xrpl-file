using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// <see cref="RevealModel"/>'s idle window is tested against a clock the test itself moves, never
/// one it waits on — see <see cref="ManualTimeProvider"/>. Every scenario here runs in well under a
/// millisecond of wall-clock time regardless of how many simulated seconds it advances through.
/// </summary>
public class RevealModelTests : VaultHarness
{
    [Fact]
    public async Task Shown_is_null_before_reveal_the_entrys_secret_after_it_and_null_again_after_hide()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        RevealModel model = new(vault, TimeProvider.System);

        Assert.Null(model.Shown);

        await model.ShowAsync(reference, token);
        Assert.Equal("sSecretOfone", model.Shown);

        model.Hide();
        Assert.Null(model.Shown);
    }

    [Fact]
    public async Task A_shown_seed_hides_itself_once_the_idle_window_passes()
    {
        // The Wallet hides after 60 seconds; the value is ours, the discipline is theirs.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        ManualTimeProvider time = new();
        RevealModel model = new(vault, time);

        await model.ShowAsync(reference, token);
        Assert.NotNull(model.Shown);

        time.Advance(TimeSpan.FromSeconds(61));

        Assert.Null(model.Shown);
    }

    [Fact]
    public async Task The_secret_stays_shown_before_the_idle_window_passes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        ManualTimeProvider time = new();
        RevealModel model = new(vault, time);

        await model.ShowAsync(reference, token);
        time.Advance(TimeSpan.FromSeconds(59));

        Assert.Equal("sSecretOfone", model.Shown);
    }

    [Fact]
    public async Task Showing_a_second_entry_cancels_the_first_entrys_hide_timer()
    {
        // Otherwise the first timer would still fire and clear Shown out from under the second
        // reveal — see ShowAsync's remarks on why the previous window is torn down first.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string first = await vault.StoreAsync(Entry("one"), token);
        string second = await vault.StoreAsync(Entry("two"), token);
        ManualTimeProvider time = new();
        RevealModel model = new(vault, time);

        await model.ShowAsync(first, token);
        time.Advance(TimeSpan.FromSeconds(45));
        await model.ShowAsync(second, token);
        time.Advance(TimeSpan.FromSeconds(45));

        // 90 simulated seconds since the first reveal, but only 45 since the second: the first
        // timer must not have fired and cleared what the second call just showed.
        Assert.Equal("sSecretOftwo", model.Shown);
    }

    [Fact]
    public async Task CopyAsync_passes_a_non_zero_expiry_to_the_clipboard()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        RevealModel model = new(vault, TimeProvider.System);
        RecordingClipboard clipboard = new();

        await model.ShowAsync(reference, token);
        await model.CopyAsync(clipboard, token);

        Assert.Equal("sSecretOfone", clipboard.CopiedValue);
        Assert.True(clipboard.CopiedExpiry > TimeSpan.Zero);
    }

    [Fact]
    public async Task CopyAsync_throws_when_nothing_is_shown()
    {
        RevealModel model = new(NewVault(), TimeProvider.System);
        RecordingClipboard clipboard = new();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.CopyAsync(clipboard, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Revealing_from_a_locked_vault_surfaces_Locked_rather_than_returning_null_silently()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        await vault.LockAsync();
        RevealModel model = new(vault, TimeProvider.System);

        VaultException error = await Assert.ThrowsAsync<VaultException>(() => model.ShowAsync(reference, token));

        Assert.Equal(VaultError.Locked, error.Error);
        Assert.Null(model.Shown);
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? CopiedValue { get; private set; }

        public TimeSpan CopiedExpiry { get; private set; }

        public Task CopySecretAsync(string value, TimeSpan expiry, CancellationToken cancellationToken)
        {
            CopiedValue = value;
            CopiedExpiry = expiry;
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// A TimeProvider double for <see cref="RevealModelTests"/>: time moves only when the test calls
/// <see cref="Advance"/>, and advancing fires every still-scheduled timer whose due time has now
/// been reached, synchronously, on the calling thread. Mirrors
/// <c>tests/XrplFile.Keys.Tests/VaultAutoLockTests.cs</c>'s own <c>ManualTimeProvider</c>: the two
/// live in separate test projects and are not shared code, so a change to one does not have to
/// touch the other, and this is simpler than pulling in a package for it —
/// <c>Microsoft.Extensions.Time.Testing</c> is not referenced anywhere in this repository.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state, dueTime);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves the clock forward and fires every still-scheduled timer whose due time has now passed.</summary>
    public void Advance(TimeSpan by)
    {
        _now += by;

        // A snapshot: a callback firing here disposes its own timer (RevealModel.Hide does exactly
        // that), which removes it from _timers — safe against a copy, not against the live list.
        foreach (ManualTimer timer in _timers.ToArray())
        {
            timer.FireIfDue(_now);
        }
    }

    private void Forget(ManualTimer timer) => _timers.Remove(timer);

    /// <summary>
    /// The one-shot shape <see cref="RevealModel"/> actually uses: a due time relative to "now" at
    /// construction, and no periodic re-arming — firing does not reschedule it, matching
    /// <see cref="Timeout.InfiniteTimeSpan"/> always being the period <see cref="RevealModel"/>
    /// passes. Nothing here ever calls <see cref="Change"/> again.
    /// </summary>
    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private readonly DateTimeOffset _dueAt = owner.GetUtcNow() + dueTime;
        private bool _disposed;
        private bool _fired;

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        internal void FireIfDue(DateTimeOffset now)
        {
            if (_disposed || _fired || now < _dueAt)
            {
                return;
            }

            _fired = true;
            callback(state);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.Forget(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
