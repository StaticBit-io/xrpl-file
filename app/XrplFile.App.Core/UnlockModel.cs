using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>Where the unlock screen stands (design 6).</summary>
public enum UnlockStage
{
    /// <summary>No vault file exists at this location yet. The screen offers to create one.</summary>
    NoVault,

    /// <summary>A vault exists and is not open.</summary>
    Locked,

    /// <summary>The vault is open.</summary>
    Unlocked,
}

/// <summary>
/// The model behind creating a vault and opening one (design 6-7): what the unlock screen can do,
/// what it refuses, and what it says. Every branch the screen can take lives here, reachable from
/// a test with no operating system in the loop (design E2).
/// </summary>
public sealed class UnlockModel(IKeyVault vault)
{
    /// <summary>Where the screen stands right now. Meaningful only after <see cref="RefreshAsync"/> has run at least once.</summary>
    public UnlockStage Stage { get; private set; } = UnlockStage.NoVault;

    /// <summary>
    /// The message for the most recent attempt to open the vault — by password or by device — or
    /// <see langword="null"/> before any such attempt has been made. Never set by
    /// <see cref="RefreshAsync"/>: a refresh answers a question about what is possible, not an
    /// attempt the person made, and showing a result as if it were one would tell a story about an
    /// action nobody took.
    /// </summary>
    public UnlockMessage? LastMessage { get; private set; }

    /// <summary>
    /// The message for the most recent attempt to create a vault, or <see langword="null"/> before
    /// any such attempt has failed. Never set on success: <see cref="Stage"/> moves to
    /// <see cref="UnlockStage.Unlocked"/> instead, and <c>Shell.razor</c> switches away from the
    /// screen that would have shown it.
    /// </summary>
    public CreateMessage? LastCreateMessage { get; private set; }

    /// <summary>
    /// Whether "unlock with device" is worth showing right now — whether the vault's record
    /// carries a device wrap at all, not whether opening it that way would actually succeed this
    /// instant. See the remarks on <see cref="RefreshAsync"/> for why that is the question this
    /// answers, and what it can be wrong about.
    /// </summary>
    public bool DeviceUnlockOffered { get; private set; }

    /// <summary>
    /// Re-reads where the vault stands: whether a file exists at all, and — when one does and it
    /// is not already open — whether the record carries a device wrap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This never opens the vault to answer the question.</b> An earlier version of this method
    /// answered "would a device unlock work right now" by actually calling
    /// <see cref="IKeyVault.UnlockWithDeviceAsync"/> and, if that opened the vault, calling
    /// <see cref="IKeyVault.LockAsync"/> to put it back — on the theory that a truthful answer
    /// needed a real attempt. It does not: those are two separate calls against a vault whose gate
    /// releases between them, and <see cref="IKeyVault.UnlockWithDeviceAsync"/> unconditionally
    /// discards whatever key was already resident before installing its own. A real press on
    /// "unlock with device", landing in the gap between this method's own unlock and its
    /// compensating lock, unlocks the vault for real and sets <see cref="Stage"/> to
    /// <see cref="UnlockStage.Unlocked"/> — and then this method's queued lock call runs anyway and
    /// wipes the key it just unlocked, leaving the screen saying "in" over a vault that is shut.
    /// </para>
    /// <para>
    /// A refresh that can change whether the vault is open is the wrong shape regardless of the
    /// race: this method must never be the thing that opens or closes the vault, so it now asks
    /// <see cref="IKeyVault.HasDeviceWrapAsync"/> — a read of the record, not an unlock — and pays
    /// no more than that read costs. See that method's remarks for what this trades away: a device
    /// wrap can be present while the platform secret store has lost the secret it was sealed under
    /// or holds one that no longer opens it, so <see cref="DeviceUnlockOffered"/> can say "yes" to
    /// a press that then reports <see cref="VaultUnlockResult.VaultMissing"/> — the outcome that
    /// exists precisely to say "there is nothing left to open with, turn the setting off". It
    /// cannot say "no" to a press that would have worked: no wrap means nothing to open with,
    /// truthfully, on every path this method can see.
    /// </para>
    /// </remarks>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (vault.IsUnlocked)
        {
            Stage = UnlockStage.Unlocked;
            DeviceUnlockOffered = false;
            return;
        }

        bool exists = await vault.ExistsAsync(cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            Stage = UnlockStage.NoVault;
            DeviceUnlockOffered = false;
            return;
        }

        Stage = UnlockStage.Locked;
        DeviceUnlockOffered = await vault.HasDeviceWrapAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a new, empty vault and leaves it open, or records what to tell the person if it
    /// could not be written.
    /// </summary>
    /// <remarks>
    /// Catches exactly the two failures <see cref="IKeyVault.CreateAsync"/> can produce for a
    /// screen to react to — see <see cref="VaultCreateResult"/> for why those are the only two —
    /// and turns each into a <see cref="CreateMessage"/> with no path, no internal exception
    /// detail, and nothing derived from the password. Every other exception, cancellation
    /// included, is let through unchanged: this method translates what a screen may show, it does
    /// not catch everything a bug could throw.
    /// </remarks>
    public async Task<VaultCreateResult> CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken)
    {
        VaultCreateResult result;
        try
        {
            await vault.CreateAsync(password, cancellationToken).ConfigureAwait(false);
            result = VaultCreateResult.Success;
        }
        catch (InvalidOperationException)
        {
            // KeyVault.CreateAsync's own refusal to overwrite a vault that is already there.
            result = VaultCreateResult.VaultAlreadyExists;
        }
        catch (VaultException ex) when (ex.Error == VaultError.StorageFailed)
        {
            // DurableFile.WriteAsync's refusal, wrapped around whatever IOException or
            // UnauthorizedAccessException the filesystem actually threw.
            result = VaultCreateResult.StorageUnavailable;
        }

        if (result == VaultCreateResult.Success)
        {
            Stage = UnlockStage.Unlocked;
            LastCreateMessage = null;
            DeviceUnlockOffered = false;
        }
        else
        {
            LastCreateMessage = CreateMessages.For(result);
        }

        return result;
    }

    /// <summary>Opens the vault with a password and records what to tell the person about the outcome.</summary>
    public async Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken)
    {
        VaultUnlockResult result = await vault.UnlockAsync(password, cancellationToken).ConfigureAwait(false);
        Apply(result);
        return result;
    }

    /// <summary>Opens the vault with whatever "remember on this device" left behind — a deliberate act, from a press on the button <see cref="DeviceUnlockOffered"/> controls.</summary>
    public async Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken)
    {
        VaultUnlockResult result = await vault.UnlockWithDeviceAsync(cancellationToken).ConfigureAwait(false);
        Apply(result);
        return result;
    }

    /// <summary>Locks the vault and returns the screen to <see cref="UnlockStage.Locked"/>.</summary>
    public async Task LockAsync()
    {
        await vault.LockAsync().ConfigureAwait(false);
        Stage = UnlockStage.Locked;
    }

    private void Apply(VaultUnlockResult result)
    {
        LastMessage = UnlockMessages.For(result);
        Stage = result is VaultUnlockResult.Success or VaultUnlockResult.VaultDegraded
            ? UnlockStage.Unlocked
            : UnlockStage.Locked;
    }
}
