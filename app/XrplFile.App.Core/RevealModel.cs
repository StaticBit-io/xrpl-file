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
/// polls <see cref="IKeyVault.IsUnlocked"/>. <see cref="ShowAsync"/> already surfaces
/// <see cref="VaultError.Locked"/> from <see cref="IKeyVault.RevealAsync"/> when the vault turns out
/// to be locked at the moment of the call — see that method's remarks for why that must propagate
/// rather than collapse into a quiet <see langword="null"/>. Design section 8 also asks for the
/// secret to clear the instant the vault locks while it is already on screen; <see cref="IKeyVault"/>
/// exposes no event a model could react to for that, and inventing one for this single caller is a
/// bigger seam than this task needs. The screen that owns both an unlock control and this model is
/// where that wiring belongs — calling <see cref="Hide"/> the moment it learns the vault locked — and
/// it is recorded as a decision left open for the task that writes that screen, not silently assumed
/// away here.
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

    /// <summary>
    /// The entry's secret while it is on screen, or <see langword="null"/> before
    /// <see cref="ShowAsync"/> has run, after <see cref="Hide"/>, or once the idle window above has
    /// passed with no further <see cref="ShowAsync"/> call.
    /// </summary>
    public string? Shown { get; private set; }

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
        Shown = entry.Secret;
        _hideTimer = time.CreateTimer(_ => Hide(), state: null, HideAfter, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Clears <see cref="Shown"/> and stops the idle window early. A harmless no-op when nothing is
    /// currently shown — a screen dismissed before the window elapses should not have to check first.
    /// </summary>
    public void Hide()
    {
        Shown = null;
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
