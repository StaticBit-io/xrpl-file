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
    /// <see cref="RefreshAsync"/>'s own device-unlock probe: that probe answers a question about
    /// what is possible, not an attempt the person made, and showing its result as if it were one
    /// would tell a story about an action nobody took.
    /// </summary>
    public UnlockMessage? LastMessage { get; private set; }

    /// <summary>
    /// Whether "unlock with device" is worth showing right now — not merely whether the vault
    /// format has a place for a device wrap, but whether opening it that way would actually work
    /// this instant. See the remarks on <see cref="RefreshAsync"/> for how that is decided.
    /// </summary>
    public bool DeviceUnlockOffered { get; private set; }

    /// <summary>
    /// Re-reads where the vault stands: whether a file exists at all, and — when one does and it
    /// is not already open — whether a device unlock is currently possible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IKeyVault"/> exposes no side-effect-free way to ask "would a device unlock work
    /// right now" — the record does not say whether it carries a device wrap, and even if it did,
    /// that would only say the option exists in the abstract: the platform secret store could
    /// still have lost its secret, or the wrap could fail to open with what it has. The only
    /// truthful answer is to actually attempt <see cref="IKeyVault.UnlockWithDeviceAsync"/> and see.
    /// </para>
    /// <para>
    /// So that is what this does — and then, if the attempt opened the vault, it locks it straight
    /// back up. The probe is real, not simulated, but it does not get to be the thing that lets
    /// the person in: that stays <see cref="UnlockWithDeviceAsync"/>'s job alone, called from a
    /// deliberate press on the button this flag controls, and never a side effect of the screen
    /// merely being looked at again.
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
        DeviceUnlockOffered = await ProbeDeviceUnlockAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a new, empty vault and leaves it open.</summary>
    public async Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken)
    {
        await vault.CreateAsync(password, cancellationToken).ConfigureAwait(false);
        Stage = UnlockStage.Unlocked;
        LastMessage = null;
        DeviceUnlockOffered = false;
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

    private async Task<bool> ProbeDeviceUnlockAsync(CancellationToken cancellationToken)
    {
        VaultUnlockResult probe = await vault.UnlockWithDeviceAsync(cancellationToken).ConfigureAwait(false);

        // See UnlockMessages.For for why CS8524 alone is suppressed here: this switch already
        // covers every named VaultUnlockResult, and only an impossible cast value is left
        // uncovered. CS8509 — a genuinely new named member with no arm — stays live.
#pragma warning disable CS8524
        bool possible = probe switch
        {
            VaultUnlockResult.Success => true,
            VaultUnlockResult.VaultDegraded => true,
            VaultUnlockResult.VaultUnavailable => true,
            VaultUnlockResult.VaultMissing => false,
            VaultUnlockResult.VaultCorrupt => false,
            VaultUnlockResult.VaultUnsupported => false,
            VaultUnlockResult.WrongPassword => false,
        };
#pragma warning restore CS8524

        if (probe is VaultUnlockResult.Success or VaultUnlockResult.VaultDegraded)
        {
            // The probe just opened the vault for real, which is the only way it could answer
            // truthfully. Put it back exactly as it found it: a refresh must never be the thing
            // that lets the person in.
            await vault.LockAsync().ConfigureAwait(false);
        }

        return possible;
    }
}
