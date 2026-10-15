using System.Diagnostics.CodeAnalysis;

using Xrpl.AddressCodec;
using Xrpl.Wallet;

using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>What the add-key screen has typed in, for the seed-import route (design 8).</summary>
/// <param name="Label">The name the entry will be shown under.</param>
/// <param name="Network">The network the imported address belongs to.</param>
/// <param name="Seed">The seed to derive the key from.</param>
/// <param name="Algorithm">
/// The algorithm the screen believes the seed is (<see cref="XrplWallet.Ed25519"/> or
/// <see cref="XrplWallet.Secp256k1"/>), or <see langword="null"/> when it was not asked. This is
/// only ever a hint: an XRPL family seed carries its own curve in its prefix, and
/// <see cref="AddKeyModel.Import"/> reads the true algorithm from the seed itself rather than
/// trusting this field — see its remarks.
/// </param>
public sealed record AddKeyDraft(string Label, string Network, string? Seed, string? Algorithm);

/// <summary>
/// What is shown before a key reaches the vault: the address actually derived from the key
/// material, and the algorithm it was actually derived with. Never built from anything the user
/// typed directly — always from a derivation — so a preview can be trusted the way a value the
/// user only typed cannot.
/// </summary>
public sealed record AddKeyPreview(string Address, string Algorithm);

/// <summary>
/// One key found inside an opened backup container, offered so the caller can choose which of
/// them — a container can hold more than one — to import. Carries no secret: that stays inside
/// the model until <see cref="AddKeyModel.SelectContainerKey"/> is called for it.
/// </summary>
/// <param name="Address">The address the container's entry claims for this key.</param>
/// <param name="Algorithm">The algorithm the container's entry records for this key.</param>
/// <param name="Label">
/// The container's own description for this key, or <see langword="null"/> when it has none —
/// offered as a starting point for the label the caller asks the user for, not as the label that
/// will be stored.
/// </param>
/// <param name="Network">
/// The network the container's entry records, or <see langword="null"/> when it has none — which
/// is every container the Wallet application writes, since its format carries no such field. A
/// <see langword="null"/> here is why <see cref="AddKeyModel.SelectContainerKey"/> requires the
/// caller to supply one rather than reusing whatever came out of the container.
/// </param>
public sealed record ContainerKeyOption(string Address, string Algorithm, string? Label, string? Network);

/// <summary>
/// The model behind adding a key to the vault (design 8): a generated key, a typed seed, or a key
/// chosen out of an imported backup container. Every branch the add-key screen can take lives
/// here, reachable from a test with no operating system in the loop.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Generate"/>, <see cref="Import"/> and <see cref="SelectContainerKey"/> only prepare
/// and validate; <see cref="StoreAsync"/> is the one call that writes. Nothing reaches the vault
/// until an address has been derived and shown as <see cref="Preview"/> — a mistyped seed that
/// happens to derive a valid-looking address the user does not own is the failure this order
/// prevents, and it holds for all three routes in, including the one where the container already
/// claims an address of its own (see <see cref="SelectContainerKey"/>).
/// </para>
/// <para>
/// <see cref="OpenContainer"/> is the third route, not in the original design: the format that
/// carries a key between this application and the Wallet moves in both directions, and an export
/// nobody can import back is only half of that feature. Opening a container is a separate step
/// from choosing a key out of it, because a container can hold several — <see cref="ContainerKeys"/>
/// is the whole list, and <see cref="SelectContainerKey"/> is called once per key the caller
/// decides to keep, each followed by its own <see cref="StoreAsync"/>.
/// </para>
/// </remarks>
public sealed class AddKeyModel(IKeyVault vault)
{
    private IReadOnlyList<BackupWalletEntry> _containerWallets = [];
    private NewVaultEntry? _pending;

    /// <summary>
    /// The address and algorithm most recently derived by <see cref="Generate"/>,
    /// <see cref="Import"/> or <see cref="SelectContainerKey"/> — or <see langword="null"/> before
    /// any of them has succeeded, or after the most recent call to any of them failed.
    /// </summary>
    public AddKeyPreview? Preview { get; private set; }

    /// <summary>
    /// What went wrong with the most recent call, in words a screen can show as-is — or
    /// <see langword="null"/> after a call that succeeded. Never built from the seed, the
    /// container's password, or anything else that must not be shown back to the person who typed
    /// it.
    /// </summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Whether the entry the most recent successful <see cref="StoreAsync"/> wrote is one this
    /// application must offer to export before letting the screen move on — <see langword="false"/>
    /// before any store has succeeded, and unchanged by one that failed.
    /// </summary>
    /// <remarks>
    /// The rule is <see cref="ExportNeed"/>'s, the same one <see cref="VaultListModel"/> already
    /// applies to decide whether a row wears the red badge. The add-key screen used to decide this
    /// for itself, from which tab it was on: a container written by this application for a
    /// generated key re-imported through the "Open a backup file" tab produced an entry whose kind
    /// is <see cref="VaultEntryKind.Generated"/>, so the list showed the badge while the import
    /// route never offered the export step. A tab is not a fact about a key; the entry's own kind
    /// is, and it is the same fact the list reads.
    /// </remarks>
    public bool LastStoredNeedsExporting { get; private set; }

    /// <summary>
    /// The keys inside the container most recently opened by <see cref="OpenContainer"/> — empty
    /// before that has succeeded, and empty again after a call to it that failed. Stays populated
    /// across a call to <see cref="SelectContainerKey"/> and <see cref="StoreAsync"/>, so a caller
    /// can walk the list and import more than one key from the same container without opening it
    /// again.
    /// </summary>
    public IReadOnlyList<ContainerKeyOption> ContainerKeys { get; private set; } = [];

    /// <summary>Prepares a freshly generated key, ed25519, for storing as <see cref="VaultEntryKind.Generated"/>.</summary>
    /// <returns>
    /// <see langword="true"/> and sets <see cref="Preview"/> when <paramref name="label"/> and
    /// <paramref name="network"/> are both given; otherwise <see langword="false"/> with
    /// <see cref="Problem"/> set and nothing prepared.
    /// </returns>
    public bool Generate(string label, string network)
    {
        Preview = null;
        _pending = null;

        if (!ValidateLabelAndNetwork(label, network, out string? canonicalNetwork))
        {
            return false;
        }

        XrplWallet wallet = XrplWallet.Generate();
        Prepare(VaultEntryKind.Generated, label, canonicalNetwork, wallet.ClassicAddress, wallet.Seed, XrplWallet.Ed25519);
        return true;
    }

    /// <summary>Prepares a key typed in by the user, for storing as <see cref="VaultEntryKind.Imported"/>.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="AddKeyDraft.Algorithm"/> is never trusted as the answer to "which curve is
    /// this": an XRPL family seed carries its own curve in its prefix, and the SDK's derivation
    /// reads it from there regardless of what is passed as the algorithm argument (the same fact
    /// <c>XrplFile.Keys.VaultSigner</c> relies on when it signs). So the seed is decoded first to
    /// learn its actual type, and that — not the draft's field — is what
    /// <see cref="AddKeyPreview.Algorithm"/> and the stored entry's algorithm end up being. A seed
    /// generated as secp256k1 previews as secp256k1 even if the screen asked for ed25519; there is
    /// no way to make one seed derive two different accounts.
    /// </para>
    /// <para>
    /// Decoding the seed is also the malformed-seed check: a string that is not a valid XRPL seed
    /// fails there with a <see cref="FormatException"/>, caught below and turned into
    /// <see cref="Problem"/> rather than left to escape the model.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> and sets <see cref="Preview"/> on success; otherwise
    /// <see langword="false"/> with <see cref="Problem"/> set and nothing prepared.
    /// </returns>
    public bool Import(AddKeyDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        Preview = null;
        _pending = null;

        if (!ValidateLabelAndNetwork(draft.Label, draft.Network, out string? canonicalNetwork))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(draft.Seed))
        {
            Problem = "Enter a seed to import.";
            return false;
        }

        string algorithm;
        XrplWallet wallet;
        try
        {
            algorithm = XrplCodec.DecodeSeed(draft.Seed).Type;
            wallet = XrplWallet.FromSeed(draft.Seed, masterAddress: null, algorithm: algorithm);
        }
        catch (FormatException)
        {
            Problem = "That does not look like a valid seed.";
            return false;
        }

        Prepare(VaultEntryKind.Imported, draft.Label, canonicalNetwork, wallet.ClassicAddress, draft.Seed, algorithm);
        return true;
    }

    /// <summary>
    /// Opens a backup container — the format shared with the Wallet application — and lists the
    /// keys inside it as <see cref="ContainerKeys"/>. Always opened as a file
    /// (<c>fromFile: true</c>): the import screen this model backs has no other way for container
    /// bytes to arrive, and a password-less container is not allowed to reach it that way — see
    /// <see cref="BackupUnpackError.PasswordlessContainerNotAllowed"/>.
    /// </summary>
    /// <remarks>
    /// The four failure outcomes <see cref="BackupContainer.TryUnpack"/> can report are returned
    /// distinctly rather than collapsed into one "could not open": a caller that only sees
    /// <see cref="Problem"/> gets the same words <see cref="BackupUnpackError"/>'s own remarks
    /// explain the reasoning for, and a caller that wants to act differently on, say, a wrong
    /// password versus a foreign file can switch on the return value instead of parsing prose.
    /// </remarks>
    /// <returns>
    /// <see cref="BackupUnpackError.None"/> and a populated <see cref="ContainerKeys"/> on
    /// success; otherwise the specific reason, with <see cref="Problem"/> set and
    /// <see cref="ContainerKeys"/> left empty.
    /// </returns>
    public BackupUnpackError OpenContainer(byte[] data, string? password)
    {
        ArgumentNullException.ThrowIfNull(data);

        Preview = null;
        _pending = null;
        ContainerKeys = [];
        _containerWallets = [];

        if (!BackupContainer.TryUnpack(data, password, fromFile: true, out BackupPayload? payload, out BackupUnpackError error))
        {
            Problem = ContainerProblem(error);
            return error;
        }

        _containerWallets = payload!.Wallets;
        ContainerKeys = [.. payload.Wallets.Select(
            wallet => new ContainerKeyOption(wallet.ClassicAddress, wallet.Algorithm, wallet.Description, wallet.Network))];
        Problem = null;
        return BackupUnpackError.None;
    }

    /// <summary>
    /// Prepares one of the keys listed in <see cref="ContainerKeys"/> for storing, under a label
    /// and a network the caller supplies rather than whatever the container itself says.
    /// </summary>
    /// <param name="index">
    /// The position of the chosen key in <see cref="ContainerKeys"/> (and, equivalently, in the
    /// container that was opened). Out of range whenever the caller offers an index
    /// <see cref="ContainerKeys"/> itself did not — including every index once
    /// <see cref="CloseContainer"/> has emptied the list — which is reported through
    /// <see cref="Problem"/> like any other failure here rather than thrown, so a screen that keeps
    /// a stale index around past a container being closed fails the same clean way any other
    /// mistaken selection does.
    /// </param>
    /// <param name="network">
    /// The network to record the key under. Required, and never defaulted from
    /// <see cref="ContainerKeyOption.Network"/>: a Wallet backup carries no network of its own,
    /// so guessing one is exactly the mistake — a testnet key filed as mainnet is an address the
    /// user does not own — that asking instead of guessing avoids.
    /// </param>
    /// <remarks>
    /// The address <see cref="ContainerKeyOption.Address"/> already names is not trusted either:
    /// this re-derives it from the container entry's own key material, the same way
    /// <see cref="Import"/> does for a typed seed, so that a container whose declared address does
    /// not match its own key is caught here rather than filed away and only surfacing as
    /// <see cref="VaultError.AddressMismatch"/> the first time it is asked to sign.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> and sets <see cref="Preview"/> on success; otherwise
    /// <see langword="false"/> with <see cref="Problem"/> set and nothing prepared.
    /// </returns>
    public bool SelectContainerKey(int index, string label, string network)
    {
        Preview = null;
        _pending = null;

        if (index < 0 || index >= _containerWallets.Count)
        {
            Problem = "That key is no longer available. Open the backup again.";
            return false;
        }

        if (!ValidateLabelAndNetwork(label, network, out string? canonicalNetwork))
        {
            return false;
        }

        BackupWalletEntry entry = _containerWallets[index];

        string algorithm;
        XrplWallet derived;
        try
        {
            algorithm = XrplCodec.DecodeSeed(entry.KeyValue).Type;
            derived = XrplWallet.FromSeed(entry.KeyValue, masterAddress: null, algorithm: algorithm);
        }
        catch (FormatException)
        {
            Problem = "This key inside the backup does not look like a valid seed.";
            return false;
        }

        Prepare(ContainerEntryKind(entry.Kind), label, canonicalNetwork, derived.ClassicAddress, entry.KeyValue, algorithm);
        return true;
    }

    /// <summary>
    /// Drops every secret <see cref="OpenContainer"/> decrypted, along with whatever
    /// <see cref="SelectContainerKey"/> most recently prepared from them, and clears
    /// <see cref="ContainerKeys"/> and <see cref="Preview"/> to match. The add-key screen calls this
    /// when it is dismissed, so a multi-key backup's seeds do not simply wait on the garbage
    /// collector to end their residency — see the type remarks on why a container is kept resident
    /// at all, and this is the deliberate end of that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the extent of what a managed <see cref="string"/> allows: dropping every reference to
    /// it is all a caller can ask, and the bytes it held remain wherever the runtime already copied
    /// them — the managed heap, possibly relocated by a compacting collection — until something
    /// overwrites that memory or the process exits. Nothing here scrubs them; this clears
    /// reachability, not the bytes themselves, the same limitation every seed held as a
    /// <see cref="string"/> elsewhere in this model already has.
    /// </para>
    /// <para>
    /// Closing is scoped to the container route: <see cref="Generate"/> and <see cref="Import"/>
    /// remain callable afterwards, and a later <see cref="OpenContainer"/> call opens a fresh
    /// container as if this one had never been opened. A key <see cref="SelectContainerKey"/>
    /// prepared but that was never passed to <see cref="StoreAsync"/> is discarded along with it —
    /// an index into <see cref="ContainerKeys"/> obtained before this call is no longer valid
    /// afterwards, and <see cref="SelectContainerKey"/> reports that through <see cref="Problem"/>
    /// rather than throwing, the same as any other index it does not recognise.
    /// </para>
    /// </remarks>
    public void CloseContainer()
    {
        _containerWallets = [];
        ContainerKeys = [];
        Preview = null;
        _pending = null;
        Problem = null;
    }

    /// <summary>
    /// Drops whatever <see cref="Generate"/>, <see cref="Import"/> or <see cref="SelectContainerKey"/>
    /// most recently prepared, without touching <see cref="ContainerKeys"/> or the container route's
    /// open state — unlike <see cref="CloseContainer"/>, which resets both. The add-key screen calls
    /// this when it switches away from whichever input route prepared the current <see cref="Preview"/>,
    /// so a person who switches tabs before storing or discarding what was prepared is never shown an
    /// address that belongs to a route they have left; a container already opened stays open so
    /// choosing a different key out of it afterwards does not require reopening the file.
    /// </summary>
    public void ClearPreview()
    {
        Preview = null;
        _pending = null;
    }

    /// <summary>
    /// Writes whatever <see cref="Generate"/>, <see cref="Import"/> or
    /// <see cref="SelectContainerKey"/> most recently prepared.
    /// </summary>
    /// <remarks>
    /// A duplicate address or a locked vault are reported through <see cref="Problem"/> rather
    /// than left to escape as a <see cref="VaultException"/>: both are outcomes the screen this
    /// model backs has words for already, and a caller that wants the failed attempt to remain
    /// prepared — unlock the vault and press "add" again with nothing retyped — gets that, because
    /// only success clears what was prepared.
    /// </remarks>
    /// <returns>
    /// The reference the vault assigned, on success. On failure, the empty string, with
    /// <see cref="Problem"/> set — never a valid reference, since every reference the vault hands
    /// out is a non-empty identifier. <see cref="LastStoredNeedsExporting"/> says whether the
    /// entry that was just stored is one the screen must offer to export.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Nothing has been prepared: none of <see cref="Generate"/>, <see cref="Import"/> or
    /// <see cref="SelectContainerKey"/> has succeeded since the last successful
    /// <see cref="StoreAsync"/> call.
    /// </exception>
    public async Task<string> StoreAsync(CancellationToken cancellationToken)
    {
        if (_pending is null)
        {
            throw new InvalidOperationException(
                "Nothing has been prepared to store. Call Generate, Import or SelectContainerKey first.");
        }

        try
        {
            VaultEntryKind stored = _pending.Kind;
            string reference = await vault.StoreAsync(_pending, cancellationToken).ConfigureAwait(false);
            LastStoredNeedsExporting = ExportNeed.ForKind(stored);
            Preview = null;
            _pending = null;
            Problem = null;
            return reference;
        }
        catch (VaultException exception)
        {
            Problem = StoreProblem(exception.Error);
            return string.Empty;
        }
    }

    private void Prepare(VaultEntryKind kind, string label, string network, string address, string secret, string algorithm)
    {
        Preview = new AddKeyPreview(address, algorithm);
        _pending = new NewVaultEntry(kind, label, network, address, secret, algorithm);
        Problem = null;
    }

    /// <summary>
    /// Checks the label and the network, and hands back the network in the form it is stored under:
    /// <see cref="XrplFileLink.TryNormalizeNetwork"/>'s, applied after trimming, so a value that
    /// differs from a known network only in case or padding is stored as that network and one no link
    /// can name is refused instead of being filed under a name nothing else will ever match.
    /// </summary>
    private bool ValidateLabelAndNetwork(string label, string network, [NotNullWhen(true)] out string? canonicalNetwork)
    {
        canonicalNetwork = null;

        if (string.IsNullOrWhiteSpace(label))
        {
            Problem = "Enter a label.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(network))
        {
            Problem = "Choose a network.";
            return false;
        }

        if (!XrplFileLink.TryNormalizeNetwork(network.Trim(), out canonicalNetwork))
        {
            Problem = NetworkChoices.NotANetwork(network);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The <see cref="VaultEntryKind"/> a container entry's own <see cref="BackupWalletEntry.Kind"/>
    /// names, or <see cref="VaultEntryKind.Imported"/> when it names none or names something this
    /// build does not recognise. Tolerant on purpose, matching why the field is a string rather
    /// than the enum on the wire: a Wallet-written container never sets it at all, and
    /// <see cref="VaultEntryKind.Imported"/> — "an account they had before and will have
    /// afterwards" — is true of every key arriving through a container, regardless of what it was
    /// called wherever it came from.
    /// </summary>
    /// <remarks>
    /// <see cref="Enum.IsDefined{TEnum}(TEnum)"/> is what makes "does not recognise" mean what it
    /// says. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> alone accepts numeric text
    /// and hands back the number cast to the enum, so a container carrying <c>"kind": "7"</c> —
    /// which nothing stops an attacker, or a future version of the other product, from writing —
    /// stored <c>(VaultEntryKind)7</c>. The key list then rendered it as the bare text "7" and it
    /// matched no arm of any switch anywhere. A container's field is untrusted input; only a name
    /// this build actually declares is a kind.
    /// </remarks>
    private static VaultEntryKind ContainerEntryKind(string? kind) =>
        Enum.TryParse(kind, ignoreCase: false, out VaultEntryKind parsed) && Enum.IsDefined(parsed)
            ? parsed
            : VaultEntryKind.Imported;

    private static string ContainerProblem(BackupUnpackError error) => error switch
    {
        BackupUnpackError.NotOurContainer => "This file is not a backup this application can open.",
        BackupUnpackError.WrongPassword => "That password does not open this backup.",
        BackupUnpackError.PasswordlessContainerNotAllowed =>
            "This backup carries no password of its own and cannot be imported from a file.",
        BackupUnpackError.IterationsOutOfRange => "This backup's parameters are outside what this build accepts.",
        _ => "This backup could not be opened.",
    };

    private static string StoreProblem(VaultError error) => error switch
    {
        VaultError.Locked => "Unlock the vault before adding a key.",
        VaultError.DuplicateAddress => "The vault already holds this address on this network.",
        VaultError.Degraded => "The vault found damage that has not been acknowledged yet. Acknowledge it first.",
        VaultError.StorageFailed => "The vault's storage did not respond. Try again in a moment.",
        _ => "The key could not be stored.",
    };
}
