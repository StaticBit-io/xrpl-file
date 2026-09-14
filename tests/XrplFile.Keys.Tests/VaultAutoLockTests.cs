using Xunit;

namespace XrplFile.Keys.Tests;

/// <summary>
/// The idle lock, tested against a clock the test itself moves rather than one it has to wait on:
/// see <see cref="ManualTimeProvider"/>. Every scenario here runs in well under a millisecond of
/// wall-clock time regardless of how many simulated minutes it advances through.
/// </summary>
public class VaultAutoLockTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task The_vault_locks_once_the_idle_window_passes_with_no_touch()
    {
        ManualTimeProvider time = new();
        RecordingVault vault = new();
        await using VaultAutoLock autoLock = new(vault, Idle, time);

        time.Advance(Idle);

        Assert.Equal(1, vault.LockCalls);
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task The_vault_stays_unlocked_before_the_idle_window_passes()
    {
        ManualTimeProvider time = new();
        RecordingVault vault = new();
        await using VaultAutoLock autoLock = new(vault, Idle, time);

        time.Advance(Idle - TimeSpan.FromSeconds(1));

        Assert.Equal(0, vault.LockCalls);
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task A_touch_inside_the_window_postpones_the_lock()
    {
        ManualTimeProvider time = new();
        RecordingVault vault = new();
        await using VaultAutoLock autoLock = new(vault, Idle, time);

        // Ten minutes in, a touch — a signature, in the real caller — resets the window to fire
        // fifteen minutes from here (at t = 25), not from construction (which would have been
        // t = 15). The next fourteen minutes bring the clock to t = 24: one minute short of the
        // postponed window, so nothing should have locked yet, even though a window measured from
        // construction would already have fired five minutes ago.
        time.Advance(TimeSpan.FromMinutes(10));
        autoLock.Touch();
        time.Advance(TimeSpan.FromMinutes(14));

        Assert.Equal(0, vault.LockCalls);
        Assert.True(vault.IsUnlocked);

        // The final minute completes the window the touch postponed.
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, vault.LockCalls);
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task Repeated_touches_keep_postponing_the_lock_indefinitely()
    {
        ManualTimeProvider time = new();
        RecordingVault vault = new();
        await using VaultAutoLock autoLock = new(vault, Idle, time);

        for (int minute = 0; minute < 100; minute++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            autoLock.Touch();
        }

        // A hundred minutes of continuous activity, each touch inside the previous window: a
        // publication running for tens of minutes must never lock itself out mid-run.
        Assert.Equal(0, vault.LockCalls);
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task The_idle_window_starts_again_after_a_lock_that_nothing_touched()
    {
        ManualTimeProvider time = new();
        RecordingVault vault = new();
        await using VaultAutoLock autoLock = new(vault, Idle, time);

        // The first window fires and locks, exactly as it always did.
        time.Advance(Idle);
        Assert.Equal(1, vault.LockCalls);

        // The failure this test exists for: with Timeout.InfiniteTimeSpan as the timer's period,
        // the first fire was also the last one, and only VaultAutoLock.Touch could ever arm it
        // again. Nothing in the application this lock protects calls Touch on a schedule, so a
        // vault unlocked again after this point would have sat resident with no idle bound at all
        // for the rest of the process. A window that has fired must start the next one by itself.
        vault.Unlock();
        time.Advance(Idle);

        Assert.Equal(2, vault.LockCalls);
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task Disposing_stops_the_timer_without_locking()
    {
        ManualTimeProvider time = new();
        RecordingVault vault = new();
        VaultAutoLock autoLock = new(vault, Idle, time);

        time.Advance(TimeSpan.FromMinutes(10));
        await autoLock.DisposeAsync();

        // Enough simulated time for the original window to have elapsed several times over, had
        // the timer still been live.
        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, vault.LockCalls);
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task Disposing_twice_is_a_no_op()
    {
        ManualTimeProvider time = new();
        RecordingVault vault = new();
        VaultAutoLock autoLock = new(vault, Idle, time);

        await autoLock.DisposeAsync();
        await autoLock.DisposeAsync();

        Assert.Equal(0, vault.LockCalls);
    }

    [Fact]
    public async Task A_touch_after_disposal_is_a_harmless_no_op()
    {
        ManualTimeProvider time = new();
        RecordingVault vault = new();
        VaultAutoLock autoLock = new(vault, Idle, time);

        await autoLock.DisposeAsync();

        // Must not throw: a caller touching a signer after its publication has finished, and the
        // auto-lock alongside it disposed, should not have to guard every call with a check first.
        autoLock.Touch();

        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, vault.LockCalls);
    }

    [Fact]
    public async Task A_touch_once_locked_does_not_silently_unlock_anything()
    {
        ManualTimeProvider time = new();
        RecordingVault vault = new();
        await using VaultAutoLock autoLock = new(vault, Idle, time);

        time.Advance(Idle);
        Assert.Equal(1, vault.LockCalls);
        Assert.False(vault.IsUnlocked);

        // Touch has exactly one job — reschedule the timer — and nothing in it so much as looks
        // at whether the vault is open.
        autoLock.Touch();
        Assert.False(vault.IsUnlocked);

        // The rescheduled timer firing again only asks the vault to lock a second time — itself
        // a no-op on an already-locked vault — never to unlock.
        time.Advance(Idle);
        Assert.Equal(2, vault.LockCalls);
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task A_callback_that_throws_does_not_escape_the_timer_or_the_process()
    {
        ManualTimeProvider time = new();
        RecordingVault vault = new() { ThrowOnLock = true };
        await using VaultAutoLock autoLock = new(vault, Idle, time);

        // If the swallow in VaultAutoLock's callback were missing, this call would throw the
        // exception LockAsync raised, straight out of Advance.
        time.Advance(Idle);

        Assert.Equal(1, vault.LockCalls);
    }

    [Fact]
    public void The_constructor_rejects_a_non_positive_idle_window()
    {
        ManualTimeProvider time = new();
        RecordingVault vault = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => new VaultAutoLock(vault, TimeSpan.Zero, time));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VaultAutoLock(vault, TimeSpan.FromMinutes(-1), time));
    }

    [Fact]
    public void The_constructor_rejects_a_null_vault_or_time_provider()
    {
        ManualTimeProvider time = new();
        RecordingVault vault = new();

        Assert.Throws<ArgumentNullException>(() => new VaultAutoLock(null!, Idle, time));
        Assert.Throws<ArgumentNullException>(() => new VaultAutoLock(vault, Idle, null!));
    }
}

/// <summary>
/// A TimeProvider double for <see cref="VaultAutoLockTests"/>: time moves only when the test calls
/// <see cref="Advance"/>, and advancing fires every still-scheduled timer whose due time has now
/// been reached, synchronously, on the calling thread. Good enough for the one timer
/// <see cref="VaultAutoLock"/> ever creates, and simpler than pulling in a package for it —
/// <c>Microsoft.Extensions.Time.Testing</c> is not referenced anywhere in this repository, and
/// task 10 asks for a hand-written double rather than adding one for a single class this small.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state, dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves the clock forward and fires every still-scheduled timer whose due time has now passed.</summary>
    public void Advance(TimeSpan by)
    {
        _now += by;

        // A snapshot: a callback firing here may go on (on its own task, never waited on by this
        // call — see VaultAutoLock's remarks) to touch this same provider through a later Touch
        // during some other test, but never through this one call's own iteration.
        foreach (ManualTimer timer in _timers.ToArray())
        {
            timer.FireIfDue(_now);
        }
    }

    private void Forget(ManualTimer timer) => _timers.Remove(timer);

    /// <summary>
    /// A due time relative to "now" at construction or at the last <see cref="Change"/>, plus the
    /// period <see cref="ITimer"/> itself defines: firing re-arms the timer one period later, and
    /// only <see cref="Timeout.InfiniteTimeSpan"/> as the period makes it one-shot. The period is
    /// honoured rather than ignored because <see cref="VaultAutoLock"/> now passes a real one —
    /// a double that silently dropped it would let a one-shot implementation pass a test written
    /// for a repeating window.
    /// </summary>
    private sealed class ManualTimer(
        ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private DateTimeOffset _dueAt = owner.GetUtcNow() + dueTime;
        private TimeSpan _period = period;
        private bool _disposed;
        private bool _fired;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ManualTimer));
            }

            _dueAt = owner.GetUtcNow() + dueTime;
            _period = period;
            _fired = false;
            return true;
        }

        internal void FireIfDue(DateTimeOffset now)
        {
            if (_disposed || _fired || now < _dueAt)
            {
                return;
            }

            if (_period == Timeout.InfiniteTimeSpan)
            {
                _fired = true;
            }
            else
            {
                _dueAt = now + _period;
            }

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

/// <summary>
/// A minimal <see cref="IKeyVault"/> double for <see cref="VaultAutoLockTests"/>: it starts
/// unlocked and counts <see cref="LockAsync"/> calls, which is the entire vocabulary
/// <see cref="VaultAutoLock"/> uses. Every other member throws <see cref="NotSupportedException"/>,
/// so a test that ever reaches one is telling the auto-lock it called something it should not have.
/// </summary>
internal sealed class RecordingVault : IKeyVault
{
    /// <summary>How many times the auto-lock asked this vault to lock.</summary>
    public int LockCalls { get; private set; }

    /// <summary>Set before construction to make <see cref="LockAsync"/> fail instead of succeeding.</summary>
    public bool ThrowOnLock { get; set; }

    /// <inheritdoc/>
    public bool IsUnlocked { get; private set; } = true;

    /// <summary>
    /// Puts this double back into the unlocked state, standing in for a person opening the vault
    /// again after an idle lock. There is no <c>UnlockAsync</c> here to do it with: the auto-lock
    /// never unlocks anything, so that member stays unsupported like every other one.
    /// </summary>
    public void Unlock() => IsUnlocked = true;

    /// <inheritdoc/>
    public Task LockAsync()
    {
        LockCalls++;
        IsUnlocked = false;

        return ThrowOnLock
            ? Task.FromException(new InvalidOperationException("This vault double was told to fail its lock."))
            : Task.CompletedTask;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> References => throw new NotSupportedException();

    /// <inheritdoc/>
    public IReadOnlyList<string> DamagedReferences => throw new NotSupportedException();

    /// <inheritdoc/>
    public bool WritesAreRefused => throw new NotSupportedException();

    /// <inheritdoc/>
    public Task AcknowledgeDamageAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<bool> HasDeviceWrapAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <inheritdoc/>
    public Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<byte[]> ExportAsync(
        string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public Task UpdateAsync(string reference, VaultEntryEdit edit, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public Task DeleteAsync(string reference, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<VaultPasswordChange> ChangePasswordAsync(
        ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <inheritdoc/>
    public Task DisableDeviceUnlockAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}
