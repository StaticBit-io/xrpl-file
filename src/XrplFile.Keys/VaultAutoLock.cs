namespace XrplFile.Keys;

/// <summary>
/// A sliding idle timeout that locks <paramref name="vault"/> once nothing has touched it for
/// <paramref name="idle"/> (design section 6.4). A publication that calls <see cref="Touch"/> on
/// every signature keeps itself open for as long as it is actually working; a machine left alone
/// with the vault open does not stay that way.
///
/// The window repeats for the life of this instance rather than firing once: a vault unlocked
/// again after an idle lock is bounded by the next window exactly as the first one was, with no
/// <see cref="Touch"/> needed to arm it. See <see cref="CreateIdleTimer"/> for what that costs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a lock firing mid-operation is safe.</b> <see cref="IKeyVault.LockAsync"/> takes the same
/// gate every other vault operation does and waits for whatever is in flight rather than cutting it
/// off (design section 6.3, enforced by <see cref="KeyVault"/> itself). This type does not need to
/// know that — it only needs to call <see cref="IKeyVault.LockAsync"/>, on whatever
/// <see cref="IKeyVault"/> it was built with, and trust that call to be safe to make at any time,
/// including from a background timer.
/// </para>
/// <para>
/// <b>The callback never blocks and never throws outward.</b> <see cref="TimerCallback"/> is a
/// synchronous delegate, so the idle callback starts the lock and returns without awaiting it — the
/// lock itself finishes on its own. Whatever it throws is caught and dropped: a callback that faults
/// runs with nothing above it to report to, and letting an exception escape a timer callback would
/// tear down the process for what exists purely as a background safety net. See
/// <see cref="KeyVault.ForgetTheOrphanedDeviceSecretAsync"/> for the same call made for the same
/// reason elsewhere in this vault.
/// </para>
/// <para>
/// <b>Disposal never blocks on the vault either.</b> <see cref="DisposeAsync"/> only disposes the
/// underlying <see cref="ITimer"/>; it never waits on a lock the timer's callback may have already
/// started. That callback either has not fired yet — in which case disposing the timer means it
/// never will — or it already fired and is locking (or has locked) the vault on its own, a race this
/// type does not need to resolve because nothing here blocks on it. There is no lock object shared
/// between <see cref="Touch"/> and <see cref="DisposeAsync"/>: a disposed flag read and written only
/// through <see cref="Interlocked"/> is all either needs, so the two can never deadlock against each
/// other.
/// </para>
/// </remarks>
/// <param name="vault">The vault this instance locks once idle. Locking an already-locked vault is a no-op.</param>
/// <param name="idle">How long the vault may sit untouched before it locks. Must be a positive duration.</param>
/// <param name="time">
/// The clock the idle window is measured against. Production passes <see cref="TimeProvider.System"/>;
/// a test passes one it controls, so that a fifteen-minute default is never fifteen real minutes in
/// a suite.
/// </param>
public sealed class VaultAutoLock(IKeyVault vault, TimeSpan idle, TimeProvider time) : IAsyncDisposable
{
    private readonly TimeSpan _idle = idle > TimeSpan.Zero
        ? idle
        : throw new ArgumentOutOfRangeException(nameof(idle), idle, "The idle window must be a positive duration.");

    // CreateIdleTimer, not a field initializer expression written out here directly, because the
    // callback it builds has to capture the primary constructor's own "vault" parameter and call
    // only a static method: a field initializer runs before "this" exists as far as the language
    // is concerned, so it can neither read an instance field nor invoke an instance method
    // (CS0236) — only the constructor's own parameters are available to it, which is exactly what
    // a primary constructor's parameters are for.
    private readonly ITimer _timer = CreateIdleTimer(vault, idle, time);

    private static ITimer CreateIdleTimer(IKeyVault vault, TimeSpan idle, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(time);

        // Periodic, not one-shot. An idle window that fired once and never armed itself again
        // bounds only the first session in a process: the vault locks at the fifteen-minute mark,
        // the person unlocks it again, and from then on the data key stays resident for as long as
        // the process lives -- which is exactly the bound MauiProgram's own remarks lean on for a
        // process nobody kills. A period equal to the window starts the next one automatically.
        //
        // The cost of that is a LockAsync call every window against a vault that is already shut.
        // IKeyVault.LockAsync is a no-op on a locked vault (it takes the gate, finds no data key,
        // and returns), so the repeated call is cheap and cannot unlock anything -- see
        // Touch's own remarks for the same point from the other direction.
        return time.CreateTimer(_ => FireAndForgetLock(vault), state: null, idle, idle);
    }

    /// <summary>
    /// How long the vault may sit untouched before it locks. A caller that keeps the vault open on
    /// a person's behalf (a run waiting for an answer) bounds that wait by this window.
    /// </summary>
    public TimeSpan Idle => _idle;

    /// <summary>
    /// 0 while active, 1 once <see cref="DisposeAsync"/> has run. Read and written only through
    /// <see cref="Interlocked"/>, never under a lock — see the type's remarks on why disposal must
    /// never block.
    /// </summary>
    private int _disposed;

    /// <summary>Resets the idle window, as if the vault had just been used.</summary>
    /// <remarks>
    /// A best-effort no-op once this instance is disposed: a caller touching a signer after its
    /// publication has finished, and the auto-lock alongside it disposed, should not have to guard
    /// every call with a check first.
    /// </remarks>
    public void Touch()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            // The period is restated here, not dropped: ITimer.Change replaces both halves, and
            // passing Timeout.InfiniteTimeSpan would quietly turn the repeating window built above
            // back into a one-shot from the first touch onward.
            _timer.Change(_idle, _idle);
        }
        catch (ObjectDisposedException)
        {
            // Lost the race with DisposeAsync between the check above and this call. The timer is
            // already gone, which is exactly the state Touch was about to ask for anyway.
        }
    }

    /// <summary>
    /// Runs on the timer's own callback. Starts the lock and returns immediately without waiting
    /// for it — see the type's remarks on why the callback must not block. Static, and given the
    /// vault explicitly, only because the timer that calls it is built in a field initializer that
    /// cannot reach back into the instance being constructed (see <see cref="_timer"/>'s comment);
    /// there is otherwise nothing here that would need to be static.
    /// </summary>
    private static void FireAndForgetLock(IKeyVault vault) => _ = LockQuietlyAsync(vault);

    private static async Task LockQuietlyAsync(IKeyVault vault)
    {
        try
        {
            await vault.LockAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Swallowed on purpose — see the type's remarks. Touch() after this still cannot
            // unlock anything; a lock already in effect (or in flight) stays exactly that.
        }
    }

    /// <summary>
    /// Stops the timer so no future idle window can start a lock. Never itself locks the vault, and
    /// never waits on a lock a callback may already have started — see the type's remarks.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _timer.DisposeAsync().ConfigureAwait(false);
    }
}
