using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The model behind showing one entry's secret on screen (design section 8): hidden by default,
/// shown on a deliberate press, and hidden again on its own once an idle window passes — the
/// Wallet's "hides after 60 seconds" discipline, driven by a clock this model is handed rather than
/// one it reads for itself, so a test can prove the behaviour without waiting on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Locking the vault is not this model's job.</b> It neither listens for the vault locking nor
/// polls <see cref="IKeyVault.IsUnlocked"/> to decide when to act. <see cref="ShowAsync"/> already
/// surfaces <see cref="VaultError.Locked"/> from <see cref="IKeyVault.RevealAsync"/> when the vault
/// turns out to be locked at the moment of the call — see that method's remarks for why that must
/// propagate rather than collapse into a quiet <see langword="null"/>.
/// </para>
/// <para>
/// Design section 8 also asks for a shown secret to clear the instant the vault locks.
/// <see cref="IKeyVault"/> exposes no event a model could react to when that happens, and inventing
/// one for this single caller is a bigger seam than this task needs — so <see cref="Shown"/> is
/// computed from <see cref="IKeyVault.IsUnlocked"/> on every read instead of being pushed to by a
/// notification. That closes the rule for anyone who reads <see cref="Shown"/> after the vault has
/// locked, including its own idle timer's lock (<c>VaultAutoLock</c> calls <c>LockAsync</c> without
/// asking this model first), with no wiring beyond what this type already owns. See
/// <see cref="Shown"/>'s own remarks for exactly what that guarantees and what it does not.
/// </para>
/// </remarks>
/// <param name="vault">The vault a reference is revealed from.</param>
/// <param name="time">
/// The clock the idle window is measured against. Production passes <see cref="TimeProvider.System"/>;
/// a test passes one it controls — the same reason <c>VaultAutoLock</c> takes one — so a sixty-second
/// default is never sixty real seconds in a suite.
/// </param>
public sealed class RevealModel(IKeyVault vault, TimeProvider time)
{
    /// <summary>How long a shown secret stays on screen before hiding itself (design section 8).</summary>
    private static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(60);

    private ITimer? _hideTimer;
    private string? _shown;

    /// <summary>
    /// The entry's secret while it is on screen, or <see langword="null"/> before
    /// <see cref="ShowAsync"/> has run, after <see cref="Hide"/>, once the idle window above has
    /// passed with no further <see cref="ShowAsync"/> call, or once <see cref="IKeyVault.IsUnlocked"/>
    /// is <see langword="false"/> — design section 8's "cleared immediately when the vault locks",
    /// which the vault can trigger on its own (its idle timer, or any other caller of
    /// <c>LockAsync</c>) without this model being told.
    /// </summary>
    /// <remarks>
    /// This guarantees that no read of <see cref="Shown"/> after the vault has locked returns the
    /// secret — the strongest guarantee a model with no lock notification from <see cref="IKeyVault"/>
    /// can make on its own. It does <b>not</b> guarantee the secret has been erased from whatever a
    /// screen already painted with the earlier value: a label, a text field, a copy made before the
    /// lock. Nothing here reaches into the UI. A screen that wants the display itself to go blank on
    /// a lock must re-read or re-bind <see cref="Shown"/> when it learns the vault locked, not assume
    /// this property pushes that change to it.
    /// </remarks>
    public string? Shown => vault.IsUnlocked ? _shown : null;

    /// <summary>
    /// Reveals <paramref name="reference"/> and shows its secret as <see cref="Shown"/>, arming the
    /// idle window that hides it again.
    /// </summary>
    /// <remarks>
    /// Whatever was already shown is cleared first, the same as a fresh <see cref="Hide"/> call: a
    /// screen only ever has one entry open at a time, so two idle windows racing to clear
    /// <see cref="Shown"/> is not a shape this model needs to support. If <see cref="IKeyVault.RevealAsync"/>
    /// throws, nothing here has changed yet — the previous <see cref="Shown"/>, if any, is exactly
    /// what it was before the call, not quietly cleared for a reveal that never happened.
    /// </remarks>
    /// <exception cref="VaultException">
    /// Whatever <see cref="IKeyVault.RevealAsync"/> itself throws for this reference —
    /// <see cref="VaultError.Locked"/> on a locked vault, <see cref="VaultError.NoSuchEntry"/> for an
    /// unknown reference, <see cref="VaultError.EntryDamaged"/> for an entry that fails its own
    /// authentication tag — propagates unchanged rather than being swallowed into a null
    /// <see cref="Shown"/>: a locked vault is not the same fact as "there is nothing to show", and
    /// collapsing the two would tell a screen the entry does not exist when the truth is it was never
    /// asked for.
    /// </exception>
    public async Task ShowAsync(string reference, CancellationToken cancellationToken)
    {
        VaultEntry entry = await vault.RevealAsync(reference, "Shown on screen", cancellationToken).ConfigureAwait(false);

        Hide();
        _shown = entry.Secret;
        _hideTimer = time.CreateTimer(_ => Hide(), state: null, HideAfter, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Clears <see cref="Shown"/> and stops the idle window early. A harmless no-op when nothing is
    /// currently shown — a screen dismissed before the window elapses should not have to check first.
    /// </summary>
    public void Hide()
    {
        _shown = null;
        _hideTimer?.Dispose();
        _hideTimer = null;
    }

    /// <summary>
    /// Copies <see cref="Shown"/> to the clipboard through <paramref name="clipboard"/>, asking it to
    /// expire the value after the same <see cref="HideAfter"/> window this model already hides the
    /// screen with: a secret copied alongside a value about to disappear from the screen has no
    /// reason to outlive it on the clipboard.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing is currently shown to copy.</exception>
    public async Task CopyAsync(IClipboardService clipboard, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clipboard);

        if (Shown is null)
        {
            throw new InvalidOperationException("Nothing is shown to copy. Call ShowAsync first.");
        }

        await clipboard.CopySecretAsync(Shown, HideAfter, cancellationToken).ConfigureAwait(false);
    }
}
